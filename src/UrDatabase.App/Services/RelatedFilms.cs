using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Picks the films already in the library that are worth offering next to the one on screen.
    /// </summary>
    /// <remarks>
    /// The point of the shelf is what it deliberately excludes. TMDB will happily recommend
    /// twenty films, and nineteen of them are things this user does not own — a row of posters
    /// that cannot be played is an advertisement, not a library. So TMDB supplies the ordering and
    /// the catalogue supplies the contents: what is shown is the intersection, in TMDB's own
    /// relevance order, and nothing else.
    ///
    /// The fallback matters as much. An install with no TMDB key, a film nothing has identified,
    /// and a film TMDB has no recommendations for are all ordinary rather than exceptional, and
    /// each of them would leave the shelf empty. Shared genres answer the same question badly but
    /// answer it, so they are used when the good answer is unavailable — and the caller is told
    /// which of the two it got, because a shelf headed "More like this" that is really "other
    /// science fiction" is overstating what the app knows.
    ///
    /// Pure: no database, no network. The library is handed in as the rows the window already has.
    /// </remarks>
    public static class RelatedFilms
    {
        /// <summary>
        /// How many posters the shelf holds. It is one row that does not scroll vertically, and a
        /// film's recommendations run to twenty — past about eight the row is wider than the
        /// window and the tail is unreachable.
        /// </summary>
        public const int Max = 8;

        /// <summary>
        /// The films to show, and on what basis they were chosen.
        /// </summary>
        /// <param name="recommended">
        /// TMDB's recommendations, in its order. Empty when there is no key, no identification, or
        /// nothing to recommend.
        /// </param>
        /// <param name="library">Every card the window is holding, films and series alike.</param>
        /// <param name="film">The film on screen, which must never appear on its own shelf.</param>
        public static RelatedShelf For(
            IEnumerable<TmdbMatch.Candidate>? recommended,
            IEnumerable<UiMovie>? library,
            MovieDetailsVm? film,
            int max = Max)
        {
            var rows = library?.Where(m => m is not null).ToList() ?? new List<UiMovie>();
            if (rows.Count == 0 || film is null || max <= 0) return RelatedShelf.Empty;

            var owned = ByTmdbId(rows, film);

            var matched = new List<UiMovie>();
            var seen = new HashSet<long>();

            foreach (var candidate in recommended ?? Array.Empty<TmdbMatch.Candidate>())
            {
                if (candidate is null || candidate.Id <= 0) continue;
                if (!owned.TryGetValue(candidate.Id, out var mine)) continue;
                if (!seen.Add(mine.Id)) continue;

                matched.Add(mine);
                if (matched.Count >= max) break;
            }

            if (matched.Count > 0) return new RelatedShelf(matched, RelatedBasis.Recommended);

            var byGenre = SharingGenres(rows, film, max);
            return byGenre.Count > 0
                ? new RelatedShelf(byGenre, RelatedBasis.Genre)
                : RelatedShelf.Empty;
        }

        /// <summary>
        /// The library indexed by TMDB id, with the film on screen left out.
        /// </summary>
        /// <remarks>
        /// Series are excluded outright. A programme and a film can share a TMDB id — the two
        /// catalogues are numbered separately — so a recommendation for film 1399 would otherwise
        /// pull in whichever series happened to be 1399 on the television side.
        ///
        /// Excluding the film itself needs both identities, not just one: a card can be a local
        /// row, a server item, or one card standing for both, and a film recommends itself often
        /// enough that leaving it in would put the poster you are already looking at first on its
        /// own shelf.
        /// </remarks>
        private static Dictionary<int, UiMovie> ByTmdbId(IReadOnlyList<UiMovie> rows, MovieDetailsVm film)
        {
            var index = new Dictionary<int, UiMovie>();

            foreach (var row in rows)
            {
                if (row.IsSeries) continue;
                if (row.TmdbId is not int id || id <= 0) continue;
                if (IsTheFilmItself(row, film)) continue;

                // First wins: the library has already folded a film held in both places onto one
                // card, so a duplicate here is two genuinely different rows and the earlier one is
                // the one the window would show.
                if (!index.ContainsKey(id)) index[id] = row;
            }

            return index;
        }

        private static bool IsTheFilmItself(UiMovie row, MovieDetailsVm film)
        {
            if (film.LocalId > 0 && row.Id == film.LocalId) return true;

            if (!string.IsNullOrWhiteSpace(film.RemoteId) &&
                string.Equals(row.RemoteId, film.RemoteId, StringComparison.OrdinalIgnoreCase))
                return true;

            return film.TmdbId is int mine && mine > 0 && row.TmdbId == mine;
        }

        /// <summary>
        /// The weak answer: films sharing a genre, most shared first and newest among equals.
        /// </summary>
        /// <remarks>
        /// Ordered by how many genres overlap rather than by whether any do, because "also Drama"
        /// is true of half a library and says nothing, while "also Science Fiction and Horror" is
        /// a real resemblance. A scanned film has no genres at all until something fills them in,
        /// so on a purely local library this usually returns nothing — which is the honest outcome
        /// and is why the shelf hides rather than showing a heading over an empty row.
        /// </remarks>
        internal static List<UiMovie> SharingGenres(IReadOnlyList<UiMovie> rows, MovieDetailsVm film, int max)
        {
            var wanted = Split(film.Genres);
            if (wanted.Count == 0) return new List<UiMovie>();

            return rows
                .Where(r => !r.IsSeries && !IsTheFilmItself(r, film))
                .Select(r => (Film: r, Shared: Split(r.Genres).Count(wanted.Contains)))
                .Where(x => x.Shared > 0)
                .OrderByDescending(x => x.Shared)
                .ThenByDescending(x => x.Film.Year ?? 0)
                .ThenBy(x => x.Film.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(max)
                .Select(x => x.Film)
                .ToList();
        }

        /// <summary>
        /// Genres as a set. Compared case-insensitively because the two sources spell them
        /// independently — TMDB says "Science Fiction" and a server's metadata agent may not.
        /// </summary>
        internal static HashSet<string> Split(string? genres) =>
            string.IsNullOrWhiteSpace(genres)
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : genres.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(g => g.Trim())
                        .Where(g => g.Length > 0)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What the shelf holds, and how honestly it can be described.</summary>
    public sealed class RelatedShelf
    {
        public RelatedShelf(IReadOnlyList<UiMovie> films, RelatedBasis basis)
        {
            Films = films;
            Basis = basis;
        }

        public IReadOnlyList<UiMovie> Films { get; }

        public RelatedBasis Basis { get; }

        public bool Any => Films.Count > 0;

        /// <summary>
        /// The heading, which says which question was actually answered. A genre shelf is not
        /// claiming these are like the film, only that they share a shelf in the library.
        /// </summary>
        public string Heading => Basis switch
        {
            RelatedBasis.Recommended => "WATCH NEXT, FROM YOUR LIBRARY",
            RelatedBasis.Genre => "MORE OF THE SAME, FROM YOUR LIBRARY",
            _ => ""
        };

        public static readonly RelatedShelf Empty = new(Array.Empty<UiMovie>(), RelatedBasis.None);
    }

    public enum RelatedBasis
    {
        None,

        /// <summary>TMDB's recommendations, narrowed to what the user owns.</summary>
        Recommended,

        /// <summary>Shared genres, used when there were no recommendations to narrow.</summary>
        Genre
    }
}
