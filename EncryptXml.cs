using Microsoft.Data.SqlClient;
using PgpCore;
using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Xml;

namespace UkrdcPgpXml
{
    internal class EncryptXml : IDisposable
    {
        // Calling code is responsible for controlling database interaction
        // Depends on the command parameter already created with SqlParameters, commandText and open SqlConnection set
        // There must be an SqlParameter to accept the patientId, all other parameters set by the calling code.
        // Command must return valid XML.
        public EncryptXml(SqlCommand xmlCommand, string sendingFaciltyCode, DateTime whenPrepared, string publicKeyPath, string outputPath, int batch, string patientParameterName = "@patientId", string outputFileExtension = "xml.pgp")
        {
            var nm = Assembly.GetExecutingAssembly().GetName();

            _strPrepared = whenPrepared.ToString("s");
            _command = xmlCommand;
            _sendingFacilityCode = sendingFaciltyCode;
            _fileNameStart = $"{sendingFaciltyCode}_{batch:000000}_";
            _patientParameterName = patientParameterName;
            _outputFileExtension = outputFileExtension;
            _nodesToAdd = $@"<SendingFacility channelName=""{nm.Name} {nm.Version}"" time=""{_strPrepared}"" schemaVersion=""4.2.0"">{_sendingFacilityCode}</SendingFacility><SendingExtract>UKRDC</SendingExtract>";
            _xmlWriterSettings = new XmlWriterSettings
            {
                Async = true,
                Indent = true,
                Encoding = new UTF8Encoding(false),
                ConformanceLevel = ConformanceLevel.Fragment
            };

            // 1. Initialise the reusable Pipe and Stream wrappers once
            _pipe = new Pipe();
            _pipeReaderStream = _pipe.Reader.AsStream();
            _pipeWriterStream = _pipe.Writer.AsStream();

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
                throw (new FileNotFoundException($"Key file {publicKeyPath} not found"));
            }
            if (Directory.Exists(outputPath))
            {
                _outputPath = outputPath;
            }
            else
            {
                throw (new FileNotFoundException($"Directory for encrypted files {outputPath} not found"));
            }
        }

        private readonly string _strPrepared;
        private readonly PGP _pgp;
        private readonly SqlCommand _command;
        private readonly string _outputPath;
        private readonly string _outputFileExtension;
        private readonly string _fileNameStart;
        private readonly string _sendingFacilityCode;
        private readonly string _patientParameterName;
        private readonly string _nodesToAdd;
        private readonly XmlWriterSettings _xmlWriterSettings;

        // Reusable pipe infrastructure
        private readonly Pipe _pipe;
        private readonly Stream _pipeReaderStream;
        private readonly Stream _pipeWriterStream;

        public async Task ExportPgpXmlAsync(int patientId, string identifier)
        {
            string filename = Path.Combine(_outputPath, $"{_fileNameStart}_{identifier}.{_outputFileExtension}");

            using FileStream targetFileStream = new(
                filename,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);

            // Start reading/encrypting asynchronously 
            Task encryptionTask = _pgp.EncryptStreamAsync(_pipeReaderStream, targetFileStream);

            try
            {
                _command.Parameters[_patientParameterName].Value = patientId;
                using XmlReader xmlReader = await _command.ExecuteXmlReaderAsync();

                // Note: Do NOT use 'using' on _pipeWriterStream as we want to keep it alive across iterations
                using StreamWriter writer = new(_pipeWriterStream, Encoding.UTF8, bufferSize: 4096, leaveOpen: true);
                using XmlWriter xmlWriter = XmlWriter.Create(writer, _xmlWriterSettings);

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
                                AddAttributesFromreader(xmlReader, xmlWriter);
                                await xmlWriter.WriteRawAsync(_nodesToAdd);
                                isRoot = false;
                            }
                            else
                            {
                                await xmlWriter.WriteStartElementAsync(xmlReader.Prefix, xmlReader.LocalName, xmlReader.NamespaceURI);
                                AddAttributesFromreader(xmlReader, xmlWriter);
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
                            await xmlWriter.WriteStringAsync(await xmlReader.GetValueAsync());
                            break;

                        case XmlNodeType.CDATA:
                            await xmlWriter.WriteCDataAsync(await xmlReader.GetValueAsync());
                            break;

                        case XmlNodeType.Comment:
                            await xmlWriter.WriteCommentAsync(await xmlReader.GetValueAsync());
                            break;
                    }

                    await xmlWriter.FlushAsync();
                    await writer.FlushAsync();
                }
            }
            finally
            {
                // 2. Complete the WRITER side of the pipe explicitly to trigger EOF for the reader
                await _pipe.Writer.CompleteAsync();
            }

            // 3. Wait for PgpCore to finish reading everything and complete its task
            await encryptionTask;

            // 4. Complete the READER side explicitly to satisfy the two-way completion rule
            await _pipe.Reader.CompleteAsync();

            // 5. Reset the pipe state machine and memory buffers for the next loop iteration
            _pipe.Reset();
        }

        private static void AddAttributesFromreader(XmlReader reader, XmlWriter writer)
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
            ((IDisposable)_pgp).Dispose();

            // 6. Dispose the long-lived stream adapters
            _pipeReaderStream.Dispose();
            _pipeWriterStream.Dispose();
        }
    }
}
