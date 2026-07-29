using Microsoft.Data.SqlClient;
using PgpCore;
using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Xml;

namespace UKRDC
{
    internal class EncryptXml : IDisposable
    {
        //calling code is reponsible for controlling database interaction
        //depends on the command parameter already created with SqlParameters, commandText and open SqlConnection set
        //there must be an SqlParameter to accept the patientId, all other parameters set by the calling code.
        //Command must return valid XML.
        public EncryptXml(SqlCommand xmlCommand, string sendingFaciltyCode, DateTime whenPrepared, string publicKeyPath, string outputPath, int batch, string patientParameterName = "@patientId", string outputFileExtension="xml.pgp")
        {
            var nm = Assembly.GetExecutingAssembly().GetName();

            Version = $"{nm.Name} {nm.Version}";
            _strPrepared = whenPrepared.ToString("s");
            // 1. Setup PGP Configurations
            _command = xmlCommand;
            _sendingFacilityCode = sendingFaciltyCode;
            _fileNameStart = $"{sendingFaciltyCode}_{batch.ToString("000000")}_";
            _patientParameterName= patientParameterName;
            _outputFileExtension = outputFileExtension;

            _xmlWriterSettings = new XmlWriterSettings
            {
                Async = true,
                Indent = true,
                Encoding = new UTF8Encoding(false),
                ConformanceLevel = ConformanceLevel.Fragment
            };
            if (File.Exists(publicKeyPath))
            {
                try
                {
                var encryptionKeys = new EncryptionKeys(new FileInfo(publicKeyPath));
                _pgp = new PGP(encryptionKeys);

                }catch(PgpCoreException ex)
                {
                   throw new Exception($"No valid encryption keys in file {publicKeyPath}, {ex.Message}" );
                }
            }else
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
        public string Version { get; init; }
        private readonly string _strPrepared;
        private readonly PGP _pgp;
        private readonly SqlCommand _command;
        private readonly string _outputPath;
        private readonly string _outputFileExtension;
        private readonly string _fileNameStart;
        private readonly string _sendingFacilityCode;
        private readonly string _patientParameterName;
        private readonly XmlWriterSettings _xmlWriterSettings;

        public async Task ExportPgpXmlAsync(int patientId, string NhsNunber)
        {
            Pipe pipe = new Pipe();
            Stream pipeReaderStream = pipe.Reader.AsStream();
            Stream pipeWriterStream = pipe.Writer.AsStream();

            string filename = Path.Combine(_outputPath, $"{_fileNameStart}_{NhsNunber}.{_outputFileExtension}");
            
            using FileStream targetFileStream = new FileStream(
                filename,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);

            // Start the PGP encryption engine processing the stream in the background
            // It reads from the Pipe's Reader and pushes encrypted data straight onto the Disk File
            Task encryptionTask = _pgp.EncryptStreamAsync(pipeReaderStream, targetFileStream);

            // generate XML chunks directly into the Pipe's Writer
            try
            {
                _command.Parameters[_patientParameterName].Value = patientId;
                using XmlReader xmlReader = await _command.ExecuteXmlReaderAsync();


                // Writer feeds directly into the pipeWriterStream
                using StreamWriter writer = new StreamWriter(pipeWriterStream, Encoding.UTF8, bufferSize: 4096, leaveOpen: true);
                using XmlWriter xmlWriter = XmlWriter.Create(writer, _xmlWriterSettings);

                //these nodes are required in each XML file for UKRDC, so we will add them to the root element of the database XML
                var addNodes = $@"
<SendingFacility channelName=""{Version}"" time=""{_strPrepared}"" schemaVersion=""4.2.0"">{_sendingFacilityCode}</SendingFacility>
<SendingExtract>UKRDC</SendingExtract>";

                bool isRoot = true;
                while (await xmlReader.ReadAsync())
                {
                    switch (xmlReader.NodeType)
                    {
                        case XmlNodeType.Element:
                            if (isRoot)
                            {//Intercept the database root element
                                string rootLocalName = xmlReader.LocalName;

                                //add namespace required for UKRDC
                                await xmlWriter.WriteStartElementAsync("ns0", rootLocalName, "http://www.rixg.org.uk");
                                //add additional attributes if required for UKRDC
                                //await xmlWriter.WriteAttributeStringAsync("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");                        // 3. Copy any existing attributes from the database root
                                //await xmlWriter.WriteAttributeStringAsync("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
                                //add additional nodes required for UKRDC
                                await xmlWriter.WriteRawAsync(addNodes);
                                isRoot = false;
                            }
                            else
                            { // Pass-through child elements as they are
                               await xmlWriter.WriteStartElementAsync(xmlReader.Prefix, xmlReader.LocalName, xmlReader.NamespaceURI);
                                
                            }
                            if (xmlReader.HasAttributes)
                            {
                                while (xmlReader.MoveToNextAttribute())
                                {
                                    await xmlWriter.WriteAttributeStringAsync(xmlReader.Prefix, xmlReader.LocalName, xmlReader.NamespaceURI, xmlReader.Value);
                                }
                                xmlReader.MoveToElement();
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
                await pipeWriterStream.DisposeAsync();
            }

            await encryptionTask;
        }

        public void Dispose()
        {
            ((IDisposable)_pgp).Dispose();
        }
    }

}
