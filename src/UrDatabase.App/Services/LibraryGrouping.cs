using System;
using System.Collections.Generic;
using System.Globalization;
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
        /// <remarks>
        /// And now for television the server never identified, which is the one thing about this
        /// bucket that has changed. A scanned film has no genres because nothing has enriched it;
        /// a Jellyfin series usually has real ones, but a show the server could not identify has
        /// none either, and both mean the same thing — nobody has said what this is. They share a
        /// bucket for that reason rather than by accident. The kind row separates them in a click
        /// when the mixture is not what somebody wanted.
        /// </remarks>
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
            => count == 1 ? "1 FILM" : $"{count.ToString(CultureInfo.InvariantCulture)} FILMS";

        /// <summary>
        /// The same, for a shelf that may hold television as well: <c>"12 FILMS · 3 SERIES"</c>.
        /// </summary>
        /// <remarks>
        /// A shelf of eight films and four programmes headed "12 FILMS" is the exact failure that
        /// makes mixing the two on one shelf indefensible, and a count is the one place the
        /// mixture is stated as a number. Each half is named only when it is there, so a library
        /// with no television reads precisely as it did before — which is every library this app
        /// had until now.
        ///
        /// Episodes are a third population, and only ever appear in the Continue watching row.
        /// They are counted as episodes rather than folded into either of the other two: an
        /// episode is not a programme, and a row of two films and three episodes headed "5 FILMS"
        /// would be the same dishonesty in a smaller place.
        /// </remarks>
        public static string CountLabel(IEnumerable<UiMovie>? items)
        {
            var materialised = items as IReadOnlyCollection<UiMovie> ?? items?.ToList();
            if (materialised is null || materialised.Count == 0) return CountLabel(0);

            var films = materialised.Count(m => m.IsFilm);
            var series = materialised.Count(m => m.IsSeries);
            var episodes = materialised.Count(m => m.IsEpisode);

            if (series == 0 && episodes == 0) return CountLabel(films);

            var parts = new List<string>();

            if (films > 0) parts.Add(CountLabel(films));

            // "SERIES" is its own plural. Spelled once rather than through a conditional that
            // would read as though one of the two branches did something.
            if (series > 0) parts.Add($"{series.ToString(CultureInfo.InvariantCulture)} SERIES");

            if (episodes > 0)
            {
                parts.Add(episodes == 1
                    ? "1 EPISODE"
                    : $"{episodes.ToString(CultureInfo.InvariantCulture)} EPISODES");
            }

            return parts.Count == 0 ? CountLabel(0) : string.Join(" · ", parts);
        }

        /// <summary>
        /// What the search field offers to search, which is the cheapest possible answer to "did
        /// the scan actually find anything". Names television only when there is some.
        /// </summary>
        public static string SearchWatermark(IEnumerable<UiMovie>? items)
        {
            IReadOnlyCollection<UiMovie> materialised =
                items as IReadOnlyCollection<UiMovie> ?? items?.ToList() ?? (IReadOnlyCollection<UiMovie>)Array.Empty<UiMovie>();

            var films = materialised.Count(m => m.IsFilm);
            var series = materialised.Count(m => m.IsSeries);

            var filmPart = films == 1 ? "1 film" : $"{films:N0} films";
            var seriesPart = series == 1 ? "1 series" : $"{series:N0} series";

            if (series == 0) return $"Search {filmPart}";
            if (films == 0) return $"Search {seriesPart}";

            return $"Search {filmPart} and {seriesPart}";
        }

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
