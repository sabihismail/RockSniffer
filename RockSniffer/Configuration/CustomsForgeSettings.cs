using System;

namespace RockSniffer.Configuration
{
    [Serializable]
    public class CustomsForgeSettings
    {
        public bool Enabled = false;
        public string Cookie = "";
        public string Username = "";
        public string Password = "";
        public string DateToStartBy = "2019-01-01"; // songs before this date will be ignored. format: yyyy-mm-dd
        public string ArtistsToIgnore = "";
        public string ArtistsToInclude = "";
        public string CreatorsToInclude = "";
        public string CreatorsToIgnore = "";
        public string FoldersToIgnore = "";
        public string NewSongFormat = "[CustomsForge] '%Artist' - '%Title' uploaded %ModifiedDate with %Downloads downloads - %URL";
    }
}
