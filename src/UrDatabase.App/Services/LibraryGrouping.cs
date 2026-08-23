using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        /// <summary>
        /// The genre row across the top, each entry carrying how many films are behind it.
        /// </summary>
        /// <remarks>
        /// The counts are the point. A genre row without them tells you a library has a Western
        /// bucket but not whether it holds two films or two hundred, and the first thing anyone
        /// wants from a catalogue is its shape. "All" counts distinct films, not the sum of the
        /// buckets: a film with three genres appears on three shelves, so adding the buckets up
        /// reports a library several times larger than it is.
        /// </remarks>
        public static IReadOnlyList<GenreChip> BuildGenreChips(IEnumerable<UiMovie>? movies)
        {
            var materialised = movies as IReadOnlyCollection<UiMovie> ?? movies?.ToList();

            if (materialised is null || materialised.Count == 0)
                return new List<GenreChip> { new() { Name = AllGenres, Count = 0 } };

            var chips = new List<GenreChip>
            {
                new()
                {
                    Name = AllGenres,
                    // Distinct on Key: every server film carries local id 0, so counting on the
                    // id alone would report the whole remote library as a single film.
                    Count = materialised.Select(m => m.Key).Distinct(StringComparer.Ordinal).Count()
                }
            };

            foreach (var genre in BuildGenreList(materialised))
            {
                if (string.Equals(genre, AllGenres, StringComparison.OrdinalIgnoreCase)) continue;

                chips.Add(new GenreChip
                {
                    Name = genre,
                    Count = ItemsForGenre(materialised, genre).Count
                });
            }

            return chips;
        }

        /// <summary>
        /// The count as it is printed beside a shelf heading: <c>"12 FILMS"</c>, <c>"1 FILM"</c>.
        /// </summary>
        /// <remarks>
        /// Singular and plural are handled here rather than in a format string in the view,
        /// because "1 films" is the sort of thing that survives review for years.
        /// </remarks>
        public static string CountLabel(int count)
            => count == 1 ? "1 FILM" : $"{count} FILMS";

        /// <summary>
        /// Every shelf the library page shows, in the order it shows them: Continue watching
        /// first, then one per genre.
        /// </summary>
        /// <remarks>
        /// Built here rather than in the window so the ordering is a rule rather than the order
        /// somebody happened to write two loops in. Continue watching goes above every genre
        /// because it is the only shelf that answers a question the viewer already has when they
        /// open the app; a genre answers one they are browsing for.
        ///
        /// It is a shelf and never a chip: it is not a genre, nothing is in it by being a kind of
        /// film, and a filter called "Continue watching" would be a fourth thing competing with
        /// the source row for the same corner of the screen.
        ///
        /// An empty row is left out entirely rather than shown empty. A heading with nothing under
        /// it reads as a shelf that failed to load, and on an install with no server — or one
        /// whose owner finishes what they start — that would be the permanent state of the top of
        /// the page. A genre with nothing in it is dropped for the same reason.
        /// </remarks>
        public static IReadOnlyList<GenreGroup> BuildShelves(
            IEnumerable<UiMovie>? movies,
            IEnumerable<string>? genres,
            IReadOnlyList<UiMovie>? continueWatching = null)
        {
            var shelves = new List<GenreGroup>();

            if (continueWatching is { Count: > 0 })
            {
                shelves.Add(new GenreGroup
                {
                    Name = ResumeRow.Heading,
                    Count = continueWatching.Count,
                    Items = new ObservableCollection<UiMovie>(continueWatching)
                });
            }

            foreach (var genre in genres ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(genre)) continue;
                if (string.Equals(genre, AllGenres, StringComparison.OrdinalIgnoreCase)) continue;

                var items = ItemsForGenre(movies, genre);
                if (items.Count == 0) continue;

                shelves.Add(new GenreGroup
                {
                    Name = genre,
                    Count = items.Count,
                    Items = new ObservableCollection<UiMovie>(items)
                });
            }

            return shelves;
        }

    }
}
