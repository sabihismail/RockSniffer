using System;
using IF.Lastfm.Core.Api;
using IF.Lastfm.Core.Objects;
using RockSnifferLib.Events;
using RockSnifferLib.Logging;
using RockSnifferLib.Sniffing;
using RockSnifferLib.RSHelpers;

namespace RockSniffer.LastFM
{
    public class LastFMHandler : IDisposable
    {
        private static readonly object Mutex = new();

        private const string SECRET = "";

        private readonly LastfmClient Client;
        private RSMemoryReadout? Readout;
        private SnifferState State;
        private SongDetails? SongDetails;
        private Scrobble? LastScrobbled;
        private bool Scrobbled;

        public LastFMHandler(Sniffer sniffer)
        {
            Client = new LastfmClient(Program.config.lastFMSettings.LAST_FM_API_KEY, Program.config.lastFMSettings.LAST_FM_API_SECRET);

            if (string.IsNullOrWhiteSpace(Program.config.lastFMSettings.LAST_FM_USERNAME) || string.IsNullOrWhiteSpace(Program.config.lastFMSettings.LAST_FM_PASSWORD))
            {
                Logger.Log("[Last.FM] Last.FM Enabled but no user credentials exist.");

                var username = Program.config.lastFMSettings.LAST_FM_USERNAME;
                while (string.IsNullOrWhiteSpace(username))
                {
                    Console.WriteLine("Last.FM Username: ");
                    username = Console.ReadLine()?.Trim();
                }

                var password = "";
                while (string.IsNullOrWhiteSpace(password))
                {
                    Console.WriteLine("Last.FM Password: ");
                    password = Console.ReadLine()?.Trim();
                }

                var passwordHashed = Crypto.EncryptStringAES(password, username + SECRET);

                Program.config.lastFMSettings.LAST_FM_USERNAME = username;
                Program.config.lastFMSettings.LAST_FM_PASSWORD = passwordHashed;

                Program.config.SaveLastFMSettings();
            }

            var passwordUnhashed = Crypto.DecryptStringAES(Program.config.lastFMSettings.LAST_FM_PASSWORD, Program.config.lastFMSettings.LAST_FM_USERNAME + SECRET);
            var response = Client.Auth.GetSessionTokenAsync(Program.config.lastFMSettings.LAST_FM_USERNAME, passwordUnhashed).Result;

            if (response.Success)
            {
                Logger.Log("[Last.FM] Received Ready from user {0}", Client.Auth.UserSession.Username);
            }
            else
            {
                Logger.Log("[Last.FM] Invalid Last.FM Credentials. Please run the program again and ensure you inputted the data correctly.");

                Program.config.lastFMSettings.LAST_FM_USERNAME = "";
                Program.config.lastFMSettings.LAST_FM_PASSWORD = "";

                Program.config.SaveLastFMSettings();

                return;
            }

            sniffer.OnStateChanged += Sniffer_OnStateChanged;
            sniffer.OnSongChanged += Sniffer_OnSongChanged;
            sniffer.OnMemoryReadout += Sniffer_OnMemoryReadout;
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
                        {
                            Logger.Log("[Last.FM] Scrobbled " + SongDetails.songName + " - " + SongDetails.artistName);
                        }
                        else
                        {
                            Logger.Log("[Last.FM] Failed Scrobble " + SongDetails.songName + " - " + SongDetails.artistName);
                        }

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
                {
                    Logger.Log("[Last.FM] Now Playing " + SongDetails.songName + " - " + SongDetails.artistName);
                }
                else
                {
                    Logger.Log("[Last.FM] Failed Now Playing Update " + SongDetails.songName + " - " + SongDetails.artistName);
                }

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
