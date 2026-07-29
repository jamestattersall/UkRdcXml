using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace UKRDC
{
    internal static class ConfigSettings
    {
        public static AppSettings GetSettings(string[] args)
        {
            // 1. Define the layout configuration paths
            const string SettingsFileName = "appsettings.json";
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string settingsFilePath = Path.Combine(baseDirectory, SettingsFileName);

            // 2. Initialize default configuration file if it does not exist
            EnsureSettingsFileExists(settingsFilePath);

            // 3. Load settings via Microsoft.Extensions.Configuration
            IConfiguration rootConfig = new ConfigurationBuilder()
                .SetBasePath(baseDirectory)
                .AddJsonFile(SettingsFileName, optional: false, reloadOnChange: true)
                .AddCommandLine(args)
                .Build();

            // Bind JSON configuration structure to a strongly-typed class instance
            return rootConfig.Get<AppSettings>() ?? new AppSettings();

        }
        static void EnsureSettingsFileExists(string filePath)
            {
                if (!File.Exists(filePath))
                {
                    var defaultSettings = new AppSettings();
                    string initialJson = JsonSerializer.Serialize(defaultSettings, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(filePath, initialJson);
                }
            }
        } 
    }

    public class AppSettings
    {
        public string ConnectionString { get; set; } = "Data Source=(local);Initial Catalog=UKRDC;Integrated Security=True;Persist Security Info=False;Pooling=False;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=True;Command Timeout=0";
        public string Query { get; set; } = "SELECT dbo.GenerateXmlV2(@sendingFacilityId,@patientId,@start,@end)";
        public string PublicKeyFile { get; set; } = "C:\\Users\\james\\OneDrive\\Documents\\JET_public.asc";
        public string OutputFileDirectory { get; set; } = "C:\\Users\\james\\Downloads\\XML";
        public string PatientIdParameter { get; set; } = "@patientId";
        public string SendingFacilityIdParameter { get; set; } = "@sendingFacilityId";
        public string StartParameter { get; set; } = "@start";
        public string EndParameter { get; set; } = "@end";
        public string OutputFileExtension { get; set; } = "xml.pgp";
        public int SFID { get; set; } = -1;

}
