using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The shelf of films to put on next.
    ///
    /// The whole design rests on one rule: TMDB supplies the ordering and the catalogue supplies
    /// the contents. A shelf of films the user does not own is an advertisement, and a library
    /// application that advertises is worse than one with an empty space on it.
    /// </summary>
    public class RelatedFilmsTests
    {
        private static UiMovie Film(long id, string title, int? tmdbId = null, string? genres = null, int? year = 2000)
            => new()
            {
                Id = id,
                Title = title,
                Year = year,
                TmdbId = tmdbId,
                Genres = genres,
                Kind = MediaKind.Film
            };

        private static TmdbMatch.Candidate Recommendation(int id) => new() { Id = id };

        private static MovieDetailsVm Open(long localId = 1, int? tmdbId = 100, string? genres = null)
            => new() { LocalId = localId, TmdbId = tmdbId, Title = "The Film", Year = 1999, Genres = genres ?? "" };

        [Fact]
        public void Only_films_already_in_the_library_reach_the_shelf()
        {
            var library = new[] { Film(2, "Owned", tmdbId: 201), Film(3, "Also Owned", tmdbId: 202) };

            // 999 is a film TMDB likes and this library does not have.
            var shelf = RelatedFilms.For(
                new[] { Recommendation(999), Recommendation(201), Recommendation(202) },
                library,
                Open());

            Assert.Equal(new[] { "Owned", "Also Owned" }, shelf.Films.Select(f => f.Title));
            Assert.Equal(RelatedBasis.Recommended, shelf.Basis);
        }

        /// <summary>
        /// TMDB's order is a relevance ranking. Re-sorting it by year or title would throw away
        /// the only thing the recommendation was actually for.
        /// </summary>
        [Fact]
        public void The_recommended_order_is_kept()
        {
            var library = new[]
            {
                Film(2, "Third", tmdbId: 203, year: 2020),
                Film(3, "First", tmdbId: 201, year: 1980),
                Film(4, "Second", tmdbId: 202, year: 1995)
            };

            var shelf = RelatedFilms.For(
                new[] { Recommendation(201), Recommendation(202), Recommendation(203) },
                library,
                Open());

            Assert.Equal(new[] { "First", "Second", "Third" }, shelf.Films.Select(f => f.Title));
        }

        [Fact]
        public void A_film_never_appears_on_its_own_shelf()
        {
            // Films recommend themselves often enough that this is the first thing to get wrong.
            var library = new[] { Film(1, "The Film", tmdbId: 100), Film(2, "Another", tmdbId: 201) };

            var shelf = RelatedFilms.For(
                new[] { Recommendation(100), Recommendation(201) },
                library,
                Open(localId: 1, tmdbId: 100));

            Assert.Equal(new[] { "Another" }, shelf.Films.Select(f => f.Title));
        }

        [Fact]
        public void A_server_film_is_kept_off_its_own_shelf_by_its_server_id()
        {
            var mine = Film(0, "The Film", tmdbId: 100);
            mine.RemoteId = "abc";

            var vm = new MovieDetailsVm { RemoteId = "abc", TmdbId = 100, Title = "The Film" };

            var shelf = RelatedFilms.For(new[] { Recommendation(100) }, new[] { mine }, vm);

            Assert.False(shelf.Any);
        }

        /// <summary>
        /// The two TMDB catalogues are numbered separately, so film 1399 and series 1399 are
        /// different works. Letting a series onto a film's shelf would put a programme there for
        /// no reason anybody could see.
        /// </summary>
        [Fact]
        public void A_series_is_never_offered_as_a_film_to_watch_next()
        {
            var show = Film(2, "A Programme", tmdbId: 201);
            show.Kind = MediaKind.Series;

            var shelf = RelatedFilms.For(new[] { Recommendation(201) }, new[] { show }, Open());

            Assert.False(shelf.Any);
        }

        [Fact]
        public void The_shelf_is_capped_so_it_cannot_run_off_the_window()
        {
            var library = Enumerable.Range(0, 20).Select(i => Film(i + 2, $"Film {i}", tmdbId: 200 + i)).ToList();
            var recommended = Enumerable.Range(0, 20).Select(i => Recommendation(200 + i)).ToList();

            Assert.Equal(RelatedFilms.Max, RelatedFilms.For(recommended, library, Open()).Films.Count);
        }

        [Fact]
        public void A_recommendation_listed_twice_earns_one_poster()
        {
            var library = new[] { Film(2, "Owned", tmdbId: 201) };

            var shelf = RelatedFilms.For(
                new[] { Recommendation(201), Recommendation(201) },
                library,
                Open());

            Assert.Single(shelf.Films);
        }

        // ---------- the fallback ----------

        /// <summary>
        /// No key, no identification and nothing recommended are all ordinary rather than
        /// exceptional, and each would leave the shelf empty. Genres answer the question badly but
        /// answer it.
        /// </summary>
        [Fact]
        public void With_nothing_recommended_the_shelf_falls_back_to_shared_genres()
        {
            var library = new[]
            {
                Film(2, "Same Two", genres: "Science Fiction, Horror"),
                Film(3, "Same One", genres: "Science Fiction, Comedy"),
                Film(4, "Nothing In Common", genres: "Romance")
            };

            var shelf = RelatedFilms.For(
                Array.Empty<TmdbMatch.Candidate>(),
                library,
                Open(genres: "Science Fiction, Horror"));

            Assert.Equal(RelatedBasis.Genre, shelf.Basis);
            Assert.Equal(new[] { "Same Two", "Same One" }, shelf.Films.Select(f => f.Title));
        }

        /// <summary>
        /// "Also Drama" is true of half a library and says nothing; "also Science Fiction and
        /// Horror" is a real resemblance. Ordering by how much overlaps is what makes the weak
        /// answer worth showing at all.
        /// </summary>
        [Fact]
        public void The_genre_shelf_puts_the_strongest_resemblance_first()
        {
            var library = new[]
            {
                Film(2, "One Genre", genres: "Drama"),
                Film(3, "Three Genres", genres: "Drama, Crime, Thriller")
            };

            var shelf = RelatedFilms.For(null, library, Open(genres: "Drama, Crime, Thriller"));

            Assert.Equal("Three Genres", shelf.Films[0].Title);
        }

        [Fact]
        public void Genres_are_matched_however_the_two_sources_capitalise_them()
        {
            var library = new[] { Film(2, "Owned", genres: "science fiction") };

            Assert.True(RelatedFilms.For(null, library, Open(genres: "Science Fiction")).Any);
        }

        /// <summary>
        /// A scanned library has no genres at all, so this is the commonest outcome on an install
        /// with no server — and the screen has to hide the shelf rather than head an empty row.
        /// </summary>
        [Fact]
        public void A_film_with_no_genres_and_no_recommendations_gets_no_shelf()
        {
            var library = new[] { Film(2, "Owned"), Film(3, "Also Owned") };

            var shelf = RelatedFilms.For(null, library, Open(genres: null));

            Assert.False(shelf.Any);
            Assert.Equal(RelatedBasis.None, shelf.Basis);
            Assert.Equal("", shelf.Heading);
        }

        [Fact]
        public void A_recommendation_that_matched_is_never_replaced_by_the_genre_shelf()
        {
            var library = new[]
            {
                Film(2, "Recommended", tmdbId: 201, genres: "Drama"),
                Film(3, "Merely The Same Genre", genres: "Drama")
            };

            var shelf = RelatedFilms.For(new[] { Recommendation(201) }, library, Open(genres: "Drama"));

            Assert.Equal(RelatedBasis.Recommended, shelf.Basis);
            Assert.Equal(new[] { "Recommended" }, shelf.Films.Select(f => f.Title));
        }

        /// <summary>
        /// The heading has to say which question was answered. A genre shelf headed "watch next"
        /// would be claiming a resemblance nobody computed.
        /// </summary>
        [Fact]
        public void The_heading_says_which_of_the_two_answers_this_is()
        {
            var recommended = RelatedFilms.For(
                new[] { Recommendation(201) },
                new[] { Film(2, "Owned", tmdbId: 201) },
                Open());

            var byGenre = RelatedFilms.For(
                null,
                new[] { Film(2, "Owned", genres: "Drama") },
                Open(genres: "Drama"));

            Assert.NotEqual(recommended.Heading, byGenre.Heading);
            Assert.NotEqual("", recommended.Heading);
            Assert.NotEqual("", byGenre.Heading);
        }

        [Fact]
        public void An_empty_library_is_not_a_failure()
        {
            Assert.False(RelatedFilms.For(new[] { Recommendation(201) }, Array.Empty<UiMovie>(), Open()).Any);
            Assert.False(RelatedFilms.For(null, null, Open()).Any);
            Assert.False(RelatedFilms.For(null, new[] { Film(2, "Owned") }, null).Any);
        }

        [Fact]
        public void A_film_the_catalogue_has_not_identified_is_not_matched_by_a_null_id()
        {
            // Two unidentified films must not be treated as the same film, or as any film.
            var library = new[] { Film(2, "Unidentified", tmdbId: null) };

            Assert.False(RelatedFilms.For(new[] { Recommendation(201) }, library, Open()).Any);
        }

        [Fact]
        public void Splitting_genres_tolerates_the_shapes_both_sources_produce()
        {
            Assert.Empty(RelatedFilms.Split(null));
            Assert.Empty(RelatedFilms.Split("  "));
            Assert.Equal(2, RelatedFilms.Split("Drama, Crime").Count);
            Assert.Equal(2, RelatedFilms.Split("Drama,,Crime,").Count);
        }
    }
}
