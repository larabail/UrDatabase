using System;
using System.Collections.Generic;
using System.Linq;

namespace UrDatabase.Models
{
    /// <summary>How a scan ended, which is what decides whether anything may be concluded from it.</summary>
    public enum ScanStatus
    {
        /// <summary>Started, and no ending recorded. A row left this way is a scan the app died during.</summary>
        Running,

        /// <summary>Walked every folder it set out to. The only status a missing mark may follow.</summary>
        Completed,

        /// <summary>Stopped early on request, keeping what it had already catalogued.</summary>
        Cancelled,

        /// <summary>Stopped early on an error.</summary>
        Failed,
    }

    /// <summary>
    /// What a scan did, counted apart.
    ///
    /// The scan used to return one integer and a sentence built around it, and the integer meant
    /// "rows written" — which is a file that is new, a file that changed, and a file that was
    /// already exactly right, all reported as the same thing. "1,412 updated" after a scan that
    /// changed nothing is not a useful thing to tell somebody, and it is unfalsifiable: there is
    /// no number it could have printed that would have looked wrong.
    ///
    /// So the categories are disjoint and each one is a different fact about the library.
    /// <see cref="Inserted"/>, <see cref="Moved"/>, <see cref="Updated"/>, <see cref="Unchanged"/>
    /// and <see cref="Failed"/> partition the video files the scan walked past;
    /// <see cref="Missing"/> counts rows rather than files and is the one number that says
    /// something got smaller.
    ///
    /// A result object rather than a formatted string because the window is not the only thing
    /// that wants this — a test wants to assert on it, and the sentence a person reads is one
    /// rendering of it rather than the thing itself.
    /// </summary>
    public sealed record ScanResult(
        long ScanId,
        ScanStatus Status,
        int Inserted,
        int Moved,
        int Updated,
        int Unchanged,
        int Failed,
        int Missing,
        int MoviesAdded,
        IReadOnlyList<string> Roots,
        IReadOnlyList<string> SkippedRoots)
    {
        /// <summary>A scan that never got as far as walking anything.</summary>
        public static ScanResult Nothing { get; } = new(
            0, ScanStatus.Completed, 0, 0, 0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>());

        /// <summary>Video files the scan accounted for, however it accounted for them.</summary>
        public int FilesSeen => Inserted + Moved + Updated + Unchanged;

        /// <summary>
        /// The sentence the status line shows.
        ///
        /// The three that partition an ordinary scan are always present, including when they are
        /// zero, because "0 added" after re-scanning an unchanged library is the answer and an
        /// omitted line reads as a scan that did not run. The other three appear only when there
        /// is something to say: a library where nothing moved, nothing failed and nothing went
        /// missing should not have to read three zeroes to find that out.
        /// </summary>
        public string Summary
        {
            get
            {
                if (Status == ScanStatus.Cancelled && FilesSeen == 0 && Failed == 0)
                    return "Scan cancelled before anything was catalogued. Nothing was marked missing.";

                var parts = new List<string>
                {
                    $"{Inserted} added",
                    $"{Updated} updated",
                    $"{Unchanged} unchanged",
                };

                if (Moved > 0) parts.Add($"{Moved} moved");
                if (Failed > 0) parts.Add($"{Failed} failed");
                if (Missing > 0) parts.Add($"{Missing} now missing");

                var counts = string.Join(", ", parts);

                return Status switch
                {
                    // A cancelled scan keeps what it catalogued and concludes nothing about the
                    // rest, and saying so is the point: the numbers below it describe part of a
                    // library, and nothing was marked missing on their account.
                    ScanStatus.Cancelled => $"Scan cancelled — {counts}. Nothing was marked missing.",
                    ScanStatus.Failed => $"Scan failed — {counts}.",
                    _ => $"Scan complete. {counts}.",
                };
            }
        }

        internal static ScanResult From(
            long scanId,
            ScanStatus status,
            ScanCounts counts,
            int moviesAdded,
            IEnumerable<string> roots,
            IEnumerable<string> skippedRoots) =>
            new(scanId,
                status,
                counts.Inserted,
                counts.Moved,
                counts.Updated,
                counts.Unchanged,
                counts.Failed,
                counts.Missing,
                moviesAdded,
                roots.ToList(),
                skippedRoots.ToList());
    }

    /// <summary>The mutable tally a scan keeps while it runs, before it becomes a result.</summary>
    internal sealed class ScanCounts
    {
        public int Inserted;
        public int Moved;
        public int Updated;
        public int Unchanged;
        public int Failed;
        public int Missing;
    }
}
