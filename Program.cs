using Dapper;
using Dapper.Contrib.Extensions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;
using System.Text.Json;
using UkrdcPgpXml;


const int TIMEOUT_MS = 60000;

var   currentSettings = new AppSettings().Get(args);

var cb = new SqlConnectionStringBuilder()
{
    DataSource = currentSettings.Server,
    InitialCatalog = currentSettings.Database,
    IntegratedSecurity = true,
    TrustServerCertificate = true,
};

string outputPath = currentSettings.OutputFileDirectory;
string publicKeyPath = currentSettings.PublicKeyFile;
string query = currentSettings.XmlQuery;

int sendingFacilityId = currentSettings.SFID;

using SqlConnection connection = new(cb.ConnectionString);

try
{
    await connection.OpenAsync();
}
catch (Exception e)
{
    Console.WriteLine("Invalid connection string, can't connect to database");
    Console.WriteLine(e.Message);
    Thread.Sleep(TIMEOUT_MS);
    return;
}

// Query data with Dapper
var sf = await connection.QuerySingleOrDefaultAsync<SendingFacility>($"SELECT TOP 1 * From dbo.SendingFacilities where Id = {sendingFacilityId}");
if (sf is null)
{
    Console.WriteLine($"No facility has id = {sendingFacilityId}");
    Thread.Sleep(TIMEOUT_MS);
    return;
}

var subm = await connection.QuerySingleOrDefaultAsync<Submission>($"SELECT top 1 * FROM dbo.Submissions WHERE SendingFacilityId={sendingFacilityId} ORDER BY Id desc");
if (subm is null)
{
    Console.WriteLine($"No submission found for {sf.Name}");
    Thread.Sleep(TIMEOUT_MS);
    return;
}

var pats = await connection.QueryAsync<Patient>($"SELECT * FROM dbo.PatientsToExport({subm.Id})");
int n = pats.Count();
if (n == 0)
{
    Console.WriteLine($"No data to export for {sf.Name}");
    Thread.Sleep(TIMEOUT_MS);
    return;
}

Console.WriteLine($"Exporting XML for {sf.Name}");

using SqlCommand command = new(query, connection);
command.Parameters.AddWithValue(currentSettings.SubmissionIdParameter, subm.Id);
command.Parameters.Add(currentSettings.PatientIdParameter, SqlDbType.Int);
command.Parameters.AddWithValue(currentSettings.StartParameter, subm.Start);
command.Parameters.AddWithValue(currentSettings.EndParameter, subm.Stop);

try
{
    using EncryptXml x = new(command, sf.Code, subm.PopulatedTables, publicKeyPath, outputPath, subm.Id);

    var prog = new ConsoleUtilities.Progress(20);
    prog.WriteProgressBar(0);
    float i = 0;
    foreach (Patient p in pats)
    {
        i++;
        prog.WriteProgressBar(i / (float)n);
        await x.ExportPgpXmlAsync(p.PatientId, p.Identifier);
    }
    Console.WriteLine(); 
    Console.WriteLine($"{n} encrypted XML files generated");
    Console.WriteLine($"Saved to  : {outputPath}");
    FileInfo fi = new(publicKeyPath);
    Console.WriteLine($"Public key: {fi.Name}");
}
catch (Exception e)
{
    Console.WriteLine($"Error from EncryptXml object: {e.Message}");
    Thread.Sleep(TIMEOUT_MS);
    return;
}

subm.GeneratedXml = DateTime.Now;
subm.NPatients = n;

//update to database using dapper.contrib.extensions
try
{
    connection.Update(subm);
}
catch (Exception e)
{
    Console.WriteLine($"Error updating submission record: {e.Message}");
}

Thread.Sleep(TIMEOUT_MS);
return;

namespace UkrdcPgpXml
{
    class Patient
    {
        public int PatientId = 0;

        public string Identifier = "";
    }

    public class SendingFacility
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Code { get; set; } = "";
    }

    class Submission
    {
        public int Id { get; }
        public DateTime PopulatedTables { get; set; }
        public DateTime Start { get; }
        public DateTime Stop { get; }
        public DateTime? GeneratedXml { get; set; }
        public int NPatients { get; set; }

    }

    public class AppSettings
    {
        public string Server { get; set; } = "(local)";
        public string Database { get; set; } = "UKRDC";
        public string XmlQuery { get; set; } = "SELECT dbo.GenerateXmlV2(@submissionId, @patientId,@start,@end)";
        public string PublicKeyFile { get; set; } = "[path to the UKRR public key file]";
        public string OutputFileDirectory { get; set; } = "[path to your output directory]";
        public string PatientIdParameter { get; set; } = "@patientId";
        public string SubmissionIdParameter { get; set; } = "@submissionId";
        public string StartParameter { get; set; } = "@start";
        public string EndParameter { get; set; } = "@end";
        public string OutputFileExtension { get; set; } = "xml.pgp";
        public int SFID { get; set; } = -1;

        public AppSettings Get(string[] args)
        {
            const string SettingsFileName = "appsettings.json";
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string settingsFilePath = Path.Combine(baseDirectory, SettingsFileName);

            // 2. Initialize default configuration file if it does not exist
            if (!File.Exists(settingsFilePath))
            {
                var defaultSettings = this;
                JsonSerializerOptions options = new() { WriteIndented = true };
                string initialJson = JsonSerializer.Serialize(defaultSettings, options);
                File.WriteAllText(settingsFilePath, initialJson);
            }

            // 3. Load settings via Microsoft.Extensions.Configuration
            IConfiguration rootConfig = new ConfigurationBuilder()
                .SetBasePath(baseDirectory)
                .AddJsonFile(SettingsFileName, optional: false, reloadOnChange: true)
                .AddCommandLine(args)
                .Build();

            // Bind JSON configuration structure to a strongly-typed class instance
            return rootConfig.Get<AppSettings>() ?? new AppSettings();
        }
    }
}