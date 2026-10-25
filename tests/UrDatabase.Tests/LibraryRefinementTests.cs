using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The filter row: length, rating, and an actor or director. Pure rules over cards, so nothing
    /// here needs a window, a database or a network.
    /// </summary>
    public class LibraryRefinementTests
    {
        private static int _next;

        private static UiMovie Film(
            string title,
            int? runtime = null,
            double? rating = null,
            string? cast = null,
            string? crew = null,
            MediaKind kind = MediaKind.Film) => new()
        {
            Id = ++_next,
            Title = title,
            RuntimeMinutes = runtime,
            Rating = rating,
            CastList = cast,
            CrewList = crew,
            Kind = kind
        };

        // ---------- length ----------

        [Theory]
        [InlineData(1, RuntimeRange.Short)]
        [InlineData(89, RuntimeRange.Short)]
        [InlineData(90, RuntimeRange.Feature)]
        [InlineData(120, RuntimeRange.Feature)]
        [InlineData(121, RuntimeRange.Long)]
        [InlineData(238, RuntimeRange.Long)]
        public void A_running_time_falls_in_exactly_one_band(int minutes, RuntimeRange expected)
        {
            Assert.Equal(expected, LibraryRefinement.BandOf(minutes));

            foreach (var band in LibraryRefinement.RuntimeOptions.Where(b => b != RuntimeRange.Any))
            {
                var filter = new LibraryRefinement { Runtime = band };
                Assert.Equal(band == expected, filter.Matches(Film("Any", runtime: minutes)));
            }
        }

        [Fact]
        public void A_film_of_unknown_length_is_not_claimed_by_any_band()
        {
            var unknown = Film("Unmeasured");

            foreach (var band in LibraryRefinement.RuntimeOptions.Where(b => b != RuntimeRange.Any))
                Assert.False(new LibraryRefinement { Runtime = band }.Matches(unknown));

            Assert.True(LibraryRefinement.None.Matches(unknown));
        }

        [Fact]
        public void A_series_has_no_length_of_its_own_and_drops_out_of_a_length_filter()
        {
            var series = Film("Twin Peaks", rating: 8.8, kind: MediaKind.Series);

            Assert.False(new LibraryRefinement { Runtime = RuntimeRange.Long }.Matches(series));
            Assert.True(new LibraryRefinement { MinimumRating = 8 }.Matches(series));
        }

        /// <summary>
        /// Nothing will ever give a programme a running time, so counting it as "not known yet"
        /// left the note under a length filter waiting for ever on every series in the library.
        /// </summary>
        [Theory]
        [InlineData(MediaKind.Series)]
        [InlineData(MediaKind.Episode)]
        public void Television_is_never_counted_as_waiting_for_a_length(MediaKind kind)
        {
            var television = new[] { Film("Twin Peaks", kind: kind), Film("The Wire", rating: 9.3, kind: kind) };

            foreach (var band in LibraryRefinement.RuntimeOptions.Where(b => b != RuntimeRange.Any))
            {
                var filter = new LibraryRefinement { Runtime = band };

                Assert.Empty(filter.Apply(television));
                Assert.Equal(0, filter.Unknown(television));
            }

            // Still unknown for what a programme can genuinely lack and later gain.
            Assert.Equal(1, new LibraryRefinement { MinimumRating = 7 }.Unknown(television));
        }

        // ---------- rating ----------

        [Theory]
        [InlineData(6.9, false)]
        [InlineData(7.0, true)]
        [InlineData(8.4, true)]
        public void The_rating_floor_includes_the_number_on_it(double rating, bool passes)
        {
            Assert.Equal(passes, new LibraryRefinement { MinimumRating = 7 }.Matches(Film("Any", rating: rating)));
        }

        [Fact]
        public void A_film_nobody_has_rated_does_not_pass_a_rating_floor()
        {
            Assert.False(new LibraryRefinement { MinimumRating = 6 }.Matches(Film("Unrated")));
            Assert.False(new LibraryRefinement { MinimumRating = 6 }.Matches(Film("Zero", rating: 0)));
        }

        // ---------- people ----------

        [Fact]
        public void An_actor_is_found_by_part_of_their_name_whatever_the_case()
        {
            var film = Film("Persona", cast: "Liv Ullmann (Elisabet Vogler)\nBibi Andersson (Alma)");

            Assert.True(new LibraryRefinement { Person = "ullmann" }.Matches(film));
            Assert.True(new LibraryRefinement { Person = "  BIBI  " }.Matches(film));
            Assert.False(new LibraryRefinement { Person = "Max von Sydow" }.Matches(film));
        }

        [Fact]
        public void The_character_an_actor_plays_is_not_the_actor()
        {
            var film = Film("Persona", cast: "Liv Ullmann (Elisabet Vogler)");

            Assert.False(new LibraryRefinement { Person = "Vogler" }.Matches(film));
        }

        [Fact]
        public void Accents_are_ignored_so_a_plain_keyboard_finds_them()
        {
            var film = Film("Volver", crew: "Director: Pedro Almodóvar");

            Assert.True(new LibraryRefinement { Person = "almodovar" }.Matches(film));
        }

        [Fact]
        public void A_director_is_found_and_a_writer_is_not()
        {
            var film = Film("Persona", crew: "Director: Ingmar Bergman\nWriter: Ulla Isaksson");

            Assert.True(new LibraryRefinement { Person = "bergman" }.Matches(film));
            Assert.False(new LibraryRefinement { Person = "isaksson" }.Matches(film));
        }

        [Fact]
        public void Every_word_has_to_be_in_the_same_persons_name()
        {
            var film = Film("Any", cast: "Chris Pine (Kirk)\nBill Nolan (Extra)", crew: "Director: Christopher Nolan");
            var mixed = Film("Other", cast: "Chris Pine (Kirk)\nBill Nolan (Extra)");

            Assert.True(new LibraryRefinement { Person = "chris nolan" }.Matches(film));
            Assert.False(new LibraryRefinement { Person = "chris nolan" }.Matches(mixed));
        }

        // ---------- together ----------

        [Fact]
        public void Every_filter_that_is_set_has_to_pass()
        {
            var films = new[]
            {
                Film("Long, good, Ullmann", 150, 8.1, "Liv Ullmann (Marianne)"),
                Film("Long, good, nobody", 150, 8.1, "Erland Josephson (Johan)"),
                Film("Short, good, Ullmann", 80, 8.1, "Liv Ullmann (Eva)"),
                Film("Long, poor, Ullmann", 150, 5.0, "Liv Ullmann (Anna)")
            };

            var filter = new LibraryRefinement { Runtime = RuntimeRange.Long, MinimumRating = 7, Person = "ullmann" };

            Assert.Equal(new[] { "Long, good, Ullmann" }, filter.Apply(films).Select(m => m.Title));
        }

        [Fact]
        public void Nothing_set_is_nothing_narrowed()
        {
            var films = new[] { Film("A"), Film("B", runtime: 100) };

            Assert.False(LibraryRefinement.None.IsActive);
            Assert.False(new LibraryRefinement { Person = "   " }.IsActive);
            Assert.Equal(2, LibraryRefinement.None.Apply(films).Count);
            Assert.Empty(LibraryRefinement.None.Apply(null));
        }

        // ---------- owning up to what it cannot see ----------

        [Fact]
        public void Films_hidden_only_for_want_of_a_fact_are_counted()
        {
            var films = new[]
            {
                Film("Known and long", runtime: 150),
                Film("Known and short", runtime: 80),
                Film("Not measured yet"),
                Film("Not measured either")
            };

            var filter = new LibraryRefinement { Runtime = RuntimeRange.Long };

            Assert.Single(filter.Apply(films));
            Assert.Equal(2, filter.Unknown(films));
            Assert.Equal(0, LibraryRefinement.None.Unknown(films));
        }

        [Fact]
        public void A_film_that_fails_on_a_known_fact_is_not_blamed_on_a_missing_one()
        {
            // Short is known, and is a reason on its own; the missing rating changes nothing.
            var film = Film("Short, unrated", runtime: 80);
            var filter = new LibraryRefinement { Runtime = RuntimeRange.Long, MinimumRating = 7 };

            Assert.Equal(0, filter.Unknown(new[] { film }));
        }

        [Fact]
        public void A_film_with_no_cast_on_record_is_counted_as_unknown_for_a_person_filter()
        {
            var films = new[] { Film("No credits yet"), Film("Credited", cast: "Someone Else (Role)") };

            Assert.Equal(1, new LibraryRefinement { Person = "ullmann" }.Unknown(films));
        }

        [Fact]
        public void The_note_says_nothing_when_nothing_is_hidden()
        {
            Assert.Equal("", LibraryRefinement.UnknownNote(0));
            Assert.Equal("1 more not shown: its details are not known yet", LibraryRefinement.UnknownNote(1));
            Assert.Equal("12 more not shown: their details are not known yet", LibraryRefinement.UnknownNote(12));
        }

        // ---------- the controls ----------

        [Fact]
        public void The_controls_start_with_their_way_back()
        {
            Assert.Equal(RuntimeRange.Any, LibraryRefinement.RuntimeOptions[0]);
            Assert.Null(LibraryRefinement.RatingOptions[0]);

            Assert.Equal("Any length", LibraryRefinement.Label(RuntimeRange.Any));
            Assert.Equal("Under 90 min", LibraryRefinement.Label(RuntimeRange.Short));
            Assert.Equal("90–120 min", LibraryRefinement.Label(RuntimeRange.Feature));
            Assert.Equal("Over 2 hours", LibraryRefinement.Label(RuntimeRange.Long));

            Assert.Equal("Any rating", LibraryRefinement.RatingLabel(null));
            Assert.Equal("7+", LibraryRefinement.RatingLabel(7));
        }

        [Fact]
        public void A_chip_names_itself_from_the_same_rules()
        {
            Assert.Equal("Over 2 hours", new RuntimeChip { Range = RuntimeRange.Long }.Name);
            Assert.Equal("8+", new RatingChip { Minimum = 8 }.Name);
        }

        // ---------- the page catching up ----------

        /// <summary>
        /// The bug: only a page with shelves on it was ever redrawn, so a page the filters had
        /// emptied straight after a scan stayed empty while the facts it was waiting for arrived.
        /// </summary>
        [Fact]
        public void A_page_emptied_by_the_filters_is_redrawn_as_facts_arrive()
        {
            Assert.True(LibraryRegroup.ShouldRedraw(stale: true, due: true, searching: false, shelvesShown: false, emptiedByFilters: true));
        }

        [Theory]
        [InlineData(false, true, false, true, true)]   // nothing new has arrived
        [InlineData(true, false, false, true, true)]   // too soon after the last redraw
        [InlineData(true, true, true, true, true)]     // search results are up and are left alone
        [InlineData(true, true, false, false, false)]  // a single genre, or the nothing-scanned invitation
        public void Otherwise_the_page_is_left_alone(bool stale, bool due, bool searching, bool shelves, bool emptied)
        {
            Assert.False(LibraryRegroup.ShouldRedraw(stale, due, searching, shelves, emptied));
        }

        [Fact]
        public void Shelves_on_screen_are_still_regrouped_as_before()
        {
            Assert.True(LibraryRegroup.ShouldRedraw(stale: true, due: true, searching: false, shelvesShown: true, emptiedByFilters: false));
        }

        [Fact]
        public void Emptied_by_the_filters_means_films_exist_and_the_filters_hide_all_of_them()
        {
            var library = new[] { Film("Not measured yet"), Film("Short", runtime: 80) };
            var longOnes = new LibraryRefinement { Runtime = RuntimeRange.Long };

            Assert.True(LibraryRegroup.EmptiedByFilters(longOnes, library));
            Assert.False(LibraryRegroup.EmptiedByFilters(LibraryRefinement.None, library));
            Assert.False(LibraryRegroup.EmptiedByFilters(longOnes, Array.Empty<UiMovie>()));

            // The fact the page was waiting for lands, and it is no longer empty.
            library[0].ApplyFacts(new FilmFacts(150, null, new List<string>(), new List<string>()));
            Assert.False(LibraryRegroup.EmptiedByFilters(longOnes, library));
        }

        // ---------- the people a card answers for ----------

        [Fact]
        public void A_cards_people_are_its_actors_and_its_directors_by_name()
        {
            var film = Film("Any",
                cast: "Keir Dullea (Dave Bowman)\nGary Lockwood",
                crew: "Director: Stanley Kubrick\nWriter: Arthur C. Clarke");

            Assert.Equal(new[] { "Keir Dullea", "Gary Lockwood", "Stanley Kubrick" }, film.People);
        }

        [Fact]
        public void Facts_from_TMDB_fill_a_card_without_discarding_a_better_rating()
        {
            var card = Film("Any", rating: 8.3);

            card.ApplyFacts(new FilmFacts(142, 7.9, new List<string> { "Tim Robbins (Andy)" }, new List<string> { "Director: Frank Darabont" }));

            Assert.True(card.FactsKnown);
            Assert.Equal(142, card.RuntimeMinutes);
            Assert.Equal(8.3, card.Rating);
            Assert.Equal(new[] { "Tim Robbins", "Frank Darabont" }, card.People);
        }

        [Fact]
        public void A_server_copy_fills_in_what_a_scanned_film_does_not_know()
        {
            var local = Film("Stalker");
            var server = JellyfinLibrary.ToUiMovie(new JellyfinMovie
            {
                ItemId = "abc",
                Title = "Stalker",
                RuntimeMinutes = 161,
                CommunityRating = 8.1,
                Cast = new List<string> { "Alisa Freyndlikh (Wife)" },
                Crew = new List<string> { "Director: Andrei Tarkovsky" }
            });

            Assert.Equal(161, server.RuntimeMinutes);
            Assert.Equal(8.1, server.Rating);

            local.AdoptServerCopy(server);

            Assert.Equal(161, local.RuntimeMinutes);
            Assert.Equal(8.1, local.Rating);
            Assert.True(new LibraryRefinement { Person = "tarkovsky", Runtime = RuntimeRange.Long }.Matches(local));
        }

        [Fact]
        public void A_server_copy_does_not_overwrite_what_the_catalogue_already_knows()
        {
            var local = Film("Stalker", runtime: 163, rating: 8.0, cast: "Anatoly Solonitsyn (Writer)");
            var server = JellyfinLibrary.ToUiMovie(new JellyfinMovie
            {
                ItemId = "abc",
                Title = "Stalker",
                RuntimeMinutes = 161,
                CommunityRating = 8.1,
                Cast = new List<string> { "Alisa Freyndlikh (Wife)" }
            });

            local.AdoptServerCopy(server);

            Assert.Equal(163, local.RuntimeMinutes);
            Assert.Equal(8.0, local.Rating);
            Assert.Equal(new[] { "Anatoly Solonitsyn" }, local.People);
        }
    }
}
