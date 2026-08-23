using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Folds a server's films into the same list as the local ones.
    ///
    /// Out of the window and pure, because this is where the two sources meet and the rules for
    /// that are worth being able to assert: a remote film is never run through the filename
    /// parser and never sent to TMDB, and a film held in both places is one card carrying both
    /// facts rather than a server copy quietly replacing the local one that plays offline.
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
                TmdbId = ParseTmdbId(movie.TmdbId),
                // Already a real list from Jellyfin's own metadata, so a server library never
                // piles into the "Uncategorised" bucket the way a freshly scanned one does.
                Genres = movie.Genres ?? "",
                PosterPath = posterUrl?.Invoke(movie)
            };
        }

        /// <summary>
        /// Jellyfin reports provider ids as strings, and reports nothing at all for a film it could
        /// not identify. Anything that is not a positive whole number is treated as no id rather
        /// than as a zero, because zero would then match every other unidentified film on the
        /// server and fold them all onto one card.
        /// </summary>
        internal static int? ParseTmdbId(string? value) =>
            int.TryParse((value ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id
                : null;

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
        /// One television series as a card, on the same shelf as the films.
        /// </summary>
        /// <remarks>
        /// Deliberately the same type. A series carries genres exactly as a film does, so it
        /// belongs in the Drama shelf rather than in a wing of its own, and a second card type
        /// would have meant a second template, a second search path and a second set of counts to
        /// keep true.
        ///
        /// What stops it being mistakable for a film is <see cref="UiMovie.Kind"/>: the card shows
        /// a badge and a season count instead of a bare year, and the kind row can take television
        /// out of the library in one click. Without those two this fold would be dishonest.
        /// </remarks>
        public static UiMovie ToUiSeries(JellyfinSeries series, Func<JellyfinSeries, string?>? posterUrl = null)
        {
            if (series is null) throw new ArgumentNullException(nameof(series));

            return new UiMovie
            {
                Id = 0,
                RemoteId = series.ItemId,
                Source = MovieSource.Jellyfin,
                Kind = MediaKind.Series,
                Title = series.Title ?? "",
                Year = series.Year,

                // Not taken from the server's TMDB id, which for a series is a TMDB *television*
                // id: a different catalogue with its own numbering, where 1396 is a different
                // programme from film 1396. Folding a series onto a film by that number is exactly
                // the sort of mistake this app has already shipped once, so nothing here carries a
                // TMDB id at all and the fold below refuses series outright.
                TmdbId = null,

                Genres = series.Genres ?? "",
                SeasonCount = series.SeasonCount,
                EpisodeCount = series.EpisodeCount,
                PosterPath = posterUrl?.Invoke(series)
            };
        }

        public static IReadOnlyList<UiMovie> ToUiSeriesList(
            IEnumerable<JellyfinSeries>? series,
            Func<JellyfinSeries, string?>? posterUrl = null)
        {
            if (series is null) return Array.Empty<UiMovie>();

            return series
                .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.ItemId))
                .Select(s => ToUiSeries(s, posterUrl))
                .ToList();
        }

        /// <summary>
        /// The library the window shows: local films and server films in one ordering, with a film
        /// that is in both places appearing once, as a single card that says so.
        /// </summary>
        /// <remarks>
        /// This used to show both copies, on the reasoning that they behave differently and only
        /// one plays with the house network down. That is true of the copies and false of the
        /// film: a search for a title held in both places answered with two identical posters and
        /// no way to tell which was which without clicking, and a shelf counted it twice.
        ///
        /// So the two facts are folded onto one card, which carries a badge for each. The local
        /// row is the one kept, because everything else in the app hangs off it — the file that
        /// plays offline, the TMDB match, the poster the catalogue owns — and the server's id,
        /// genres and artwork are folded into it.
        ///
        /// Which films are the same film is decided by <see cref="MovieIndex"/>, the same rules a
        /// re-scan uses to avoid inserting a second row for a file it has already seen: titles
        /// normalised for case, accents and punctuation, and a missing year on either side
        /// treated as agreement. Deliberately not by TMDB or IMDb id — the local half of the
        /// library mostly has neither, so matching on them would fold almost nothing.
        /// </remarks>
        public static IReadOnlyList<UiMovie> Merge(IEnumerable<UiMovie>? local, IEnumerable<UiMovie>? remote)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var combined = new List<UiMovie>();

            var here = new MovieIndex();
            var byId = new Dictionary<long, UiMovie>();

            // The local films that have been identified, keyed by the film they are. Built
            // alongside the title index rather than instead of it: a film nothing has identified
            // has no entry here and still has to be foldable by name.
            var byTmdbId = new Dictionary<int, UiMovie>();

            foreach (var movie in local ?? Array.Empty<UiMovie>())
            {
                if (movie is null) continue;
                if (!seen.Add(movie.Key)) continue;

                combined.Add(movie);
                if (byId.TryAdd(movie.Id, movie)) here.Add(movie.Id, movie.Title, movie.Year);

                // First wins, as everywhere else here. Two local rows claiming one TMDB film is a
                // duplicate in the catalogue, and the server's copy belongs to whichever the rest
                // of this method already kept.
                if (movie.TmdbId is int tmdbId) byTmdbId.TryAdd(tmdbId, movie);
            }

            foreach (var movie in remote ?? Array.Empty<UiMovie>())
            {
                if (movie is null) continue;
                if (!seen.Add(movie.Key)) continue;

                if (TryFold(movie, here, byId, byTmdbId)) continue;

                combined.Add(movie);
            }

            return combined
                .OrderByDescending(m => m.Year ?? 0)
                .ThenBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Folds one server film into the local card for the same film, if there is one.
        /// </summary>
        /// <remarks>
        /// A local card that has already taken a server copy is left alone and the second server
        /// film is kept as a card of its own. Two items on a server can normalise to one title —
        /// a film and its remaster, or one entry that never got a year — and quietly overwriting
        /// the first with the second would lose a film from the library rather than merely
        /// showing it separately.
        /// </remarks>
        /// <summary>
        /// Folds a server film onto the local copy of the same film, when there is one.
        /// </summary>
        /// <remarks>
        /// Identity is tried before the name, because the two sources genuinely disagree about
        /// names. A film catalogued from <c>El Drama (The Drama) (2026).mkv</c> is <em>El Drama</em>
        /// here and <em>The Drama</em> on the server; normalisation folds case, accents and
        /// punctuation but not a translated title, so the wall showed one film twice with no way to
        /// tell it they were the same. A TMDB id is the same number on both sides whatever either
        /// calls the film.
        ///
        /// The name is still the fallback, and has to be: only a film something has identified has
        /// an id at all. A scanned film TMDB refused to match has none, and a server that could not
        /// identify a film reports none either.
        /// </remarks>
        private static bool TryFold(
            UiMovie server,
            MovieIndex here,
            IReadOnlyDictionary<long, UiMovie> byId,
            IReadOnlyDictionary<int, UiMovie> byTmdbId)
        {
            // Television never folds onto a film, whatever either is called. Nothing in the local
            // catalogue is a series — a scan records films — so a fold could only ever be wrong,
            // and the names collide often enough for that to matter: Fargo, Hannibal, Westworld
            // and Shōgun are each a film and a programme, and normalising the titles makes them
            // one. The card the fold kept would be the film, and the show would vanish from the
            // library rather than merely being shown beside it.
            if (server.IsSeries) return false;

            if (server.TmdbId is int tmdbId &&
                byTmdbId.TryGetValue(tmdbId, out var identified) &&
                !identified.IsOnServer)
            {
                identified.AdoptServerCopy(server);
                return true;
            }

            if (!here.TryResolve(new ParsedMedia(server.Title, server.Year), out var id, out _)) return false;
            if (!byId.TryGetValue(id, out var local) || local.IsOnServer) return false;

            local.AdoptServerCopy(server);
            return true;
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
