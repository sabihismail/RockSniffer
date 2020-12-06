using System;

namespace RockSniffer.Configuration
{
    [Serializable]
    public class LastFMSettings
    {
        public bool Enabled = true;
        public string LAST_FM_API_KEY = "";
        public string LAST_FM_API_SECRET = "";
        public string LAST_FM_USERNAME = "";
        public string LAST_FM_PASSWORD = "";
    }
}
