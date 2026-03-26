using Newtonsoft.Json;
using System;
using System.IO;

namespace RockSniffer.Configuration
{
    public class ConfigExt : Config
    {
        private static readonly string cfiledir = "." + Path.DirectorySeparatorChar + "config" + Path.DirectorySeparatorChar;

        private const string lastFMFile = "lastFM.json";
        private const string customsForgeFile = "customsForge.json";

        public static LastFMSettings LastFMSettings => lastFMSettingsImpl.Value;
        public static CustomsForgeSettings CustomsForgeSettings => customsForgeSettingsImpl.Value;
        private static readonly Lazy<LastFMSettings> lastFMSettingsImpl = new Lazy<LastFMSettings>(() => LoadFile<LastFMSettings>(lastFMFile));
        private static readonly Lazy<CustomsForgeSettings> customsForgeSettingsImpl = new Lazy<CustomsForgeSettings>(() => LoadFile<CustomsForgeSettings>(customsForgeFile));

        public static void SaveLastFMSettings()
        {
            File.WriteAllText(cfiledir + lastFMFile, JsonConvert.SerializeObject(LastFMSettings, Formatting.Indented));
        }

        private static T LoadFile<T>(string file) where T : new()
        {
            if (File.Exists(cfiledir + file))
            {
                string str = File.ReadAllText(cfiledir + file);
                return JsonConvert.DeserializeObject<T>(str);
            }

            T t = new T();
            File.WriteAllText(cfiledir + file, JsonConvert.SerializeObject(t, Formatting.Indented));

            return t;
        }
    }
}
