using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// The rules about reported problems that are not about storage: what each one is called,
    /// how it is written to disk, which film it belongs to, and what the card and the details
    /// screen say about it.
    /// </summary>
    public static class FilmProblems
    {
        /// <summary>Every kind, in the order the details screen offers them.</summary>
        public static IReadOnlyList<FilmProblemKind> All { get; } =
            (FilmProblemKind[])Enum.GetValues(typeof(FilmProblemKind));

        /// <summary>The word on the card. One word, like the other card badges.</summary>
        public const string BadgeText = "Problem";

        /// <summary>
        /// What a problem is called on screen. Spelled out rather than taken from the enum name,
        /// which would print "LowResolution".
        /// </summary>
        public static string Label(FilmProblemKind kind) => kind switch
        {
            FilmProblemKind.Audio => "Audio",
            FilmProblemKind.LowResolution => "Low resolution",
            FilmProblemKind.AspectRatio => "Wrong aspect ratio",
            FilmProblemKind.Subtitles => "Subtitles",
            FilmProblemKind.Picture => "Picture glitches",
            FilmProblemKind.Other => "Other",
            _ => kind.ToString()
        };

        /// <summary>
        /// How a problem is written to the catalogue. Stable strings rather than the enum's
        /// numbers, so reordering the enum, or adding to it, cannot turn one person's "audio" into
        /// somebody else's "subtitles".
        /// </summary>
        public static string Code(FilmProblemKind kind) => kind switch
        {
            FilmProblemKind.Audio => "audio",
            FilmProblemKind.LowResolution => "low-resolution",
            FilmProblemKind.AspectRatio => "aspect-ratio",
            FilmProblemKind.Subtitles => "subtitles",
            FilmProblemKind.Picture => "picture",
            FilmProblemKind.Other => "other",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        /// <summary>The kinds as one column value: distinct, in enum order, comma separated.</summary>
        public static string Encode(IEnumerable<FilmProblemKind>? kinds) =>
            string.Join(",", Normalise(kinds).Select(Code));

        /// <summary>
        /// Reads a column value back. A code this build does not know — written by a newer one —
        /// is skipped rather than failing the read: losing one tick is better than losing the
        /// library.
        /// </summary>
        public static IReadOnlyList<FilmProblemKind> Decode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<FilmProblemKind>();

            var found = new List<FilmProblemKind>();

            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var kind in All)
                {
                    if (string.Equals(Code(kind), part, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(kind);
                        break;
                    }
                }
            }

            return Normalise(found);
        }

        /// <summary>Distinct and in a fixed order, so two saves of the same ticks are the same row.</summary>
        public static IReadOnlyList<FilmProblemKind> Normalise(IEnumerable<FilmProblemKind>? kinds) =>
            (kinds ?? Array.Empty<FilmProblemKind>())
                .Where(k => Enum.IsDefined(typeof(FilmProblemKind), k))
                .Distinct()
                .OrderBy(k => k)
                .ToList();

        /// <summary>
        /// Which film a report is about, from what the details screen knows. The same string
        /// <see cref="UiMovie.Key"/> produces for the card that was clicked, so a report saved on
        /// the details screen is found again by the library.
        /// </summary>
        /// <remarks>
        /// A catalogued film is keyed on its row id even when its file is gone and it is being
        /// streamed instead, because that is the identity the card keeps. Null when there is
        /// nothing to key on, which only happens to a screen shown with no film behind it.
        /// </remarks>
        public static string? KeyFor(long localId, string? remoteId)
        {
            if (localId > 0) return $"local:{localId.ToString(CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrWhiteSpace(remoteId)) return $"jellyfin:{remoteId}";
            return null;
        }

        /// <summary>
        /// One line naming everything reported: <c>"Audio, Wrong aspect ratio — dialogue out of
        /// sync"</c>. Empty when there is nothing.
        /// </summary>
        public static string Summary(FilmProblemReport? report)
        {
            if (report is null || report.IsEmpty) return "";

            var kinds = string.Join(", ", report.Kinds.Select(Label));

            if (report.Note.Length == 0) return kinds;
            if (kinds.Length == 0) return report.Note;

            return $"{kinds} — {report.Note}";
        }

        /// <summary>
        /// Puts each film's report on its card, and takes it off a card whose report has been
        /// cleared. Every card is written, so a list stamped twice says what the second read said.
        /// </summary>
        public static void Stamp(IEnumerable<UiMovie>? films, IReadOnlyDictionary<string, FilmProblemReport>? reports)
        {
            if (films is null) return;

            foreach (var film in films)
            {
                if (film is null) continue;

                film.ProblemReport = reports is not null && reports.TryGetValue(film.Key, out var report) && !report.IsEmpty
                    ? report
                    : null;
            }
        }
    }
}
