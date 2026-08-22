using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Decides which genre bucket a film lands in, and in what order films appear inside one.
    ///
    /// Pure, and out of the window, because the grouped view is what a user actually sees. Genres
    /// only arrive with TMDB enrichment, so a freshly scanned library — or any library on a build
    /// with no API key — has none at all. Grouping strictly by genre would render that library as
    /// a blank page, which is indistinguishable from the scan having failed.
    /// </summary>
    public static class LibraryGrouping
    {
        /// <summary>The chip that shows every bucket at once.</summary>
        public const string AllGenres = "All";

        /// <summary>The bucket for films no genre is known for yet.</summary>
        public const string Uncategorised = "Uncategorised";

        public static bool HasNoGenre(UiMovie movie) => !movie.GenresList.Any();

        /// <summary>
        /// The chips across the top: "All" first, then the genres present in the library, then
        /// "Uncategorised" if anything is still waiting on metadata.
        /// </summary>
        public static IReadOnlyList<string> BuildGenreList(IEnumerable<UiMovie>? movies)
        {
            var list = new List<string> { AllGenres };
            if (movies is null) return list;

            var materialised = movies as IReadOnlyCollection<UiMovie> ?? movies.ToList();

            list.AddRange(materialised
                .SelectMany(m => m.GenresList)
                .Select(g => g.Trim())
                .Where(g => g.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase));

            if (materialised.Any(HasNoGenre)) list.Add(Uncategorised);

            return list;
        }

        /// <summary>
        /// The films in one bucket, newest first. "Uncategorised" is not a genre any film claims,
        /// so it is matched on the absence of one.
        /// </summary>
        public static IReadOnlyList<UiMovie> ItemsForGenre(IEnumerable<UiMovie>? movies, string? genre)
        {
            if (movies is null || string.IsNullOrWhiteSpace(genre)) return Array.Empty<UiMovie>();

            var matches = string.Equals(genre, Uncategorised, StringComparison.OrdinalIgnoreCase)
                ? movies.Where(HasNoGenre)
                : movies.Where(m => m.HasGenre(genre));

            return matches
                .OrderByDescending(m => m.Year ?? 0)
                .ThenBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }
}
