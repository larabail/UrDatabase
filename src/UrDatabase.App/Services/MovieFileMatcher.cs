using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UrDatabase.Services
{
    /// <summary>
    /// Heuristic link between a catalogued movie and a file on disk. Extracted from the main
    /// window so the matching rules can be tested, and so comparisons stay ordinal — the app
    /// runs on both case-insensitive (NTFS, APFS) and case-sensitive (HFSX, ext4) filesystems.
    ///
    /// This is a <em>suggestion</em> and nothing more. The authoritative answer to "which file is
    /// this film" is <c>files.movie_id</c>, which the scanner writes and
    /// <see cref="PlayTargetResolver"/> reads; a name is only evidence about a file, and a weak
    /// kind of evidence at that. Nothing here should be opened without a person agreeing to it
    /// first.
    ///
    /// The rules are deliberately reluctant. The bug that produced them matched a title anywhere
    /// inside a filename, so the film <em>It</em> matched <c>Spirited Away.mkv</c> — "it" is
    /// inside "spirited" — and the Play button opened a different film entirely. Refusing to
    /// answer costs a user one click on <em>Link File…</em>; answering wrongly wastes their
    /// evening. So a match has to land on word boundaries, must not contradict the year, and must
    /// be the only candidate of its strength.
    /// </summary>
    public static class MovieFileMatcher
    {
        /// <summary>
        /// A normalised title this short is not evidence on its own — <em>It</em>, <em>Up</em>,
        /// <em>Her</em> and <em>Dune</em> all turn up at a word boundary inside longer names, so
        /// a loose match on one needs the year to corroborate it. An exact filename is still
        /// accepted: <c>Up.mkv</c> is not ambiguous about anything.
        /// </summary>
        private const int ShortTitleLength = 5;

        private static readonly char[] PathSeparators = { '/', '\\' };

        // Ordered worst to best. A candidate that ties with another at the winning rank is
        // discarded rather than picked, because "two equally good answers" is not an answer.
        private const int Rejected = 0;
        private const int LooseMatch = 1;
        private const int LooseMatchWithYear = 2;
        private const int ExactName = 3;
        private const int ExactNameWithYear = 4;

        public static string? FindBestMatch(IEnumerable<string> filePaths, string? title) =>
            FindBestMatch(filePaths, title, null);

        /// <summary>
        /// The best filename for a title, or null when there is no answer worth offering.
        /// </summary>
        /// <param name="year">
        /// The catalogued release year, when there is one. It is what separates a remake from its
        /// original: a filename naming a different year is rejected outright rather than ranked
        /// below, because <c>Dune (1984).mkv</c> is not a worse answer for the 2021 film, it is
        /// the wrong one.
        /// </param>
        public static string? FindBestMatch(IEnumerable<string> filePaths, string? title, int? year)
        {
            if (filePaths is null || string.IsNullOrWhiteSpace(title)) return null;

            var needle = Normalise(title);
            if (needle.Length == 0) return null;

            var isShortTitle = needle.Length <= ShortTitleLength;

            string? best = null;
            var bestRank = Rejected;
            var tied = false;

            foreach (var path in filePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                var rank = Rank(path, needle, year, isShortTitle);
                if (rank == Rejected) continue;

                if (rank > bestRank)
                {
                    best = path;
                    bestRank = rank;
                    tied = false;
                }
                else if (rank == bestRank && !string.Equals(path, best, StringComparison.Ordinal))
                {
                    tied = true;
                }
            }

            return tied ? null : best;
        }

        private static int Rank(string path, string needle, int? year, bool isShortTitle)
        {
            var stem = Normalise(FileStem(path));
            if (stem.Length == 0) return Rejected;

            var at = IndexOfAtTokenBoundary(stem, needle);
            if (at < 0) return Rejected;

            var isExact = stem.Length == needle.Length;

            // Years are read from whatever is left once the title is taken out, so "Blade Runner
            // 2049 (2017)" is the 2017 release of a film with a number in its name rather than a
            // release from 2049.
            var years = YearsIn(stem.Remove(at, needle.Length));

            var yearMatches = year.HasValue && years.Contains(year.Value);
            var yearContradicts = year.HasValue && !yearMatches && years.Count > 0;

            if (yearContradicts) return Rejected;
            if (isShortTitle && !isExact && !yearMatches) return Rejected;

            if (isExact) return yearMatches ? ExactNameWithYear : ExactName;
            return yearMatches ? LooseMatchWithYear : LooseMatch;
        }

        /// <summary>
        /// The filename without its directory or extension. Both separators are honoured because
        /// a Windows path reaches a macOS build through configuration and test data alike, and
        /// <c>Path.GetFileName</c> only knows the separator of the host OS.
        /// </summary>
        private static string FileStem(string path)
        {
            var lastSeparator = path.LastIndexOfAny(PathSeparators);
            var name = lastSeparator >= 0 ? path[(lastSeparator + 1)..] : path;

            var dot = name.LastIndexOf('.');
            return dot > 0 ? name[..dot] : name;
        }

        /// <summary>
        /// Reduces a title or a filename to lower-case words separated by single spaces, so that
        /// "the.matrix.1999.BluRay" and "The Matrix (1999)" can be compared at all.
        ///
        /// Lower-casing is invariant and every comparison afterwards is ordinal. A culture-aware
        /// comparison would decide the Turkish dotless ı is not an i, which is not something a
        /// film library should depend on the user's locale for.
        /// </summary>
        private static string Normalise(string text)
        {
            var builder = new StringBuilder(text.Length);
            var pendingSpace = false;

            foreach (var ch in text)
            {
                // Dropped rather than turned into a space: "Ocean's Eleven" and "Oceans Eleven"
                // are the same film, and splitting the word would stop them matching.
                if (ch is '\'' or '\u2019') continue;

                if (char.IsLetterOrDigit(ch))
                {
                    if (pendingSpace && builder.Length > 0) builder.Append(' ');
                    pendingSpace = false;
                    builder.Append(char.ToLowerInvariant(ch));
                }
                else
                {
                    pendingSpace = true;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Where <paramref name="needle"/> appears in <paramref name="haystack"/> as whole words,
        /// or -1. The word boundary is the whole point: a plain substring search puts "it" inside
        /// "spirited", which is how the Play button came to open the wrong film.
        /// </summary>
        private static int IndexOfAtTokenBoundary(string haystack, string needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length) return -1;

            var from = 0;
            while (from <= haystack.Length - needle.Length)
            {
                var at = haystack.IndexOf(needle, from, StringComparison.Ordinal);
                if (at < 0) return -1;

                var startsWord = at == 0 || haystack[at - 1] == ' ';
                var endsWord = at + needle.Length == haystack.Length || haystack[at + needle.Length] == ' ';
                if (startsWord && endsWord) return at;

                from = at + 1;
            }

            return -1;
        }

        /// <summary>
        /// Every standalone four-digit word that could be a release year. "2160p" is not one
        /// because it is not four digits, and "2049" is not one because no film has been released
        /// yet — <see cref="FilenameParser.IsPlausibleYear"/> owns that judgement so the scanner
        /// and the matcher cannot drift apart on it.
        /// </summary>
        private static HashSet<int> YearsIn(string normalised)
        {
            var years = new HashSet<int>();

            foreach (var token in normalised.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length != 4) continue;
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var year)) continue;
                if (FilenameParser.IsPlausibleYear(year)) years.Add(year);
            }

            return years;
        }
    }
}
