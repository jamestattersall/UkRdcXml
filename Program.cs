using Dapper;
using Dapper.Contrib.Extensions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;
using System.Net;
using System.Text.Json;
using UKRDC;

var currentSettings = new AppSettings().Get(args);

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

DateTime whenPrepared = DateTime.Now;
int sendingFacilityId = currentSettings.SFID;

using SqlConnection connection = new SqlConnection(cb.ConnectionString);

// Query data with Dapper
var pats = await connection.QueryAsync<Patient>($"SELECT * FROM dbo.PatientsToExport({sendingFacilityId})");
var subm = await connection.QuerySingleOrDefaultAsync<Submission>($"SELECT top 1 * FROM dbo.Submissions WHERE SendingFacilityId={sendingFacilityId} ORDER BY Id desc");
var sf = await connection.QuerySingleOrDefaultAsync<SendingFaciliy>($"SELECT TOP 1 * From dbo.SendingFacilities where Id = {sendingFacilityId}");

if (sf is null)
{
    Console.WriteLine($"No facility has id = {sendingFacilityId}");
    return;
}

int n = pats.Count();
Console.WriteLine($"Exporting XML for {sf.Name}");
if (n == 0)
{
    Console.WriteLine($"No data to export");
    return;
}

using SqlCommand command = new SqlCommand(query, connection);
command.Parameters.AddWithValue(currentSettings.SendingFacilityIdParameter, sendingFacilityId);
command.Parameters.Add(currentSettings.PatientIdParameter, SqlDbType.Int);
command.Parameters.AddWithValue(currentSettings.StartParameter, subm.Start);
command.Parameters.AddWithValue(currentSettings.EndParameter, subm.Stop);

try
{
    await connection.OpenAsync();
} catch(Exception e)
{
    throw new Exception("Invalid connection string, cannot connect to database",e);
}

using EncryptXml x = new(command, sf.Code, subm.PopulatedTables, publicKeyPath, outputPath, subm.Id);

var prog = new ConsoleUtilities.Progress(20);
prog.WriteProgressBar(0);
float i = 0;
foreach (Patient p in pats)
{
    i++;
    prog.WriteProgressBar(i / (float)n);
    await x.ExportPgpXmlAsync(p.PatientId, p.NhsNumber);
}
Console.WriteLine();
Console.WriteLine($"Exported {n} XML files.");

subm.GeneratedXml = DateTime.Now;
subm.NPatients = n;

//update to database using dapper.contrib.extensions
connection.Update(subm);

class Patient
{
    public int PatientId = 0;
    public string NhsNumber = "";
}

public class SendingFaciliy
{
    public int Id { get; set;  }
    public string Name { get;  set; }
    public string Code { get; set; }
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
    public string XmlQuery { get; set; } = "SELECT dbo.GenerateXmlV2(@sendingFacilityId,@patientId,@start,@end)";
    public string PublicKeyFile { get; set; } = "C:\\Users\\james\\OneDrive\\Documents\\JET_public.asc";
    public string OutputFileDirectory { get; set; } = "C:\\Users\\james\\Downloads\\XML";
    public string PatientIdParameter { get; set; } = "@patientId";
    public string SendingFacilityIdParameter { get; set; } = "@sendingFacilityId";
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
            string initialJson = JsonSerializer.Serialize(defaultSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settingsFilePath, initialJson);
        } 
        
        // 3. Load settings via Microsoft.Extensions.Configuration
        IConfiguration rootConfig = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile(SettingsFileName, optional: false, reloadOnChange: true)
            .AddCommandLine(args)
            .Build();
        
        // Bind JSON configuration structure to a strongly-typed class instance
        return  rootConfig.Get<AppSettings>() ?? new AppSettings();
    }
}