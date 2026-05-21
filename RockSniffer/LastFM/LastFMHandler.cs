using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using IF.Lastfm.Core.Api;
using IF.Lastfm.Core.Api.Helpers;
using IF.Lastfm.Core.Objects;
using RockSnifferLib.Events;
using RockSnifferLib.Logging;
using RockSnifferLib.Sniffing;
using RockSnifferLib.RSHelpers;
using RockSniffer.Configuration;

namespace RockSniffer.LastFM
{
    public class LastFMHandler : IDisposable
    {
        private static readonly object Mutex = new();
        private static readonly HttpClient Http = new() { DefaultRequestHeaders = { { "User-Agent", "RockSniffer/0.6.4" } } };

        private readonly LastfmClient Client;
        private RSMemoryReadout? Readout;
        private SnifferState State;
        private SongDetails? SongDetails;
        private Scrobble? LastScrobbled;
        private bool Scrobbled;

        public LastFMHandler(Sniffer sniffer)
        {
            if (string.IsNullOrWhiteSpace(ConfigExt.LastFMSettings.LAST_FM_API_KEY) ||
                string.IsNullOrWhiteSpace(ConfigExt.LastFMSettings.LAST_FM_API_SECRET))
            {
                Logger.Log("[Last.FM] API key and secret are required. Fill them in config/lastFM.json and restart.");
                return;
            }

            Client = new LastfmClient(ConfigExt.LastFMSettings.LAST_FM_API_KEY, ConfigExt.LastFMSettings.LAST_FM_API_SECRET);

            try
            {
                if (!AuthenticateAsync().Result)
                    return;
            }
            catch (Exception ex)
            {
                Logger.LogError("[Last.FM] Authentication failed: {0}", ex.InnerException?.Message ?? ex.Message);
                return;
            }

            sniffer.OnStateChanged += Sniffer_OnStateChanged;
            sniffer.OnSongChanged += Sniffer_OnSongChanged;
            sniffer.OnMemoryReadout += Sniffer_OnMemoryReadout;
        }

        private async Task<bool> AuthenticateAsync()
        {
            var sessionKey = ConfigExt.LastFMSettings.LAST_FM_SESSION_KEY;

            if (!string.IsNullOrWhiteSpace(sessionKey))
            {
                ((LastAuth)Client.Auth).LoadSession(new LastUserSession { Token = sessionKey });
                Logger.Log("[Last.FM] Session restored.");
                return true;
            }

            return await DoWebAuthAsync();
        }

        private async Task<bool> DoWebAuthAsync()
        {
            Logger.Log("[Last.FM] No session found. Starting web authentication...");

            var apiKey = ConfigExt.LastFMSettings.LAST_FM_API_KEY;
            var apiSecret = ConfigExt.LastFMSettings.LAST_FM_API_SECRET;

            // Step 1: get an unsigned token
            var tokenSig = Sign(new Dictionary<string, string>
            {
                ["api_key"] = apiKey,
                ["method"] = "auth.getToken"
            }, apiSecret);

            var tokenUrl = $"https://ws.audioscrobbler.com/2.0/?method=auth.getToken&api_key={apiKey}&api_sig={tokenSig}&format=json";
            var tokenResponse = await Http.GetAsync(tokenUrl);
            var tokenJson = await tokenResponse.Content.ReadAsStringAsync();

            if (!tokenResponse.IsSuccessStatusCode)
            {
                Logger.LogError("[Last.FM] Failed to get auth token (HTTP {0}): {1}", (int)tokenResponse.StatusCode, tokenJson);
                return false;
            }

            var tokenDoc = JsonDocument.Parse(tokenJson);

            if (!tokenDoc.RootElement.TryGetProperty("token", out var tokenElement))
            {
                Logger.LogError("[Last.FM] Failed to get auth token: {0}", tokenJson);
                return false;
            }

            var token = tokenElement.GetString()!;
            var authUrl = $"https://www.last.fm/api/auth/?api_key={apiKey}&token={token}";

            Logger.Log("[Last.FM] Opening browser for authorization...");
            Logger.Log("[Last.FM] If browser doesn't open, visit: https://www.last.fm/api/auth/?token={0}", token);

            try { Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true }); }
            catch { }

            Logger.Log("[Last.FM] Waiting for authorization in browser...");

            // Step 2: poll until user approves (up to 5 minutes)
            var sessionSig = Sign(new Dictionary<string, string>
            {
                ["api_key"] = apiKey,
                ["method"] = "auth.getSession",
                ["token"] = token
            }, apiSecret);

            var sessionUrl = $"https://ws.audioscrobbler.com/2.0/?method=auth.getSession&api_key={apiKey}&token={token}&api_sig={sessionSig}&format=json";

            JsonElement sessionElement = default;
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(2000);
                var pollResponse = await Http.GetAsync(sessionUrl);
                if (!pollResponse.IsSuccessStatusCode)
                    continue;
                var sessionJson = await pollResponse.Content.ReadAsStringAsync();
                var sessionDoc = JsonDocument.Parse(sessionJson);
                if (sessionDoc.RootElement.TryGetProperty("session", out sessionElement))
                    break;
                sessionElement = default;
            }

            if (sessionElement.ValueKind == JsonValueKind.Undefined)
            {
                Logger.LogError("[Last.FM] Authorization timed out. Restart to try again.");
                return false;
            }

            var sessionKey = sessionElement.GetProperty("key").GetString()!;
            var username = sessionElement.GetProperty("name").GetString()!;

            ((LastAuth)Client.Auth).LoadSession(new LastUserSession { Token = sessionKey });

            ConfigExt.LastFMSettings.LAST_FM_SESSION_KEY = sessionKey;
            ConfigExt.SaveLastFMSettings();

            Logger.Log("[Last.FM] Authorized as {0}", username);
            return true;
        }

        private static string Sign(Dictionary<string, string> parameters, string secret)
        {
            // Sort params alphabetically, concat key+value, append secret, md5
            var keys = new List<string>(parameters.Keys);
            keys.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            foreach (var k in keys)
                sb.Append(k).Append(parameters[k]);
            sb.Append(secret);

            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private void UpdatePresence()
        {
            lock (Mutex)
            {
                if ((SongDetails == null || Readout == null) || State != SnifferState.SONG_PLAYING)
                {
                    if (State != SnifferState.IN_MENUS && State != SnifferState.NONE) return;

                    Scrobbled = false;
                    LastScrobbled = null;

                    return;
                }

                var scrobble = new Scrobble(SongDetails.artistName, SongDetails.albumName, SongDetails.songName, DateTimeOffset.UtcNow);

                if (LastScrobbled != null)
                {
                    if (LastScrobbled.Artist == scrobble.Artist && LastScrobbled.Album == scrobble.Album && LastScrobbled.Track == scrobble.Track)
                    {
                        if (Scrobbled) return;

                        var diff = Readout.songTimer;
                        var halfTime = Convert.ToInt32(SongDetails.songLength) / 2;

                        if (diff <= halfTime) return;

                        var scrobbleResponse = Client.Scrobbler.ScrobbleAsync(scrobble).Result;

                        if (scrobbleResponse.Success)
                            Logger.Log("[Last.FM] Scrobbled " + SongDetails.songName + " - " + SongDetails.artistName);
                        else
                            Logger.Log("[Last.FM] Failed Scrobble " + SongDetails.songName + " - " + SongDetails.artistName);

                        Scrobbled = true;
                    }
                    else
                    {
                        Scrobbled = false;
                        LastScrobbled = null;
                    }

                    return;
                }

                var response = Client.Track.UpdateNowPlayingAsync(scrobble).Result;

                if (response.Success)
                    Logger.Log("[Last.FM] Now Playing " + SongDetails.songName + " - " + SongDetails.artistName);
                else
                    Logger.Log("[Last.FM] Failed Now Playing Update " + SongDetails.songName + " - " + SongDetails.artistName);

                LastScrobbled = scrobble;
            }
        }

        private void Sniffer_OnMemoryReadout(object sender, OnMemoryReadoutArgs e)
        {
            Readout = e.memoryReadout;
            UpdatePresence();
        }

        private void Sniffer_OnSongChanged(object sender, OnSongChangedArgs e)
        {
            SongDetails = e.songDetails;
            UpdatePresence();
        }

        private void Sniffer_OnStateChanged(object sender, OnStateChangedArgs e)
        {
            State = e.newState;
            UpdatePresence();
        }

        public void Dispose()
        {
            Client?.Dispose();
        }
    }
}
