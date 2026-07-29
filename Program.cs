using Dapper;
using Dapper.Contrib.Extensions;
using Microsoft.Data.SqlClient;
using System.Data;
using UKRDC;

var currentSettings = ConfigSettings.GetSettings(args);

string connectionString = currentSettings.ConnectionString;
string outputPath = currentSettings.OutputFileDirectory;
string publicKeyPath = currentSettings.PublicKeyFile;
string query = currentSettings.Query;

DateTime whenPrepared = DateTime.Now;
int sendingFacilityId = currentSettings.SFID;

using SqlConnection connection = new SqlConnection(connectionString);

// Query data with Dapper
var pats = await connection.QueryAsync<Patient>($"SELECT * FROM dbo.PatientsToExport({sendingFacilityId})");
var subm = await connection.QuerySingleOrDefaultAsync<Submission>($"SELECT top 1 * FROM dbo.Submissions WHERE SendingFacilityId={sendingFacilityId} ORDER BY Id desc");
var sf = await connection.QuerySingleOrDefaultAsync<SendingFaciliy>($"SELECT TOP 1 * From dbo.SendingFacilities where Id = {sendingFacilityId}");

if (sf is null)
{
    Console.WriteLine($"No facility has id = {sendingFacilityId}");
    return;
}

float n = pats.Count();
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

await connection.OpenAsync();

using EncryptXml x = new(command, sf.Code, subm.PopulatedTables, publicKeyPath, outputPath, subm.Id);

var prog = new ConsoleUtilities.Progress(20);
prog.WriteProgressBar(0);
float i = 0;
foreach (Patient p in pats)
{
    i++;
    prog.WriteProgressBar(i / n);
    await x.ExportPgpXmlAsync(p.PatientId, p.NhsNumber);
}
Console.WriteLine();
Console.WriteLine($"Exported {pats.Count()} XML files.");

subm.GeneratedXml = DateTime.Now;
subm.NPatients = pats.Count();

//update using dapper.contrib.extensions
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