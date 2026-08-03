using Microsoft.Data.SqlClient;
using PgpCore;
using Serilog;
using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Xml;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace UkrdcPgpXml
{
    internal class EncryptXml : IDisposable
    {
        private const int BATCH_LENGTH = 4096;

        public EncryptXml(SqlCommand xmlCommand, string sendingFacilityCode, DateTime whenPrepared, string publicKeyPath, string outputPath, int submissionId, ILogger logger, string patientParameterName = "@patientId", string outputFileExtension = "xml.pgp")
        {
            var nm = Assembly.GetExecutingAssembly().GetName();
            if (File.Exists(publicKeyPath))
            {
                try
                {
                    var encryptionKeys = new EncryptionKeys(new FileInfo(publicKeyPath));
                    _pgp = new PGP(encryptionKeys);
                }
                catch (PgpCoreException ex)
                {
                    logger.Error(ex, $"No valid encryption keys in file {publicKeyPath}, {ex.Message}");
                   
                }
            }
            else
            {
                FileNotFoundException ex = new();
                logger.Error(ex, $"Key file {publicKeyPath} not found");
                throw ex;
            }

            if (Directory.Exists(outputPath))
            {
                _outputPath = outputPath;
            }
            else
            {
                throw new FileNotFoundException($"Directory for encrypted files {outputPath} not found");
            }

            _strPrepared = whenPrepared.ToString("s");
            _command = xmlCommand;
            _patientParameter = xmlCommand.Parameters[patientParameterName];
            _outputPath = outputPath;
            _fileNameStart = $"{sendingFacilityCode}_{submissionId:000000}_";
            _outputFileExtension = outputFileExtension;
            _nodesToAdd = $@"<SendingFacility channelName=""{nm.Name} {nm.Version}"" time=""{_strPrepared}"" schemaVersion=""4.2.0"">{sendingFacilityCode}</SendingFacility><SendingExtract>UKRDC</SendingExtract>";

            // REMOVED class-level Pipe definitions from here

            _xmlWriterSettings = new XmlWriterSettings
            {
                Async = true,
                Indent = true,
                Encoding = new UTF8Encoding(false),
                ConformanceLevel = ConformanceLevel.Fragment,
                CheckCharacters = false
            };

            _log = logger;
        }
        private static readonly PipeOptions _pipeOptions = new(
            pool: MemoryPool<byte>.Shared,          // Reuses memory blocks across iterations
            pauseWriterThreshold: 65536,            // Pause writing if 64KB is unread
            resumeWriterThreshold: 32768,           // Resume writing when unread drops to 32KB
            useSynchronizationContext: false        // Prevents UI thread context switching overheads
        );
        private readonly string _strPrepared;
        private readonly string _outputPath;
        private readonly string _outputFileExtension;
        private readonly string _fileNameStart;
        private readonly string _nodesToAdd;
        private readonly PGP _pgp;
        private readonly SqlCommand _command;
        private readonly SqlParameter _patientParameter;
        private readonly XmlWriterSettings _xmlWriterSettings;
        private readonly ILogger _log;

        public async Task ExportPgpXmlAsync(int patientId, string identifier)
        {
            if(_pgp == null)
            {
                return;
            }
            string finalPath = Path.Combine(_outputPath, $"{_fileNameStart}_{identifier}.{_outputFileExtension}");

            var pipe = new Pipe(_pipeOptions);
            await using Stream pipeReaderStream = pipe.Reader.AsStream();
            await using Stream pipeWriterStream = pipe.Writer.AsStream(leaveOpen: true);

            await using FileStream targetFileStream = new(
                finalPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: BATCH_LENGTH,
                useAsync: true);

            Task encryptionTask = _pgp.EncryptStreamAsync(pipeReaderStream, targetFileStream);
            Exception? processingException = null;

            try
            {
                _patientParameter.Value = patientId;
                using XmlReader xmlReader = await _command.ExecuteXmlReaderAsync();

                using XmlWriter xmlWriter = XmlWriter.Create(pipeWriterStream, _xmlWriterSettings);
                bool isRoot = true;

                while (await xmlReader.ReadAsync())
                {
                    switch (xmlReader.NodeType)
                    {
                        case XmlNodeType.Element:
                            if (isRoot)
                            {
                                string rootLocalName = xmlReader.LocalName;
                                await xmlWriter.WriteStartElementAsync("ns0", rootLocalName, "http://www.rixg.org.uk");
                                AddAttributesFromReader(xmlReader, xmlWriter);
                                await xmlWriter.WriteRawAsync(_nodesToAdd);
                                isRoot = false;
                            }
                            else
                            {
                                await xmlWriter.WriteStartElementAsync(xmlReader.Prefix, xmlReader.LocalName, xmlReader.NamespaceURI);
                                AddAttributesFromReader(xmlReader, xmlWriter);
                            }

                            if (xmlReader.IsEmptyElement)
                            {
                                await xmlWriter.WriteEndElementAsync();
                            }
                            break;

                        case XmlNodeType.EndElement:
                            await xmlWriter.WriteEndElementAsync();
                            break;

                        case XmlNodeType.Text:
                        case XmlNodeType.CDATA:
                        case XmlNodeType.Comment:
                            string value = await xmlReader.GetValueAsync();
                            if (xmlReader.NodeType == XmlNodeType.Text) await xmlWriter.WriteStringAsync(value);
                            else if (xmlReader.NodeType == XmlNodeType.CDATA) await xmlWriter.WriteCDataAsync(value);
                            else await xmlWriter.WriteCommentAsync(value);
                            break;
                    }
                }

                await xmlWriter.FlushAsync();
            }
            catch (Exception ex)
            {
                processingException = ex;
                _log.Error($"Error processing XML for patient {patientId} with identifier {identifier}: {ex.Message}");
            }
            finally
            {
                if (processingException != null)
                {
                    await pipe.Writer.CompleteAsync(processingException);
                }
                else
                {
                    await pipe.Writer.CompleteAsync();
                }
            }

            await encryptionTask;
            await pipe.Reader.CompleteAsync();

        }

        private static void AddAttributesFromReader(XmlReader reader, XmlWriter writer)
        {
            if (reader.HasAttributes)
            {
                while (reader.MoveToNextAttribute())
                {
                    writer.WriteAttributeString(reader.Prefix, reader.LocalName, reader.NamespaceURI, reader.Value);
                }
                reader.MoveToElement();
            }
        }

        public void Dispose()
        {
            _pgp?.Dispose();           
        }
    }
}
