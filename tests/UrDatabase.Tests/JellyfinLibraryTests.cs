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
            Assert.True(card.IsOnServer);
            Assert.False(card.IsOnThisComputer);
            Assert.False(card.IsInBothPlaces);
            Assert.Equal(MovieSource.Jellyfin, card.Source);
            Assert.Equal("a", card.RemoteId);
            Assert.Equal("Server", card.ServerBadge);
            Assert.Equal("A Wholly Invented Film", card.Title);
            Assert.Equal("Drama", card.Genres);
        }

        [Fact]
        public void A_local_card_is_local_without_anyone_saying_so()
        {
            var card = Local(1, "A Local Film");

            Assert.False(card.IsRemote);
            Assert.True(card.IsOnThisComputer);
            Assert.False(card.IsOnServer);

            // No badge at all on a film only this machine has: the absence of the server's badge
            // is the whole message, and one on every card would be a wall of them.
            Assert.False(card.IsInBothPlaces);
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
        public void A_film_held_both_locally_and_on_the_server_is_one_card_carrying_both_facts()
        {
            var merged = JellyfinLibrary.Merge(
                new[] { Local(1, "The Same Film", 1999) },
                new[] { JellyfinLibrary.ToUiMovie(Film("a", "The Same Film", 1999)) });

            var only = Assert.Single(merged);

            Assert.True(only.IsInBothPlaces);
            Assert.True(only.IsOnThisComputer);
            Assert.True(only.IsOnServer);

            // Not remote: it opens from this disk, and the server copy is a second badge on the
            // card rather than a change of how it plays.
            Assert.False(only.IsRemote);
            Assert.Equal("a", only.RemoteId);
            Assert.Equal("local:1", only.Key);
        }

        [Fact]
        public void The_same_film_spelled_differently_in_the_two_libraries_is_still_one_card()
        {
            // The rules a re-scan already uses: case, accents and punctuation are not differences.
            var merged = JellyfinLibrary.Merge(
                new[] { Local(1, "amelie", 2001) },
                new[] { JellyfinLibrary.ToUiMovie(Film("a", "Amélie", 2001)) });

            Assert.Single(merged);
            Assert.True(merged[0].IsInBothPlaces);
        }

        [Fact]
        public void A_local_film_whose_filename_carried_no_year_still_meets_its_server_copy()
        {
            // "The Matrix.mkv" scans to a film with no year at all, and the server knows 1999.
            var merged = JellyfinLibrary.Merge(
                new[] { Local(1, "A Wholly Invented Film", null) },
                new[] { JellyfinLibrary.ToUiMovie(Film("a", "A Wholly Invented Film", 1999)) });

            Assert.Single(merged);
            Assert.True(merged[0].IsInBothPlaces);
        }

        [Fact]
        public void Two_films_that_merely_share_a_title_are_left_alone()
        {
            var merged = JellyfinLibrary.Merge(
                new[] { Local(1, "The Thing", 1982) },
                new[] { JellyfinLibrary.ToUiMovie(Film("a", "The Thing", 2011)) });

            Assert.Equal(2, merged.Count);
            Assert.DoesNotContain(merged, m => m.IsInBothPlaces);
        }

        [Fact]
        public void A_second_server_film_with_the_same_title_stays_a_card_of_its_own()
        {
            // A film and its remaster, or an entry that never got a year. Overwriting the first
            // fold with the second would drop a film out of the library altogether.
            var merged = JellyfinLibrary.Merge(
                new[] { Local(1, "A Wholly Invented Film", 1999) },
                new[]
                {
                    JellyfinLibrary.ToUiMovie(Film("a", "A Wholly Invented Film", 1999)),
                    JellyfinLibrary.ToUiMovie(Film("b", "A Wholly Invented Film", 1999))
                });

            Assert.Equal(2, merged.Count);
            Assert.Single(merged, m => m.IsInBothPlaces);
            Assert.Single(merged, m => m.IsRemote);
            Assert.Equal("a", merged.Single(m => m.IsInBothPlaces).RemoteId);
        }

        [Fact]
        public void A_folded_film_takes_the_genres_it_has_none_of_its_own()
        {
            // A scanned film has no genres at all, and its server twin was the only reason it
            // appeared on a shelf. Folding without this would drop it into Uncategorised.
            var merged = JellyfinLibrary.Merge(
                new[] { Local(1, "A Wholly Invented Film", 1999, genres: "") },
                new[] { JellyfinLibrary.ToUiMovie(Film("a", "A Wholly Invented Film", 1999, genres: "Drama, Crime")) });

            Assert.Equal("Drama, Crime", Assert.Single(merged).Genres);
        }

        [Fact]
        public void A_folded_film_keeps_the_genres_it_already_had()
        {
            var merged = JellyfinLibrary.Merge(
                new[] { Local(1, "A Wholly Invented Film", 1999, genres: "Comedy") },
                new[] { JellyfinLibrary.ToUiMovie(Film("a", "A Wholly Invented Film", 1999, genres: "Drama")) });

            Assert.Equal("Comedy", Assert.Single(merged).Genres);
        }

        [Fact]
        public void A_folded_film_borrows_the_servers_poster_only_until_it_has_one()
        {
            var local = Local(1, "A Wholly Invented Film", 1999);
            var server = JellyfinLibrary.ToUiMovie(Film("a", "A Wholly Invented Film", 1999), m => $"http://media.invalid/{m.ItemId}");

            var card = Assert.Single(JellyfinLibrary.Merge(new[] { local }, new[] { server }));

            Assert.Equal("http://media.invalid/a", card.DisplayPosterPath);

            // The local column stays empty, so the poster loader still fetches artwork this
            // machine owns — which is the copy that survives the server being switched off.
            Assert.Null(card.PosterPath);

            card.PosterPath = "/tmp/cached.jpg";
            Assert.Equal("/tmp/cached.jpg", card.DisplayPosterPath);
        }

        [Fact]
        public void A_folded_film_is_counted_once()
        {
            var merged = JellyfinLibrary.Merge(
                new[] { Local(1, "A Wholly Invented Film", 1999) },
                new[] { JellyfinLibrary.ToUiMovie(Film("a", "A Wholly Invented Film", 1999)) });

            Assert.Equal(1, LibraryGrouping.BuildGenreChips(merged)[0].Count);
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

        // ---------- one film, two sources, two names ----------

        /// <summary>
        /// The reported case. A file named <c>El Drama (The Drama) (2026).mkv</c> is catalogued as
        /// "El Drama" and the same film is "The Drama" on the server, so the wall showed it twice
        /// and nothing could be told they were one film. Normalisation folds case, accents and
        /// punctuation — it does not translate a title.
        /// </summary>
        [Fact]
        public void One_film_the_two_sources_call_different_things_is_one_card()
        {
            var local = Local(1, "El Drama", 2026);
            local.TmdbId = 901;

            var server = Film("s1", "The Drama", 2026);
            server.TmdbId = "901";

            var merged = JellyfinLibrary.Merge(new[] { local }, JellyfinLibrary.ToUiMovies(new[] { server }));

            var card = Assert.Single(merged);
            Assert.Equal("El Drama", card.Title);
            Assert.True(card.IsOnThisComputer);
            Assert.True(card.IsOnServer);
            Assert.Equal("s1", card.RemoteId);
        }

        /// <summary>
        /// The same two films with nothing identifying either. Matching on the title is all that is
        /// left, and it genuinely cannot tell these apart — so two cards is the honest answer, not
        /// a regression. This is what the picker on the details screen is for.
        /// </summary>
        [Fact]
        public void Without_an_id_on_either_side_the_two_names_stay_two_cards()
        {
            var merged = JellyfinLibrary.Merge(
                new[] { Local(1, "El Drama", 2026) },
                JellyfinLibrary.ToUiMovies(new[] { Film("s1", "The Drama", 2026) }));

            Assert.Equal(2, merged.Count);
        }

        [Fact]
        public void An_id_folds_a_film_the_year_alone_would_have_refused()
        {
            // Release dates differ by region, and the title index treats a different year as a
            // different film. The id says otherwise.
            var local = Local(1, "El Drama", 2026);
            local.TmdbId = 901;

            var server = Film("s1", "El Drama", 2025);
            server.TmdbId = "901";

            var merged = JellyfinLibrary.Merge(new[] { local }, JellyfinLibrary.ToUiMovies(new[] { server }));

            var card = Assert.Single(merged);
            Assert.True(card.IsOnServer);
        }

        [Fact]
        public void Two_different_films_that_share_an_identity_with_neither_are_not_folded()
        {
            var local = Local(1, "One Film", 2001);
            local.TmdbId = 11;

            var server = Film("s1", "Another Film", 2001);
            server.TmdbId = "22";

            var merged = JellyfinLibrary.Merge(new[] { local }, JellyfinLibrary.ToUiMovies(new[] { server }));

            Assert.Equal(2, merged.Count);
        }

        /// <summary>
        /// Jellyfin reports a provider id as text and reports none at all for a film it could not
        /// identify. Read carelessly that becomes zero, and every unidentified film on the server
        /// then shares one id and folds onto whichever local film happened to be read first.
        /// </summary>
        [Fact]
        public void A_server_film_with_no_usable_id_carries_none()
        {
            Assert.Null(JellyfinLibrary.ParseTmdbId(null));
            Assert.Null(JellyfinLibrary.ParseTmdbId(""));
            Assert.Null(JellyfinLibrary.ParseTmdbId("   "));
            Assert.Null(JellyfinLibrary.ParseTmdbId("not-a-number"));
            Assert.Null(JellyfinLibrary.ParseTmdbId("0"));
            Assert.Null(JellyfinLibrary.ParseTmdbId("-5"));
            Assert.Equal(901, JellyfinLibrary.ParseTmdbId(" 901 "));
        }

        [Fact]
        public void Unidentified_films_on_both_sides_do_not_collapse_onto_one_card()
        {
            var merged = JellyfinLibrary.Merge(
                new[] { Local(1, "One Film", 2001), Local(2, "Another Film", 2002) },
                JellyfinLibrary.ToUiMovies(new[]
                {
                    Film("s1", "A Third Film", 2003),
                    Film("s2", "A Fourth Film", 2004)
                }));

            Assert.Equal(4, merged.Count);
        }

        /// <summary>
        /// A film only the server has identified. Folding it on the title is all that was possible,
        /// and taking its id afterwards is what lets the next merge hold on identity instead.
        /// </summary>
        [Fact]
        public void A_fold_made_on_the_title_adopts_the_id_the_server_knew()
        {
            var local = Local(1, "A Wholly Invented Film", 1994);

            var server = Film("s1", "A Wholly Invented Film", 1994);
            server.TmdbId = "77";

            var merged = JellyfinLibrary.Merge(new[] { local }, JellyfinLibrary.ToUiMovies(new[] { server }));

            Assert.Equal(77, Assert.Single(merged).TmdbId);
        }

        [Fact]
        public void A_corrected_local_answer_is_never_overwritten_by_the_servers()
        {
            var local = Local(1, "A Wholly Invented Film", 1994);
            local.TmdbId = 901;

            var server = Film("s1", "A Wholly Invented Film", 1994);
            server.TmdbId = "77";

            var merged = JellyfinLibrary.Merge(new[] { local }, JellyfinLibrary.ToUiMovies(new[] { server }));

            Assert.Equal(901, Assert.Single(merged).TmdbId);
        }
    }
}
