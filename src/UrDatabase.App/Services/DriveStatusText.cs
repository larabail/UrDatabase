using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// What the drive check says, on the banner and in the drives dialog. Out of the window so
    /// the sentences can be asserted on: "1 drives" and a banner raised for a healthy library are
    /// exactly the kind of mistake that survives a glance at a screenshot.
    /// </summary>
    public static class DriveStatusText
    {
        /// <summary>
        /// The banner's first line, or null when every drive that matters answered and there is
        /// nothing to say.
        /// </summary>
        public static string? Headline(IReadOnlyList<DriveReport>? reports)
        {
            var down = Down(reports);
            if (down.Count == 0) return null;

            if (down.Count == 1)
            {
                var drive = down[0];
                return drive.State == DriveState.NotResponding
                    ? $"{Name(drive)} is not responding"
                    : $"{Name(drive)} is not connected";
            }

            return $"{down.Count.ToString(CultureInfo.InvariantCulture)} drives are not available";
        }

        /// <summary>
        /// The banner's second line: what being away costs, which is the reason to care.
        /// </summary>
        public static string Detail(IReadOnlyList<DriveReport>? reports)
        {
            var down = Down(reports);
            if (down.Count == 0) return "";

            var films = down.Sum(d => d.FilmCount);
            var names = string.Join(", ", down.Select(Name));

            var it = down.Count == 1 ? "it" : "them";
            var itIs = down.Count == 1 ? "it is" : "they are";
            var itHas = down.Count == 1 ? "it has" : "they have";

            var cost = films > 0
                ? $"{Films(films)} on {it} cannot be played until {itIs} back."
                : down.Any(d => d.WatchFolders.Count > 0)
                    ? $"A scan will skip the library folders on {it} until {itIs} back."
                    : $"Nothing in your library is on {it}. If {itHas} been retired, forget {it} in Details.";

            return down.Count == 1 ? cost : $"{names}. {cost}";
        }

        /// <summary>
        /// The drives dialog as one block of text: every drive, down ones first, one paragraph each.
        /// </summary>
        public static string Describe(IReadOnlyList<DriveReport>? reports)
        {
            if (reports is null || reports.Count == 0) return Empty;

            var sb = new StringBuilder();

            foreach (var drive in Ordered(reports))
            {
                if (sb.Length > 0) sb.AppendLine().AppendLine();

                sb.Append(Title(drive));
                foreach (var line in Lines(drive))
                    sb.AppendLine().Append("    ").Append(line);
            }

            return sb.ToString();
        }

        /// <summary>What the dialog says when there is nothing to list.</summary>
        public const string Empty =
            "No drives were found, no library folders are configured and nothing has been scanned yet, so there are no drives to check.";

        /// <summary>The order the dialog lists drives in: the ones in trouble first.</summary>
        public static IEnumerable<DriveReport> Ordered(IEnumerable<DriveReport> reports) =>
            reports
                .OrderBy(d => d.RaisesAlarm ? 0 : d.IsDown ? 1 : 2)
                .ThenBy(d => d.Root, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Label, StringComparer.OrdinalIgnoreCase);

        /// <summary>A drive's heading: where it is, what it is called, and how it stands.</summary>
        public static string Title(DriveReport drive) => $"{Name(drive)} — {StateText(drive.State)}";

        /// <summary>
        /// The drive's root and, when it has one that says something the root does not, its name:
        /// <c>E:\ (E3.0_6TB)</c>, but <c>/Volumes/Films</c> rather than <c>/Volumes/Films (Films)</c>.
        /// </summary>
        public static string Name(DriveReport drive)
        {
            var label = drive.Label?.Trim();
            if (string.IsNullOrEmpty(label)) return drive.Root;

            var root = drive.Root.TrimEnd('/', '\\');
            if (string.Equals(label, drive.Root, StringComparison.Ordinal)
                || root.EndsWith("/" + label, StringComparison.Ordinal))
                return drive.Root;

            return $"{drive.Root} ({label})";
        }

        /// <summary>Everything under a drive's heading, one line each.</summary>
        public static IReadOnlyList<string> Lines(DriveReport drive)
        {
            var lines = new List<string>();

            var first = Films(drive.FilmCount);
            if (drive.State == DriveState.Connected && drive.TotalBytes is long total && drive.FreeBytes is long free)
                first += $" · {ByteSize.Describe(free)} free of {ByteSize.Describe(total)}";
            else if (drive.IsDown && drive.TotalBytes is long size && size > 0)
                first += $" · {ByteSize.Describe(size)} disk";
            lines.Add(first);

            foreach (var folder in drive.WatchFolders)
                lines.Add($"Library folder: {folder}");

            if (drive.IsDown && drive.Remembered && drive.LastSeenUtc is DateTime seen && seen > DateTime.MinValue)
            {
                var when = seen.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
                lines.Add(drive.WatchFolders.Count == 0
                    ? $"Last seen {when}. Forget it if it has been retired."
                    : $"Last seen {when}.");
            }

            if (!drive.Remembered && drive.WatchFolders.Count == 0)
                lines.Add(NotWatched(drive.Kind));

            return lines;
        }

        public static string StateText(DriveState state) => state switch
        {
            DriveState.Connected => "connected",
            DriveState.Disconnected => "NOT CONNECTED",
            DriveState.NotResponding => "NOT RESPONDING",
            _ => state.ToString()
        };

        /// <summary>Why a drive that is listed will not be missed if it goes.</summary>
        private static string NotWatched(DriveKind? kind) => kind switch
        {
            DriveKind.Removable => "Removable drive; not reported at launch.",
            DriveKind.Network => "Network drive; not reported at launch.",
            DriveKind.Virtual => "Cloud or virtual drive; not reported at launch.",
            DriveKind.Optical => "Optical drive; not reported at launch.",
            DriveKind.Fixed => "Not remembered; not reported at launch.",
            _ => "No library folder here; not reported at launch."
        };

        private static string Films(int count) =>
            count == 1 ? "1 film" : $"{count.ToString(CultureInfo.InvariantCulture)} films";

        private static IReadOnlyList<DriveReport> Down(IReadOnlyList<DriveReport>? reports) =>
            reports?.Where(r => r.RaisesAlarm).ToList() ?? (IReadOnlyList<DriveReport>)Array.Empty<DriveReport>();
    }
}