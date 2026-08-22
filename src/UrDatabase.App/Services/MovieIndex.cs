using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Decides which catalogue entry a parsed filename belongs to. Purely in memory: it is handed
    /// the movies that already exist and answers questions about them, so the matching rules can
    /// be tested without a database and a scan can resolve thousands of files without a query each.
    ///
    /// The rules exist to make a re-scan idempotent. Two files that name the same film must land on
    /// one movie row however they spell it, and a second scan of an unchanged folder must create
    /// nothing at all.
    /// </summary>
    public sealed class MovieIndex
    {
        private readonly Dictionary<string, long> _byTitleAndYear = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _byTitle = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _yearlessByTitle = new(StringComparer.Ordinal);

        /// <summary>Distinct movies known to the index.</summary>
        public int Count { get; private set; }

        /// <summary>
        /// Folds a title down to what two spellings of the same film have in common: case,
        /// accents, punctuation and the difference between "&amp;" and "and" all disappear, so
        /// "Amélie", "amelie" and "AMELIE" agree, as do "Spider-Man" and "Spider Man".
        /// </summary>
        public static string NormalizeTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";

            var folded = RemoveDiacritics(title).ToLowerInvariant();
            var builder = new StringBuilder(folded.Length);

            foreach (var ch in folded)
            {
                if (char.IsLetterOrDigit(ch)) builder.Append(ch);
                else if (ch == '&') builder.Append(" and ");
                else if (ch is '\'' or '\u2019' or '`') { /* Ocean's == Oceans */ }
                else builder.Append(' ');
            }

            return CollapseWhitespace(builder.ToString());
        }

        /// <summary>
        /// The key the index files a movie under. Normally the normalised title; for the rare name
        /// that is nothing but punctuation ("+++.mkv") normalisation leaves nothing to key on, and
        /// falling back to the raw text keeps such a film findable. Without the fallback the row
        /// would be invisible to the index and every scan would insert another copy of it.
        /// </summary>
        private static string IndexKey(string? title)
        {
            var normalized = NormalizeTitle(title);
            if (normalized.Length > 0) return normalized;

            return CollapseWhitespace((title ?? "").Trim().ToLowerInvariant());
        }

        /// <summary>
        /// The identity of a movie as far as a scan is concerned. A missing year is part of the
        /// key rather than a wildcard, because "The Thing" without a year genuinely might not be
        /// the 1982 one.
        /// </summary>
        public static string BuildKey(string? title, int? year) =>
            year.HasValue
                ? $"{IndexKey(title)}|{year.Value.ToString(CultureInfo.InvariantCulture)}"
                : $"{IndexKey(title)}|";

        public static string BuildKey(ParsedMedia media) => BuildKey(media.Title, media.Year);

        /// <summary>
        /// True when two parsed names describe the same film. A year-less name matches one that
        /// carries a year, which is what lets "The Matrix.mkv" join "The Matrix (1999).mkv"
        /// instead of splitting the library in two.
        /// </summary>
        public static bool AreSameMovie(ParsedMedia left, ParsedMedia right)
        {
            var leftTitle = IndexKey(left.Title);
            if (leftTitle.Length == 0 || leftTitle != IndexKey(right.Title)) return false;

            return !left.Year.HasValue || !right.Year.HasValue || left.Year == right.Year;
        }

        /// <summary>
        /// Registers an existing movie. Rows are expected in id order, so the oldest row wins any
        /// ambiguity and a scan keeps pointing at whatever a previous scan created.
        /// </summary>
        public void Add(long id, string? title, int? year)
        {
            var normalized = IndexKey(title);
            if (normalized.Length == 0) return;

            if (_byTitleAndYear.TryAdd(BuildKey(title, year), id)) Count++;
            _byTitle.TryAdd(normalized, id);

            if (!year.HasValue) _yearlessByTitle.TryAdd(normalized, id);
        }

        /// <summary>
        /// Finds the movie a parsed filename belongs to.
        /// </summary>
        /// <param name="yearIsNewInformation">
        /// Set when the match is a row that has no year and the filename supplies one. The caller
        /// is expected to write it back: a folder gradually renamed to include years should fill
        /// the catalogue in, not fork it.
        /// </param>
        public bool TryResolve(ParsedMedia media, out long id, out bool yearIsNewInformation)
        {
            id = 0;
            yearIsNewInformation = false;

            var normalized = IndexKey(media.Title);
            if (normalized.Length == 0) return false;

            if (_byTitleAndYear.TryGetValue(BuildKey(media.Title, media.Year), out id))
                return true;

            if (media.Year.HasValue)
            {
                if (_yearlessByTitle.TryGetValue(normalized, out id))
                {
                    yearIsNewInformation = true;
                    return true;
                }

                return false;
            }

            return _byTitle.TryGetValue(normalized, out id);
        }

        /// <summary>
        /// Records that a previously year-less movie now has a year, keeping the index in step
        /// with the row the caller just updated.
        /// </summary>
        public void SetYear(long id, string? title, int year)
        {
            var normalized = IndexKey(title);
            if (normalized.Length == 0) return;

            if (_yearlessByTitle.TryGetValue(normalized, out var existing) && existing == id)
                _yearlessByTitle.Remove(normalized);

            var yearlessKey = BuildKey(title, null);
            if (_byTitleAndYear.TryGetValue(yearlessKey, out var owner) && owner == id)
                _byTitleAndYear.Remove(yearlessKey);

            _byTitleAndYear[BuildKey(title, year)] = id;
            _byTitle.TryAdd(normalized, id);
        }

        private static string RemoveDiacritics(string text)
        {
            var decomposed = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);

            foreach (var ch in decomposed)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    builder.Append(ch);

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        private static string CollapseWhitespace(string text)
        {
            var builder = new StringBuilder(text.Length);
            var pendingSpace = false;

            foreach (var ch in text)
            {
                if (ch == ' ')
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace) builder.Append(' ');
                pendingSpace = false;
                builder.Append(ch);
            }

            return builder.ToString();
        }
    }
}
