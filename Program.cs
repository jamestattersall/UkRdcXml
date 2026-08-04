using Dapper;
using Dapper.Contrib.Extensions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Serilog;
using System.Data;
using System.Text.Json;
using UkrdcPgpXml;
using static System.Runtime.InteropServices.JavaScript.JSType;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        const int TIMEOUT_MS = 60000;

        var currentSettings = new AppSettings().Get(args);

        // 1. Initialise Serilog with structured engine targets
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(currentSettings.LogFile, rollingInterval: RollingInterval.Day)
            .CreateLogger();

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

        await using SqlConnection connection = new(cb.ConnectionString);

        try
        {
            await connection.OpenAsync();
        }
        catch (Exception e)
        {
            Log.Error(e, "Invalid connection string, can't connect to database");
            await Task.Delay(TIMEOUT_MS);
            await Log.CloseAndFlushAsync();
            return 1;
        }

        // Parameterised queries to prevent SQL parsing errors and injection vulnerabilities
        var sf = await connection.QuerySingleOrDefaultAsync<SendingFacility>(
            "SELECT TOP 1 * FROM dbo.SendingFacilities WHERE Id = @Id",
            new { Id = sendingFacilityId });

        if (sf is null)
        {
            Log.Error("No facility found matching ID: {SendingFacilityId}", sendingFacilityId);
            await Task.Delay(TIMEOUT_MS);
            await Log.CloseAndFlushAsync();
            return 1;
        }

        var subm = await connection.QuerySingleOrDefaultAsync<Submission>(
            "SELECT TOP 1 * FROM dbo.Submissions WHERE SendingFacilityId = @SFID ORDER BY Id DESC",
            new { SFID = sendingFacilityId });

        if (subm is null)
        {
            Log.Error("No submission history records found for: {FacilityName}", sf.Name);
            await Task.Delay(TIMEOUT_MS);
            await Log.CloseAndFlushAsync();
            return 1;
        }

        var pats = (await connection.QueryAsync<Patient>(
            "SELECT PatientId, Identifier FROM dbo.PatientsToExport(@SubId)",
            new { SubId = subm.Id })).ToList();

        int n = pats.Count;
        if (n == 0)
        {
            Log.Error("No payload data queue elements ready to export for facility: {FacilityName}", sf.Name);
            await Task.Delay(TIMEOUT_MS);
            await Log.CloseAndFlushAsync();
            return 1;
        }

        Log.Information("Starting XML export for: {FacilityName}", sf.Name);

        using SqlCommand command = new(query, connection);
        command.Parameters.AddWithValue(currentSettings.SubmissionIdParameter, subm.Id);
        command.Parameters.Add(currentSettings.PatientIdParameter, SqlDbType.Int);
        command.Parameters.AddWithValue(currentSettings.StartParameter, subm.Start);
        command.Parameters.AddWithValue(currentSettings.EndParameter, subm.Stop);

        EncryptXml? x = null;
        try
        {
            x = new(
               command,
               sf.Code,
               subm.PopulatedTables,
               publicKeyPath,
               outputPath,
               subm.Id,
               logger: Log.ForContext<EncryptXml>()
           );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error initializing EncryptXml");
            await Task.Delay(TIMEOUT_MS);
            await Log.CloseAndFlushAsync();
            x = null;
            return 1;
        }

        if (x == null)
        {
            Log.Error("EncryptXml initialization failed, exiting");
            await Task.Delay(TIMEOUT_MS);
            await Log.CloseAndFlushAsync();
            return 1;
        }
        else
        {
            try
            {
                var prog = new ConsoleUtilities.Progress(20);
                prog.WriteProgressBar(0);
                float i = 0;
                int errors = 0;
                foreach (Patient p in pats)
                {
                    i++;
                    prog.WriteProgressBar(i / n);

                    try
                    {
                        await x.ExportPgpXmlAsync(p.PatientId, p.Identifier);
                    }
                    catch (Exception)
                    {
                        errors++;
                        // Internal EncryptXml handles full structured Serilog logging parameter mapping.
                        // Bypass explicitly here so an individual record exception doesn't kill the batch loop execution.
                        continue;
                    }
                }

                Console.WriteLine();
                Log.Information("{Count} files not processed due to errors", errors);
                Log.Information("{Count} encrypted XML output files safely generated", n - errors);

                Log.Information("Destination directory: {OutputPath}", outputPath);
                FileInfo fi = new(publicKeyPath);
                Log.Information("Public key verification resource: {KeyName}", fi.Name);
            }
            catch (Exception e)
            {
                Log.Error(e, "Error from EncryptXml:");
                await Task.Delay(TIMEOUT_MS);
                await Log.CloseAndFlushAsync();
                return 1;
            }

            subm.GeneratedXml = DateTime.Now;
            subm.NPatients = n;

            try
            {
                await connection.UpdateAsync(subm);
            }
            catch (Exception e)
            {
                Log.Error(e, "Error updating submission record");
            }

            x?.Dispose();

            await Task.Delay(TIMEOUT_MS);
            await Log.CloseAndFlushAsync(); // Safely flush remaining files out to text disks before closing console execution context
            return 0;
        }
    }
}

namespace UkrdcPgpXml
{
    [Table("Patients")]
    class Patient
    {
        public int PatientId { get; set; }
        public string Identifier { get; set; } = string.Empty;
    }

    [Table("SendingFacilities")]
    public class SendingFacility
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
    }

    [Table("Submissions")]
    class Submission
    {
        [Key]
        public int Id { get; set; } // Settable property required by Dapper for update mutations
        public DateTime PopulatedTables { get; set; }
        public DateTime Start { get; set; }
        public DateTime Stop { get; set; }
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
        public string LogFile { get; set; } = "[path to your log files]";
        public int SFID { get; set; } = -1;  //Sending facility. Default to an invalid ID to force user configuration

        public AppSettings Get(string[] args)
        {
            const string SettingsFileName = "appsettings.json";
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string settingsFilePath = Path.Combine(baseDirectory, SettingsFileName);

            if (!File.Exists(settingsFilePath))
            {
                var defaultSettings = this;
                JsonSerializerOptions options = new() { WriteIndented = true };
                string initialJson = JsonSerializer.Serialize(defaultSettings, options);
                File.WriteAllText(settingsFilePath, initialJson);
            }

            IConfiguration rootConfig = new ConfigurationBuilder()
                .SetBasePath(baseDirectory)
                .AddJsonFile(SettingsFileName, optional: false, reloadOnChange: true)
                .AddCommandLine(args)
                .Build();

            return rootConfig.Get<AppSettings>() ?? new AppSettings();
        }
    }
}
