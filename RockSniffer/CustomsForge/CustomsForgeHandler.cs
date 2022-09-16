using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RockSnifferLib.Cache;
using RockSnifferLib.Logging;
using Exception = System.Exception;

namespace RockSniffer.CustomsForge
{
    public class CustomsForgeHandler : IDisposable
    {
        private const string USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:83.0) Gecko/20100101 Firefox/83.0";

        private static readonly CookieContainer CookieContainer = new();
        private static readonly HttpClientHandler Handler = new() { CookieContainer = CookieContainer };
        private static readonly HttpClient Client = new();

        private readonly CustomsForgeDatabase SavedDatabase = new();
        private readonly SQLiteCache DatabaseCache;

        private readonly List<int> ToUpdate = new();
        private readonly List<string> CreatorsToIgnore = new();
        private readonly List<string> FoldersToIgnore = new();

        private static int currentJobs;

        public CustomsForgeHandler(SQLiteCache databaseCache)
        {
            DatabaseCache = databaseCache;

            Logger.Log("[CustomsForge] Checking for new songs...");

            var cookies = Program.config.customsForgeSettings.Cookie;

            if (string.IsNullOrEmpty(cookies))
            {
                Logger.LogError(
                    "[CustomsForge] You need to set the cookie. To do this, open up your browser and click 'CTRL+SHIFT+I'. " +
                    "Click on the tab at the top that says 'Network' (if you don't see it, click on the expansion arrow on the right side of the top bar) and select 'All' as the subheading. " +
                    "Load up 'http://ignition.customsforge.com' and ensure you are still in the Network tab. Look for the entry that says '200 POST' and click it. On the right you should see some headings. " +
                    "Go under 'Request headers' and copy the value after the 'Cookie:' entry. Ensure you copy it in it's entirety (click on it and click CTRL+A and CTRL+C to copy). " +
                    "Paste that after the 'cookie:' part in the config file named 'customsForge.json'.");

                return;
            }

            SetUpCookieContainer(cookies);

            Client.DefaultRequestHeaders.UserAgent.ParseAdd(USER_AGENT);
            Client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");

            CreatorsToIgnore = Program.config.customsForgeSettings.CreatorsToIgnore.Split('|')
                .Select(x => x.Trim().ToLower())
                .Distinct()
                .ToList()
                .FindAll(x => !string.IsNullOrEmpty(x));

            FoldersToIgnore = Program.config.customsForgeSettings.FoldersToIgnore.Split('|')
                .Select(x => x.Trim().ToLower())
                .Distinct()
                .ToList()
                .FindAll(x => !string.IsNullOrEmpty(x));

            CheckForNewSongs();
        }

        private void CheckForNewSongs()
        {
            var badArtists = Program.config.customsForgeSettings.ArtistsToIgnore.Split('|')
                .Select(x => x.Trim().ToLower())
                .Distinct()
                .ToList()
                .FindAll(x => !string.IsNullOrEmpty(x));

            var artists = DatabaseCache.GetAllDLCSongs()
                .FindAll(x => !FoldersToIgnore.Any(folder => x.psarcFile.ToLower().Replace("/", "\\").Contains($"\\{folder}\\")))
                .Select(x => x.artistName)
                .ToList()
                .FindAll(x => !badArtists.Any(y => x.StartsWith(y.ToLower())));

            var goodArtists = Program.config.customsForgeSettings.ArtistsToInclude.Split('|')
                .Select(x => x.Trim())
                .Distinct()
                .ToList()
                .FindAll(x => !string.IsNullOrEmpty(x));

            artists.AddRange(goodArtists);

            artists = artists.Select(x => x.Trim())
                .Distinct()
                .ToList()
                .FindAll(x => !string.IsNullOrEmpty(x));

            var creators = Program.config.customsForgeSettings.CreatorsToInclude.Split('|')
                .Select(x => x.Trim().ToLower())
                .Distinct()
                .ToList()
                .FindAll(x => !string.IsNullOrEmpty(x));

            currentJobs = artists.Count + creators.Count;

            foreach (var artist in artists)
            {
                Handle(artist, CustomsForgeQueryType.ARTIST);
            }

            foreach (var creator in creators)
            {
                Handle(creator, CustomsForgeQueryType.CREATOR);
            }
        }

        private async void Handle(string name, CustomsForgeQueryType queryType)
        {
            var queryID = await GetQueryID(name, queryType);

            if (queryID?.Results?.Length == 0)
            {
                Decrement();
                return; 
            }

            var entries = await GetCustomsForgeEntries(queryID?.Results?.First().ID ?? 0, queryType) ?? new List<CustomsForgeQueryData>();

            foreach (var entry in entries)
            {
                if (CreatorsToIgnore.Contains(entry.Artist ?? "")) continue;
                if (string.IsNullOrEmpty(entry.URL)) continue;

                var stringFormat = Program.config.customsForgeSettings.NewSongFormat;
                if (string.IsNullOrEmpty(stringFormat))
                {
                    var formatted = Format("[CustomsForge] '%Artist' - '%Title' uploaded %ModifiedDate with %Downloads downloads - %URL", entry);

                    Logger.Log(formatted);
                }
                else
                {
                    var formatted = Format(stringFormat, entry);

                    Logger.Log(formatted);
                }

                SavedDatabase.AddSongEntry(entry);
                if (ToUpdate.Contains(entry.ID))
                {
                    SavedDatabase.UpdateDate(entry.ID, entry.ModifiedDate);
                    ToUpdate.Remove(entry.ID);
                }
            }

            Decrement();

            Thread.Sleep(200);
        }

        private async Task<CustomsForgeArtistResults?> GetQueryID(string name, CustomsForgeQueryType queryType)
        {
            string searchType;

            if (queryType == CustomsForgeQueryType.ARTIST)
            {
                searchType = "artists";
            }
            else if (queryType == CustomsForgeQueryType.CREATOR)
            {
                searchType = "members";
            }
            else
            {
                throw new ArgumentException("Invalid Query Type.");
            }

            var url = $"https://ignition4.customsforge.com/cdlc/search/{searchType}?term={name}&_type=query&q={name}";

            using (var request = new HttpRequestMessage
            {
                RequestUri = new Uri(url),
                Method = HttpMethod.Get
            })
            {
                var response = await Client.SendAsync(request);

                var str = await response.Content.ReadAsStringAsync();

                var json = JsonConvert.DeserializeObject<CustomsForgeArtistResults>(str);

                return json;
            }
        }

        private static string Format(string format, CustomsForgeQueryData query)
        {
            var output = format;

            foreach (var property in typeof(CustomsForgeQueryData).GetProperties())
            {
                var key = "%" + property.Name;
                var valueObj = property.GetValue(query, null);

                string value;

                switch (valueObj)
                {
                    case null:
                        value = string.Empty;
                        break;

                    case bool valueBool:
                        value = valueBool ? "Yes" : "No";
                        break;

                    case DateTimeOffset valueDateTimeOffset:
                        value = valueDateTimeOffset.ToString("yyyy-MM-dd");
                        break;

                    default:
                        value = valueObj?.ToString() ?? "";
                        break;
                }

                output = output.Replace(key, value);
            }

            return output;
        }

        private static void Decrement()
        {
            Interlocked.Decrement(ref currentJobs);

            if (currentJobs == 0)
            {
                Logger.Log("[CustomsForge] Completed new songs check.");
            }
        }

        private async Task<List<CustomsForgeQueryData>?> GetCustomsForgeEntries(int queryID, CustomsForgeQueryType queryType)
        {
            var url = $"https://ignition4.customsforge.com/?draw=1&columns[0][data]=addBtn&columns[0][searchable]=false&columns[0][orderable]=false&columns[1][data]=artistName&columns[2][data]=titleName&columns[3][data]=albumName&columns[4][data]=year&columns[5][data]=duration&columns[5][orderable]=false&columns[6][data]=tunings&columns[6][searchable]=false&columns[6][orderable]=false&columns[7][data]=version&columns[7][searchable]=false&columns[7][orderable]=false&columns[8][data]=author.name&columns[9][data]=created_at&columns[9][searchable]=false&columns[10][data]=updated_at&columns[10][searchable]=false&columns[11][data]=downloads&columns[11][searchable]=false&columns[12][data]=parts&columns[12][orderable]=false&columns[13][data]=platforms&columns[13][orderable]=false&columns[14][data]=file_pc_link&columns[14][searchable]=false&columns[15][data]=file_mac_link&columns[15][searchable]=false&columns[16][data]=artist.name&columns[17][data]=title&columns[18][data]=album&order[0][column]=10&order[0][dir]=desc&start=0&length=25&search[value]=&filter_title=&filter_album=&filter_start_year=&filter_end_year=&filter_preferred=&filter_official=&filter_disable=&filter_hidden=&_=1607221637930";

            if (queryType == CustomsForgeQueryType.ARTIST)
            {
                url += $"&filter_artist[]={queryID}";
            }
            else if (queryType == CustomsForgeQueryType.CREATOR)
            {
                url += $"&filter_member[]={queryID}";
            }
            else
            {
                throw new ArgumentException("Invalid Query Type.");
            }

            using (var request = new HttpRequestMessage
            {
                RequestUri = new Uri(url),
                Method = HttpMethod.Get,
                Headers =
                {
                    { HttpRequestHeader.Accept.ToString(), "application/json" },
                    { "X-Requested-With", "XMLHttpRequest" }
                }
            })
            {
                string content;
                try
                {
                    var response = await Client.SendAsync(request);
                    content = await response.Content.ReadAsStringAsync();
                }
                catch (Exception e)
                {
                    Logger.LogError("[CustomsForge] Something went wrong communicating with the CustomsForge servers. Error: \n" + e);

                    return new List<CustomsForgeQueryData>();
                }

                if (content.StartsWith("<!DOCTYPE html>"))
                {
                    Logger.LogError("[CustomsForge] Something went wrong. Output:\n\n" + content);

                    return new List<CustomsForgeQueryData>();
                }

                var json = JsonConvert.DeserializeObject<CustomsForgeQueryResult>(content);
                var customsForgeQueryResults = json?.Data;

                var dateToStartByStr = Program.config.customsForgeSettings.DateToStartBy;
                if (!string.IsNullOrEmpty(dateToStartByStr))
                {
                    if (!DateTimeOffset.TryParse(dateToStartByStr, out var dateToStartBy))
                    {
                        Logger.LogError("[CustomsForge] The date that was inputted is incorrect. Use the format 'yyyy-mm-dd'.");
                    }

                    customsForgeQueryResults = customsForgeQueryResults?.FindAll(x => x.ModifiedDate > dateToStartBy);
                }

                customsForgeQueryResults = customsForgeQueryResults?.FindAll(x =>
                {
                    var handled = SavedDatabase.IsAlreadyHandled(x.ID, x.ModifiedDate);

                    switch (handled)
                    {
                        case CustomsForgeDatabase.Handled.NOT_HANDLED:
                            return true;

                        case CustomsForgeDatabase.Handled.OUTDATED:
                            ToUpdate.Add(x.ID);
                            return true;

                        case CustomsForgeDatabase.Handled.HANDLED:
                            return false;

                        default:
                            return false;
                    }
                });

                return customsForgeQueryResults;
            }
        }

        private static void SetUpCookieContainer(string cookies)
        {
            var splitCookies = cookies.Split(';')
                .Select(x => x.Trim());

            foreach (var cookie in splitCookies)
            {
                var split = cookie.Split(new[] { '=' }, 2);

                if (split.Length != 2) continue;

                var key = split[0].Trim();
                var value = split[1].Trim();

                CookieContainer.Add(new Uri("http://ignition4.customsforge.com"), new Cookie(key, value));
                CookieContainer.Add(new Uri("http://customsforge.com"), new Cookie(key, value));
            }
        }

        public void Dispose()
        {
            Client.Dispose();
            Handler.Dispose();
        }

        public enum CustomsForgeQueryType
        {
            ARTIST,
            CREATOR
        }

        public class CustomsForgeArtistResults
        {
            [JsonProperty("results")]
            public CustomsForgeArtistResult[]? Results { get; set; }
        }

        public class CustomsForgeArtistResult
        {
            [JsonProperty("id")]
            public int ID { get; set; }

            [JsonProperty("text")]
            public string? Text { get; set; }
        }

        public class CustomsForgeQueryResult
        {
            [JsonProperty("recordsTotal")]
            public int RecordsTotal { get; set; }

            [JsonProperty("recordsFiltered")]
            public int RecordsFiltered { get; set; }

            [JsonProperty("data")]
            public List<CustomsForgeQueryData>? Data { get; set; }
        }

        public class CustomsForgeQueryArtist
        {
            [JsonProperty("id")]
            public int ID { get; set; }

            [JsonProperty("name")]
            public string? Name { get; set; }
        }

        public class CustomsForgeQueryAuthor
        {
            [JsonProperty("id")]
            public int ID { get; set; }

            [JsonProperty("name")]
            public string? Name { get; set; }
        }

        public class CustomsForgeQueryData
        {
            [JsonProperty("id")]
            public int ID { get; set; }

            [JsonProperty("artist")]
            public CustomsForgeQueryArtist? ArtistObj { get; set; }

            [JsonProperty("author")]
            public CustomsForgeQueryArtist? AuthorObj { get; set; }

            [JsonProperty("title")]
            public string? Title { get; set; }

            [JsonProperty("album")]
            public string? Album { get; set; }

            [JsonProperty("lead")]
            public string? Lead { get; set; }

            [JsonProperty("rhythm")]
            public string? Rhythm { get; set; }

            [JsonProperty("bass")]
            public string? Bass { get; set; }

            [JsonProperty("created_at")]
            public string? CreationDateImpl { get; set; }

            [JsonProperty("updated_at")]
            public string? ModifiedDateImpl { get; set; }

            [JsonProperty("downloads")]
            public int? Downloads { get; set; }

            [JsonProperty("has_lyrics")]
            public bool? IsVocals { get; set; }

            [JsonProperty("file_pc_link")]
            public string? URL { get; set; }

            public string? Artist => ArtistObj?.Name;

            public string? Author => AuthorObj?.Name;

            public DateTimeOffset CreationDate => DateTimeOffset.ParseExact(CreationDateImpl ?? "01/01/2023", "MM/dd/yyyy", CultureInfo.InvariantCulture);

            public DateTimeOffset ModifiedDate => DateTimeOffset.ParseExact(ModifiedDateImpl ?? "01/01/2023", "MM/dd/yyyy", CultureInfo.InvariantCulture);

            public bool IsLead => !string.IsNullOrWhiteSpace(Lead);

            public bool IsRhythm => !string.IsNullOrWhiteSpace(Rhythm);

            public bool IsBass => !string.IsNullOrWhiteSpace(Bass);

            public bool IsPC => !string.IsNullOrWhiteSpace(URL);
        }
    }
}
