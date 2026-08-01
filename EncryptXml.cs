using Microsoft.Data.SqlClient;
using PgpCore;
using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Xml;
using Serilog;

namespace UkrdcPgpXml
{
    internal class EncryptXml : IDisposable
    {
        private const int BATCH_LENGTH = 4096;

        public EncryptXml(SqlCommand xmlCommand, string sendingFacilityCode, DateTime whenPrepared, string publicKeyPath, string outputPath, int submissionId, ILogger? logger = null, string patientParameterName = "@patientId", string outputFileExtension = "xml.pgp")
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
                    throw new Exception($"No valid encryption keys in file {publicKeyPath}, {ex.Message}");
                }
            }
            else
            {
                throw new FileNotFoundException($"Key file {publicKeyPath} not found");
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

            _pipe = new Pipe();
            _pipeReaderStream = _pipe.Reader.AsStream();
            _pipeWriterStream = _pipe.Writer.AsStream(true);

            _xmlWriterSettings = new XmlWriterSettings
            {
                Async = true,
                Indent = true,
                Encoding = new UTF8Encoding(false),
                ConformanceLevel = ConformanceLevel.Fragment,
                CheckCharacters = false
            };
        }

        private readonly string _strPrepared;
        private readonly string _outputPath;
        private readonly string _outputFileExtension;
        private readonly string _fileNameStart;
        private readonly string _nodesToAdd;
        private readonly PGP _pgp;
        private readonly SqlCommand _command;
        private readonly SqlParameter _patientParameter;
        private readonly Pipe _pipe;
        private readonly Stream _pipeReaderStream;
        private readonly Stream _pipeWriterStream;
        private readonly XmlWriterSettings _xmlWriterSettings;

        public async Task ExportPgpXmlAsync(int patientId, string identifier)
        {
            string finalPath = Path.Combine(_outputPath, $"{_fileNameStart}_{identifier}.{_outputFileExtension}");

            await using FileStream targetFileStream = new(
                finalPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: BATCH_LENGTH,
                useAsync: true);

            Task encryptionTask = _pgp.EncryptStreamAsync(_pipeReaderStream, targetFileStream);
            Exception? processingException = null;

            try
            {
                _patientParameter.Value = patientId;
                using XmlReader xmlReader = await _command.ExecuteXmlReaderAsync();

                XmlWriter? xmlWriter = null;
                try
                {
                    xmlWriter = XmlWriter.Create(_pipeWriterStream, _xmlWriterSettings);
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

                    //Explicitly push remaining buffer fragments to the pipe on success
                    await xmlWriter.FlushAsync();
                }
                finally
                {
                    // Dispose local writer instance without flushing if an exception happened
                    xmlWriter?.Dispose();
                }
            }
            catch (Exception ex)
            {
                processingException = ex;
                throw;
            }
            finally
            {
                if (processingException != null)
                {
                    await _pipe.Writer.CompleteAsync(processingException);
                }
                else
                {
                    await _pipe.Writer.CompleteAsync();
                }
            }

            await encryptionTask;
            await _pipe.Reader.CompleteAsync();
            _pipe.Reset();
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
            _pgp.Dispose();
            _pipeReaderStream.Dispose();
            _pipeWriterStream.Dispose();
        }
    }
}
