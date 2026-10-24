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
        /// The banner's first line, or null when every drive answered and there is nothing to say.
        /// </summary>
        public static string? Headline(IReadOnlyList<DriveReport>? reports)
        {
            var down = Down(reports);
            if (down.Count == 0) return null;

            if (down.Count == 1)
            {
                var drive = down[0];
                return drive.State == DriveState.NotResponding
                    ? $"{drive.Root} is not responding"
                    : $"{drive.Root} is not connected";
            }

            return $"{down.Count.ToString(CultureInfo.InvariantCulture)} drives in your library are not available";
        }

        /// <summary>
        /// The banner's second line: what being away costs, which is the reason to care.
        /// </summary>
        public static string Detail(IReadOnlyList<DriveReport>? reports)
        {
            var down = Down(reports);
            if (down.Count == 0) return "";

            var films = down.Sum(d => d.FilmCount);
            var names = string.Join(", ", down.Select(d => d.Root));

            var it = down.Count == 1 ? "it" : "them";
            var itIs = down.Count == 1 ? "it is" : "they are";

            var cost = films == 0
                ? $"A scan will skip the library folders on {it} until {itIs} back."
                : $"{Films(films)} on {it} cannot be played until {itIs} back.";

            return down.Count == 1 ? cost : $"{names}. {cost}";
        }

        /// <summary>
        /// The drives dialog: every drive, down ones first, one paragraph each.
        /// </summary>
        public static string Describe(IReadOnlyList<DriveReport>? reports)
        {
            if (reports is null || reports.Count == 0)
                return "No library folders are configured and nothing has been scanned yet, so there are no drives to check.";

            var sb = new StringBuilder();

            foreach (var drive in reports.OrderBy(d => d.IsDown ? 0 : 1).ThenBy(d => d.Root, StringComparer.OrdinalIgnoreCase))
            {
                if (sb.Length > 0) sb.AppendLine().AppendLine();

                sb.Append(drive.Root);
                if (!string.IsNullOrWhiteSpace(drive.Label) && !string.Equals(drive.Label, drive.Root, StringComparison.Ordinal))
                    sb.Append(" (").Append(drive.Label).Append(')');
                sb.Append(" — ").Append(StateText(drive.State));

                sb.AppendLine();
                sb.Append("    ").Append(Films(drive.FilmCount));

                if (drive.State == DriveState.Connected && drive.TotalBytes is long total && drive.FreeBytes is long free)
                    sb.Append(" · ").Append(ByteSize.Describe(free)).Append(" free of ").Append(ByteSize.Describe(total));

                foreach (var folder in drive.WatchFolders)
                    sb.AppendLine().Append("    Library folder: ").Append(folder);
            }

            return sb.ToString();
        }

        public static string StateText(DriveState state) => state switch
        {
            DriveState.Connected => "connected",
            DriveState.Disconnected => "NOT CONNECTED",
            DriveState.NotResponding => "NOT RESPONDING",
            _ => state.ToString()
        };

        private static string Films(int count) =>
            count == 1 ? "1 film" : $"{count.ToString(CultureInfo.InvariantCulture)} films";

        private static IReadOnlyList<DriveReport> Down(IReadOnlyList<DriveReport>? reports) =>
            reports?.Where(r => r.IsDown).ToList() ?? (IReadOnlyList<DriveReport>)Array.Empty<DriveReport>();
    }
}
