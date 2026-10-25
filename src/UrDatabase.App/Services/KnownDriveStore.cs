using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Every disk the app has seen plugged in, on disk.
    /// </summary>
    /// <remarks>
    /// In the catalogue rather than in configuration, for the reason <see cref="ResumeDismissalStore"/>
    /// gives: it is state the app keeps for itself, it grows by one row per disk, and
    /// <c>appsettings.json</c> is a file a person is expected to read and that two allowlists
    /// rewrite. It is also exactly as portable as the films it describes — a catalogue copied to
    /// another computer brings its disks' identities along with the paths that point at them.
    /// </remarks>
    public static class KnownDriveStore
    {
        /// <summary>Every remembered disk.</summary>
        public static IReadOnlyList<KnownDrive> Load(SqliteConnection conn)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var drives = new List<KnownDrive>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT drive_id, label, last_root, total_bytes, last_seen_at FROM known_drives";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var seen = DateTime.TryParse(
                    reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                    ? at.ToUniversalTime()
                    : DateTime.MinValue;

                drives.Add(new KnownDrive(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    seen));
            }

            return drives;
        }

        /// <summary>
        /// Records that these disks were seen, where, and when. A disk seen before keeps the date it
        /// was first seen and has everything else brought up to date, so one that has moved to a
        /// different letter is looked for at the new one.
        /// </summary>
        public static void Remember(SqliteConnection conn, IEnumerable<KnownDrive> drives)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var list = drives?.Where(d => !string.IsNullOrWhiteSpace(d.Id)).ToList() ?? new List<KnownDrive>();
            if (list.Count == 0) return;

            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO known_drives (drive_id, label, last_root, total_bytes, first_seen_at, last_seen_at)
VALUES (@id, @label, @root, @total, @seen, @seen)
ON CONFLICT(drive_id) DO UPDATE SET
    label        = excluded.label,
    last_root    = excluded.last_root,
    total_bytes  = excluded.total_bytes,
    last_seen_at = excluded.last_seen_at;";

            var id = cmd.Parameters.Add("@id", SqliteType.Text);
            var label = cmd.Parameters.Add("@label", SqliteType.Text);
            var root = cmd.Parameters.Add("@root", SqliteType.Text);
            var total = cmd.Parameters.Add("@total", SqliteType.Integer);
            var seen = cmd.Parameters.Add("@seen", SqliteType.Text);

            foreach (var drive in list)
            {
                id.Value = drive.Id;
                label.Value = (object?)drive.Label ?? DBNull.Value;
                root.Value = drive.LastRoot;
                total.Value = (object?)drive.TotalBytes ?? DBNull.Value;
                seen.Value = drive.LastSeenUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        /// <summary>
        /// Stops missing a disk. If it is plugged in again it is simply remembered again, which is
        /// what somebody who forgot it by mistake would want.
        /// </summary>
        public static void Forget(SqliteConnection conn, string id)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (string.IsNullOrWhiteSpace(id)) return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM known_drives WHERE drive_id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }
}
