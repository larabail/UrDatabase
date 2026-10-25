using System;
using System.Collections.Generic;
using System.Linq;

namespace UrDatabase.Services
{
    /// <summary>
    /// Puts TMDB's television genres on the shelves the films already use.
    /// </summary>
    /// <remarks>
    /// TMDB keeps one genre list for films and another for television, and they do not agree.
    /// Television folds pairs of film genres into one — <c>Sci-Fi &amp; Fantasy</c>,
    /// <c>Action &amp; Adventure</c>, <c>War &amp; Politics</c> — so storing the names as TMDB sends
    /// them would give a library a "Sci-Fi &amp; Fantasy" shelf beside its "Science Fiction" one,
    /// with the programmes on one and the films on the other. The genres column is what the
    /// shelves are built from, so the folding happens here, once, before anything is written.
    ///
    /// A compound this table does not know is still split on its ampersand: TMDB adds genres
    /// rarely, and two plain names are a better guess than a shelf nobody else is on. Everything
    /// else — Drama, Comedy, Crime, and the television-only Kids, News, Reality, Soap and Talk —
    /// passes through as it came.
    /// </remarks>
    public static class TelevisionGenres
    {
        private static readonly Dictionary<string, string[]> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Sci-Fi & Fantasy"] = new[] { "Science Fiction", "Fantasy" },
            ["Action & Adventure"] = new[] { "Action", "Adventure" },
            ["War & Politics"] = new[] { "War", "Politics" },
        };

        /// <summary>Single names that television spells differently from the film list.</summary>
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Sci-Fi"] = "Science Fiction",
            ["SciFi"] = "Science Fiction",
        };

        /// <summary>The shelf names one TMDB television genre belongs on. Empty for a blank one.</summary>
        public static IReadOnlyList<string> Fold(string? name)
        {
            var trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0) return Array.Empty<string>();

            if (Known.TryGetValue(trimmed, out var known)) return known;

            if (!trimmed.Contains('&')) return new[] { Alias(trimmed) };

            return trimmed
                .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part => part.Length > 0)
                .Select(Alias)
                .ToArray();
        }

        /// <summary>
        /// The comma separated list the catalogue stores, folded and without repeats, or null when
        /// there is nothing to store — null rather than empty, so a caller that leaves the column
        /// alone on null does not blank genres somebody already has.
        /// </summary>
        public static string? Join(IEnumerable<string?>? names)
        {
            if (names is null) return null;

            var folded = new List<string>();
            foreach (var name in names)
            foreach (var shelf in Fold(name))
            {
                if (folded.Contains(shelf, StringComparer.OrdinalIgnoreCase)) continue;
                folded.Add(shelf);
            }

            return folded.Count == 0 ? null : string.Join(", ", folded);
        }

        private static string Alias(string name) => Aliases.TryGetValue(name, out var alias) ? alias : name;
    }
}
