using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The kind row: films, television, or both.
    ///
    /// The rules that matter here are the ones that decide whether the row appears at all. A
    /// library of films only — which is every library this app had before television — must grow
    /// no control, because a permanent "Television 0" beside "Films 412" is a control whose only
    /// possible use is to empty the window.
    /// </summary>
    public class LibraryKindFilterTests
    {
        private static UiMovie Film(long id, string title) => new()
        {
            Id = id,
            Title = title,
            Kind = MediaKind.Film
        };

        private static UiMovie Series(string id, string title, int? seasons = 3) => new()
        {
            Id = 0,
            RemoteId = id,
            Source = MovieSource.Jellyfin,
            Kind = MediaKind.Series,
            Title = title,
            SeasonCount = seasons
        };

        // ---------- whether the row appears ----------

        [Fact]
        public void A_library_of_films_only_offers_no_kind_row()
        {
            var films = new[] { Film(1, "A Wholly Invented Film"), Film(2, "Another Made Up Picture") };

            Assert.Empty(LibraryFilter.AvailableKinds(films));
        }

        [Fact]
        public void A_library_of_television_only_offers_no_kind_row_either()
        {
            // Symmetric on purpose. A server that holds nothing but programmes has nothing to
            // choose between, and the row would only ever be able to hide half its own library.
            var shows = new[] { Series("a", "A Wholly Invented Programme"), Series("b", "Another Made Up Show") };

            Assert.Empty(LibraryFilter.AvailableKinds(shows));
        }

        [Fact]
        public void A_library_holding_both_offers_all_three_controls()
        {
            var mixed = new[] { Film(1, "A Wholly Invented Film"), Series("a", "A Wholly Invented Programme") };

            Assert.Equal(
                new[] { LibraryKind.Everything, LibraryKind.Films, LibraryKind.Television },
                LibraryFilter.AvailableKinds(mixed).ToArray());
        }

        [Fact]
        public void An_empty_library_offers_nothing()
        {
            Assert.Empty(LibraryFilter.AvailableKinds(Array.Empty<UiMovie>()));
            Assert.Empty(LibraryFilter.AvailableKinds(null));
        }

        // ---------- what each control selects ----------

        [Fact]
        public void Each_control_selects_its_own_half()
        {
            var mixed = new[]
            {
                Film(1, "A Wholly Invented Film"),
                Film(2, "Another Made Up Picture"),
                Series("a", "A Wholly Invented Programme")
            };

            Assert.Equal(3, LibraryFilter.Apply(mixed, LibraryKind.Everything).Count);

            var films = LibraryFilter.Apply(mixed, LibraryKind.Films);
            Assert.Equal(2, films.Count);
            Assert.All(films, m => Assert.True(m.IsFilm));

            var television = LibraryFilter.Apply(mixed, LibraryKind.Television);
            Assert.Equal("A Wholly Invented Programme", Assert.Single(television).Title);
        }

        [Fact]
        public void Nothing_answers_to_both_controls()
        {
            // Unlike the source row, where a film in two places is genuinely in both lists and the
            // counts deliberately do not add up. A card is exactly one kind, so these do.
            var mixed = new[]
            {
                Film(1, "A Wholly Invented Film"),
                Series("a", "A Wholly Invented Programme"),
                Series("b", "Another Made Up Show")
            };

            var everything = LibraryFilter.Count(mixed, LibraryKind.Everything);
            var films = LibraryFilter.Count(mixed, LibraryKind.Films);
            var television = LibraryFilter.Count(mixed, LibraryKind.Television);

            Assert.Equal(3, everything);
            Assert.Equal(everything, films + television);
        }

        [Fact]
        public void Counting_is_done_on_identity_not_on_the_local_id()
        {
            // Every remote card carries local id 0, so counting on the id alone would report the
            // whole television library as a single programme.
            var shows = new[]
            {
                Film(1, "A Wholly Invented Film"),
                Series("a", "A Wholly Invented Programme"),
                Series("b", "Another Made Up Show"),
                Series("c", "The Third Fiction")
            };

            Assert.Equal(3, LibraryFilter.Count(shows, LibraryKind.Television));
        }

        [Fact]
        public void A_null_library_filters_to_nothing_rather_than_throwing()
        {
            Assert.Empty(LibraryFilter.Apply(null, LibraryKind.Television));
            Assert.Equal(0, LibraryFilter.Count(null, LibraryKind.Films));
        }

        [Fact]
        public void The_controls_are_named_for_what_they_select()
        {
            Assert.Equal("Everything", LibraryFilter.Label(LibraryKind.Everything));
            Assert.Equal("Films", LibraryFilter.Label(LibraryKind.Films));
            Assert.Equal("Television", LibraryFilter.Label(LibraryKind.Television));
        }

        // ---------- crossing it with the source row ----------

        [Fact]
        public void Kind_and_source_narrow_independently()
        {
            // Three different questions — where a thing is, what it is, and what genre it is — and
            // answering one must not silently discard another.
            var local = Film(1, "A Wholly Invented Film");
            var onServer = new UiMovie
            {
                Id = 0,
                RemoteId = "film-remote",
                Source = MovieSource.Jellyfin,
                Title = "Another Made Up Picture"
            };
            var show = Series("a", "A Wholly Invented Programme");

            var library = new[] { local, onServer, show };

            var serverTelevision = LibraryFilter.Apply(
                LibraryFilter.Apply(library, LibrarySource.Server),
                LibraryKind.Television);

            Assert.Equal("A Wholly Invented Programme", Assert.Single(serverTelevision).Title);

            var offlineTelevision = LibraryFilter.Apply(
                LibraryFilter.Apply(library, LibrarySource.ThisComputer),
                LibraryKind.Television);

            // Nothing on this machine is a programme: a scan catalogues films.
            Assert.Empty(offlineTelevision);
        }

        // ---------- the card ----------

        [Fact]
        public void A_series_card_says_it_is_one()
        {
            var show = Series("a", "A Wholly Invented Programme");
            show.Year = 2011;

            Assert.True(show.IsSeries);
            Assert.Equal("Series", show.SeriesBadge);

            // The badge and the season count together are what make it acceptable for a programme
            // to share a shelf with films. A card reading nothing but "2011" would be a film with
            // an odd year.
            Assert.Equal("2011 · 3 seasons", show.MetaLine);
        }

        [Fact]
        public void A_film_card_says_exactly_what_it_always_said()
        {
            var film = Film(1, "A Wholly Invented Film");
            film.Year = 1994;

            Assert.Equal("1994", film.MetaLine);
            Assert.False(film.IsSeries);
        }

        [Theory]
        [InlineData(2011, 1, "2011 · 1 season")]
        [InlineData(2011, null, "2011")]
        [InlineData(null, 3, "3 seasons")]
        [InlineData(null, null, "")]
        public void A_card_prints_only_what_is_actually_known(int? year, int? seasons, string expected)
        {
            var show = Series("a", "A Wholly Invented Programme", seasons);
            show.Year = year;

            Assert.Equal(expected, show.MetaLine);
        }

        [Fact]
        public void A_series_does_not_also_carry_the_server_badge()
        {
            // It genuinely is on the server, but so is every other programme, so the mark would
            // appear on all of them and say nothing the series badge did not.
            var show = Series("a", "A Wholly Invented Programme");

            Assert.True(show.IsOnServer);
            Assert.False(show.ShowServerBadge);
        }

        [Fact]
        public void A_server_film_still_carries_the_server_badge()
        {
            var film = new UiMovie
            {
                Id = 0,
                RemoteId = "film-remote",
                Source = MovieSource.Jellyfin,
                Title = "A Wholly Invented Film"
            };

            Assert.True(film.ShowServerBadge);
        }

        [Fact]
        public void A_programme_and_a_film_are_never_the_same_card()
        {
            // Jellyfin does not reuse an id between the two today. This is here so that the day
            // something does, a programme and a film cannot silently become one entry.
            var film = new UiMovie { RemoteId = "same-id", Source = MovieSource.Jellyfin };
            var show = new UiMovie { RemoteId = "same-id", Source = MovieSource.Jellyfin, Kind = MediaKind.Series };

            Assert.NotEqual(film.Key, show.Key);
        }

        // ---------- what a shelf says it is holding ----------

        [Fact]
        public void A_shelf_of_films_is_counted_as_it_always_was()
        {
            var films = new List<UiMovie> { Film(1, "One"), Film(2, "Two") };

            Assert.Equal("2 FILMS", LibraryGrouping.CountLabel(films));
            Assert.Equal("1 FILM", LibraryGrouping.CountLabel(new List<UiMovie> { Film(1, "One") }));
        }

        [Fact]
        public void A_shelf_holding_both_says_so()
        {
            // Eight films and four programmes headed "12 FILMS" is exactly the way mixing the two
            // on one shelf becomes dishonest.
            var mixed = new List<UiMovie> { Film(1, "One"), Film(2, "Two"), Series("a", "A Show") };

            Assert.Equal("2 FILMS · 1 SERIES", LibraryGrouping.CountLabel(mixed));
        }

        [Fact]
        public void A_shelf_of_television_is_not_counted_in_films()
        {
            var shows = new List<UiMovie> { Series("a", "A Show"), Series("b", "Another Show") };

            Assert.Equal("2 SERIES", LibraryGrouping.CountLabel(shows));
        }

        [Fact]
        public void The_search_field_offers_what_it_can_actually_search()
        {
            Assert.Equal("Search 2 films", LibraryGrouping.SearchWatermark(new[] { Film(1, "One"), Film(2, "Two") }));
            Assert.Equal("Search 1 series", LibraryGrouping.SearchWatermark(new[] { Series("a", "A Show") }));
            Assert.Equal(
                "Search 1 film and 1 series",
                LibraryGrouping.SearchWatermark(new[] { Film(1, "One"), Series("a", "A Show") }));
        }

        [Fact]
        public void A_programme_the_server_never_identified_lands_where_an_unenriched_film_does()
        {
            // Deliberate rather than accidental: both mean "nobody has said what this is", and the
            // kind row separates them in one click when that mixture is not what somebody wanted.
            var show = Series("a", "A Wholly Invented Programme");
            show.Genres = "";

            var bucket = LibraryGrouping.ItemsForGenre(
                new[] { Film(1, "An Unenriched Film"), show },
                LibraryGrouping.Uncategorised);

            Assert.Equal(2, bucket.Count);
        }
    }
}
