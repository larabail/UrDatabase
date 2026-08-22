using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Where the two libraries meet. Every rule asserted here is one the window would otherwise
    /// have hidden inside an event handler.
    /// </summary>
    public class JellyfinLibraryTests
    {
        private static JellyfinMovie Film(string id, string title, int? year = 1994, string genres = "Drama") => new()
        {
            ItemId = id,
            Title = title,
            Year = year,
            Genres = genres,
            ImageTag = "tag-" + id
        };

        private static UiMovie Local(long id, string title, int? year = 1994, string genres = "Drama") => new()
        {
            Id = id,
            Title = title,
            Year = year,
            Genres = genres
        };

        [Fact]
        public void A_server_film_becomes_a_card_that_says_it_is_remote()
        {
            var card = JellyfinLibrary.ToUiMovie(Film("a", "A Wholly Invented Film"));

            Assert.True(card.IsRemote);
            Assert.Equal(MovieSource.Jellyfin, card.Source);
            Assert.Equal("a", card.RemoteId);
            Assert.Equal("Server", card.SourceLabel);
            Assert.Equal("A Wholly Invented Film", card.Title);
            Assert.Equal("Drama", card.Genres);
        }

        [Fact]
        public void A_local_card_is_local_without_anyone_saying_so()
        {
            var card = Local(1, "A Local Film");

            Assert.False(card.IsRemote);
            Assert.Equal("Local", card.SourceLabel);
        }

        [Fact]
        public void The_poster_url_is_supplied_by_the_caller_because_it_needs_the_server_address()
        {
            var card = JellyfinLibrary.ToUiMovie(Film("a", "A Wholly Invented Film"), m => $"http://media.invalid/{m.ItemId}");

            Assert.Equal("http://media.invalid/a", card.PosterPath);
        }

        [Fact]
        public void Every_server_film_carries_id_zero_so_identity_has_to_come_from_somewhere_else()
        {
            var first = JellyfinLibrary.ToUiMovie(Film("a", "First"));
            var second = JellyfinLibrary.ToUiMovie(Film("b", "Second"));

            Assert.Equal(0, first.Id);
            Assert.Equal(0, second.Id);
            Assert.NotEqual(first.Key, second.Key);
        }

        [Fact]
        public void A_local_and_a_remote_film_never_share_a_key()
        {
            var local = Local(0, "A Film");
            var remote = JellyfinLibrary.ToUiMovie(Film("0", "A Film"));

            Assert.NotEqual(local.Key, remote.Key);
        }

        [Fact]
        public void Films_with_no_id_are_not_turned_into_cards()
        {
            var cards = JellyfinLibrary.ToUiMovies(new[] { Film("", "Nameless"), Film("a", "A Wholly Invented Film") });

            Assert.Single(cards);
        }

        [Fact]
        public void Merging_puts_both_libraries_in_one_ordering()
        {
            var merged = JellyfinLibrary.Merge(
                new[] { Local(1, "An Older Local Film", 1980) },
                new[] { JellyfinLibrary.ToUiMovie(Film("a", "A Newer Server Film", 2020)) });

            Assert.Equal(new[] { "A Newer Server Film", "An Older Local Film" }, merged.Select(m => m.Title).ToArray());
        }

        [Fact]
        public void A_film_held_both_locally_and_on_the_server_is_shown_twice()
        {
            // Deliberate. One of them plays with the house network down and the other does not,
            // so collapsing them would hide the only copy that always works.
            var merged = JellyfinLibrary.Merge(
                new[] { Local(1, "The Same Film", 1999) },
                new[] { JellyfinLibrary.ToUiMovie(Film("a", "The Same Film", 1999)) });

            Assert.Equal(2, merged.Count);
            Assert.Single(merged, m => m.IsRemote);
            Assert.Single(merged, m => !m.IsRemote);
        }

        [Fact]
        public void Merging_the_same_film_twice_keeps_one_copy()
        {
            var remote = JellyfinLibrary.ToUiMovie(Film("a", "A Wholly Invented Film"));

            var merged = JellyfinLibrary.Merge(Array.Empty<UiMovie>(), new[] { remote, remote });

            Assert.Single(merged);
        }

        [Fact]
        public void Merging_with_no_server_returns_the_local_library_untouched()
        {
            var merged = JellyfinLibrary.Merge(new[] { Local(1, "A Local Film") }, Array.Empty<UiMovie>());

            var only = Assert.Single(merged);
            Assert.False(only.IsRemote);
        }

        [Fact]
        public void Merging_survives_a_null_on_either_side()
        {
            Assert.Empty(JellyfinLibrary.Merge(null, null));
            Assert.Single(JellyfinLibrary.Merge(null, new[] { JellyfinLibrary.ToUiMovie(Film("a", "A Film")) }));
            Assert.Single(JellyfinLibrary.Merge(new[] { Local(1, "A Film") }, null));
        }

        [Fact]
        public void Searching_the_server_library_matches_a_title_anywhere_in_it()
        {
            var films = JellyfinLibrary.ToUiMovies(new[]
            {
                Film("a", "A Wholly Invented Film"),
                Film("b", "Another Made Up Picture")
            });

            var hits = JellyfinLibrary.Search(films, "invented");

            Assert.Single(hits);
            Assert.Equal("A Wholly Invented Film", hits[0].Title);
        }

        [Fact]
        public void Searching_the_server_library_matches_a_genre()
        {
            var films = JellyfinLibrary.ToUiMovies(new[]
            {
                Film("a", "A Wholly Invented Film", genres: "Drama, Crime"),
                Film("b", "Another Made Up Picture", genres: "Comedy")
            });

            Assert.Single(JellyfinLibrary.Search(films, "crime"));
        }

        [Fact]
        public void An_empty_search_returns_everything()
        {
            var films = JellyfinLibrary.ToUiMovies(new[] { Film("a", "A Wholly Invented Film") });

            Assert.Single(JellyfinLibrary.Search(films, ""));
            Assert.Single(JellyfinLibrary.Search(films, "   "));
            Assert.Single(JellyfinLibrary.Search(films, null));
        }

        [Fact]
        public void A_search_that_matches_nothing_returns_nothing_rather_than_everything()
        {
            var films = JellyfinLibrary.ToUiMovies(new[] { Film("a", "A Wholly Invented Film") });

            Assert.Empty(JellyfinLibrary.Search(films, "documentary"));
        }

        [Fact]
        public void Server_films_group_by_genre_like_any_other()
        {
            // Jellyfin supplies real genres, so a server library sidesteps the empty
            // "Uncategorised" bucket that a freshly scanned one falls into.
            var films = JellyfinLibrary.ToUiMovies(new[]
            {
                Film("a", "A Wholly Invented Film", genres: "Drama, Crime"),
                Film("b", "Another Made Up Picture", genres: "Comedy")
            });

            var genres = LibraryGrouping.BuildGenreList(films);

            Assert.Equal(new[] { "All", "Comedy", "Crime", "Drama" }, genres.ToArray());
            Assert.DoesNotContain(LibraryGrouping.Uncategorised, genres);
            Assert.Single(LibraryGrouping.ItemsForGenre(films, "Crime"));
        }

        [Fact]
        public void A_server_film_with_no_genres_still_lands_somewhere()
        {
            var films = JellyfinLibrary.ToUiMovies(new[] { Film("a", "A Wholly Invented Film", genres: "") });

            Assert.Contains(LibraryGrouping.Uncategorised, LibraryGrouping.BuildGenreList(films));
        }
    }
}
