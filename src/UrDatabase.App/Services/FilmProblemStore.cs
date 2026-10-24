using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Problems the owner has reported with their films, on disk.
    /// </summary>
    /// <remarks>
    /// A table of its own rather than a column on <c>movies</c>, because a film from the server
    /// has no row there, and because <c>jellyfin_movies</c> is deleted and rewritten by every
    /// sync: a report stored beside the cached server library would be gone the next time the
    /// server was asked anything.
    ///
    /// Keyed on <see cref="UiMovie.Key"/>, the identity the library already deduplicates on, so a
    /// film in both places has one report — the local copy's, which is the one that plays.
    ///
    /// Nothing here is sent to Jellyfin. It is this owner's note about their copy.
    /// </remarks>
    public static class FilmProblemStore
    {
        private const string Table = "film_problems";

        /// <summary>
        /// Saves a report, replacing whatever was there. A report with nothing in it clears the
        /// film instead, so unticking the last box is the same as pressing Clear.
        /// </summary>
        public static void Save(SqliteConnection conn, FilmProblemReport report)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (report is null) throw new ArgumentNullException(nameof(report));

            var key = report.FilmKey.Trim();
            if (key.Length == 0) return;

            if (report.IsEmpty)
            {
                Clear(conn, key);
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO film_problems (film_key, problems, note, flagged_at)
VALUES (@key, @problems, @note, @at)
ON CONFLICT(film_key) DO UPDATE SET
    problems   = excluded.problems,
    note       = excluded.note,
    flagged_at = excluded.flagged_at;";

            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@problems", FilmProblems.Encode(report.Kinds));
            cmd.Parameters.AddWithValue("@note", report.Note.Length == 0 ? DBNull.Value : report.Note);
            cmd.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            cmd.ExecuteNonQuery();
        }

        /// <summary>Forgets every problem reported for one film.</summary>
        public static void Clear(SqliteConnection conn, string filmKey)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (string.IsNullOrWhiteSpace(filmKey)) return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM film_problems WHERE film_key = @key";
            cmd.Parameters.AddWithValue("@key", filmKey.Trim());
            cmd.ExecuteNonQuery();
        }

        /// <summary>The report for one film, or null when nothing has been reported.</summary>
        public static FilmProblemReport? Load(SqliteConnection conn, string? filmKey)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (string.IsNullOrWhiteSpace(filmKey) || !Database.TableExists(conn, Table)) return null;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT film_key, problems, note, flagged_at FROM film_problems WHERE film_key = @key";
            cmd.Parameters.AddWithValue("@key", filmKey.Trim());

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }

        /// <summary>
        /// Every report, by film key. Empty — not an error — on a catalogue that predates the
        /// table, because the library's read path connects without migrating and must still show
        /// the films.
        /// </summary>
        public static IReadOnlyDictionary<string, FilmProblemReport> LoadAll(SqliteConnection conn)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var reports = new Dictionary<string, FilmProblemReport>(StringComparer.Ordinal);
            if (!Database.TableExists(conn, Table)) return reports;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT film_key, problems, note, flagged_at FROM film_problems";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var report = Read(reader);
                if (!report.IsEmpty) reports[report.FilmKey] = report;
            }

            return reports;
        }

        /// <summary>
        /// The same, from a path. No file means nothing reported, and a read must never create a
        /// catalogue: that is a scan's job.
        /// </summary>
        public static IReadOnlyDictionary<string, FilmProblemReport> LoadAll(string? dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                return new Dictionary<string, FilmProblemReport>(StringComparer.Ordinal);

            using var conn = Database.Connect(dbPath);
            return LoadAll(conn);
        }

        private static FilmProblemReport Read(SqliteDataReader reader)
        {
            var key = reader.GetString(0);
            var kinds = FilmProblems.Decode(reader.IsDBNull(1) ? null : reader.GetString(1));
            var note = reader.IsDBNull(2) ? null : reader.GetString(2);

            DateTime? at = null;
            if (!reader.IsDBNull(3) &&
                DateTime.TryParse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                at = parsed;
            }

            return new FilmProblemReport(key, kinds, note, at);
        }
    }
}
