using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Folds a server's films into the same list as the local ones.
    ///
    /// Out of the window and pure, because this is where the two sources meet and the rules for
    /// that are worth being able to assert: a remote film is never run through the filename
    /// parser, never sent to TMDB, and never silently replaces a local copy of the same title.
    /// Both are shown, because only one of them plays with the house network down.
    /// </summary>
    public static class JellyfinLibrary
    {
        /// <summary>
        /// One server item as a card. The poster URL is supplied by the caller because building
        /// it needs the server address, which belongs to configuration rather than to the film.
        /// </summary>
        public static UiMovie ToUiMovie(JellyfinMovie movie, Func<JellyfinMovie, string?>? posterUrl = null)
        {
            if (movie is null) throw new ArgumentNullException(nameof(movie));

            return new UiMovie
            {
                Id = 0,
                RemoteId = movie.ItemId,
                Source = MovieSource.Jellyfin,
                Title = movie.Title ?? "",
                Year = movie.Year,
                // Already a real list from Jellyfin's own metadata, so a server library never
                // piles into the "Uncategorised" bucket the way a freshly scanned one does.
                Genres = movie.Genres ?? "",
                PosterPath = posterUrl?.Invoke(movie)
            };
        }

        public static IReadOnlyList<UiMovie> ToUiMovies(
            IEnumerable<JellyfinMovie>? movies,
            Func<JellyfinMovie, string?>? posterUrl = null)
        {
            if (movies is null) return Array.Empty<UiMovie>();

            return movies
                .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.ItemId))
                .Select(m => ToUiMovie(m, posterUrl))
                .ToList();
        }

        /// <summary>
        /// The library the window shows: local films and server films in one ordering, with
        /// duplicates removed by identity rather than by title. A film that exists both on this
        /// disk and on the server legitimately appears twice — they behave differently, and
        /// hiding one would mean hiding the only one that works offline.
        /// </summary>
        public static IReadOnlyList<UiMovie> Merge(IEnumerable<UiMovie>? local, IEnumerable<UiMovie>? remote)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var combined = new List<UiMovie>();

            foreach (var movie in (local ?? Array.Empty<UiMovie>()).Concat(remote ?? Array.Empty<UiMovie>()))
            {
                if (movie is null) continue;
                if (seen.Add(movie.Key)) combined.Add(movie);
            }

            return combined
                .OrderByDescending(m => m.Year ?? 0)
                .ThenBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Searching the server's films. The local half of the library is searched through SQLite's
        /// full text index, which cannot see these rows, so they are matched in memory instead —
        /// a few hundred titles, already loaded, is not worth a second index to filter.
        /// </summary>
        public static IReadOnlyList<UiMovie> Search(IEnumerable<UiMovie>? movies, string? query)
        {
            if (movies is null) return Array.Empty<UiMovie>();

            var needle = (query ?? "").Trim();
            if (needle.Length == 0) return movies.ToList();

            return movies
                .Where(m =>
                    (m.Title ?? "").Contains(needle, StringComparison.CurrentCultureIgnoreCase) ||
                    (m.Genres ?? "").Contains(needle, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        }
    }
}
