using System.Data.SQLite;
using System.IO;
using RockSnifferLib.Logging;
using DateTimeOffset = System.DateTimeOffset;

namespace RockSniffer.CustomsForge
{
    public class CustomsForgeDatabase
    {
        private readonly SQLiteConnection Connection;

        private const string FILE_NAME = "customsForgeDatabase.sqlite";

        public CustomsForgeDatabase()
        {
            if (!File.Exists(FILE_NAME))
            {
                SQLiteConnection.CreateFile(FILE_NAME);
            }

            Connection = new SQLiteConnection("Data Source=" + FILE_NAME + ";");
            Connection.Open();

            CreateTables();
        }

        public Handled IsAlreadyHandled(int id, DateTimeOffset modifiedDate)
        {
            var q = @"SELECT * FROM `songs` WHERE `song_id` = @id";

            using var cmd = Connection.CreateCommand();
            cmd.CommandText = q;
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var entryModifiedDate = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt32(reader.GetOrdinal("modified_date")));

                return modifiedDate > entryModifiedDate ? Handled.OUTDATED : Handled.HANDLED;
            }

            return Handled.NOT_HANDLED;
        }

        public void AddSongEntry(CustomsForgeHandler.CustomsForgeQueryData result)
        {
            var q = @"
            INSERT OR IGNORE INTO `songs`(
                `song_id`,
                `artist`,
                `title`,
                `album`,
                `modified_date`,
                `creation_date`,
                `url`
            )
            VALUES (@id, @artist, @title, @album, @modified_date, @creation_date, @url);";

            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = q;

                cmd.Parameters.AddWithValue("@id", result.ID);
                cmd.Parameters.AddWithValue("@artist", result.Artist);
                cmd.Parameters.AddWithValue("@title", result.Title);
                cmd.Parameters.AddWithValue("@album", result.Album);
                cmd.Parameters.AddWithValue("@modified_date", result.ModifiedDate.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("@creation_date", result.CreationDate.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("@url", result.URL);

                cmd.ExecuteNonQuery();
            }
        }

        public void UpdateDate(int id, DateTimeOffset modifiedDate)
        {
            var q = "UPDATE `songs` SET `modified_date` = @date WHERE `song_id` = @id";

            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = q;

                cmd.Parameters.AddWithValue("@date", modifiedDate.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("@id", id);

                cmd.ExecuteNonQuery();
            }
        }

        public void AddProblematicURL(string url)
        {
            var q = @"
            INSERT OR IGNORE INTO `problematic_urls`(
                `url`
            )
            VALUES (@url);";

            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = q;

                cmd.Parameters.AddWithValue("@url", url);

                cmd.ExecuteNonQuery();
            }
        }

        public bool IsProblematicURL(string url)
        {
            var q = @"SELECT EXISTS (SELECT 1 FROM `problematic_urls` WHERE `url` = @url)";

            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = q;
                cmd.Parameters.AddWithValue("@url", url);

                var result = cmd.ExecuteScalar();

                return (long) result == 1;
            }
        }

        private void CreateTables()
        {
            var q = @"
            CREATE TABLE IF NOT EXISTS `songs` (
	            `id`	INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
	            `song_id`	INTEGER UNIQUE NOT NULL,
                `artist`    STRING NOT NULL,
                `title`    STRING NOT NULL,
                `album`    STRING NOT NULL,
	            `modified_date`	INTEGER NOT NULL,
	            `creation_date`	INTEGER NOT NULL,
                `url`   STRING NOT NULL
            );";

            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = q;
                cmd.ExecuteNonQuery();
            }

            q = @"
            CREATE TABLE IF NOT EXISTS `problematic_urls` (
	            `id`	INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                `url`   STRING NOT NULL
            );";

            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = q;
                cmd.ExecuteNonQuery();
            }

            q = "CREATE INDEX IF NOT EXISTS `song_id` ON `songs` (`song_id`);";

            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = q;
                cmd.ExecuteNonQuery();
            }

            if (Logger.logCache)
            {
                Logger.Log("[CustomsForge] SQLite database initialized");
            }
        }

        public enum Handled
        {
            NOT_HANDLED,
            HANDLED,
            OUTDATED
        }
    }
}
