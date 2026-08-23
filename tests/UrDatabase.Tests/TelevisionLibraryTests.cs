using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Television arriving in a library that has only ever held films: how a series becomes a
    /// card, what stops it being folded onto a film, and what the app says about a library that
    /// holds both.
    /// </summary>
    public class TelevisionLibraryTests
    {
        private static JellyfinSeries Show(string id, string title, int? year = 2011) => new()
        {
            ItemId = id,
            Title = title,
            Year = year,
            Genres = "Drama, Crime",
            TmdbId = "1396",
            SeasonCount = 5,
            EpisodeCount = 62,
            ImageTag = "tag-" + id
        };

        // ---------- a series as a card ----------

        [Fact]
        public void A_series_becomes_a_card_that_knows_what_it_is()
        {
            var card = JellyfinLibrary.ToUiSeries(Show("series1", "A Wholly Invented Programme"));

            Assert.Equal(MediaKind.Series, card.Kind);
            Assert.Equal(MovieSource.Jellyfin, card.Source);
            Assert.Equal("series1", card.RemoteId);
            Assert.Equal("A Wholly Invented Programme", card.Title);
            Assert.Equal("Drama, Crime", card.Genres);
            Assert.Equal(5, card.SeasonCount);
            Assert.Equal(62, card.EpisodeCount);
        }

        [Fact]
        public void A_series_never_carries_a_tmdb_film_id()
        {
            // Jellyfin reports a TMDB *television* id for a programme: a different catalogue with
            // its own numbering, where 1396 is a different work from film 1396. Carrying it would
            // let a programme fold onto an unrelated film by number.
            var card = JellyfinLibrary.ToUiSeries(Show("series1", "A Wholly Invented Programme"));

            Assert.Null(card.TmdbId);
        }

        [Fact]
        public void The_poster_url_is_supplied_by_the_caller_as_it_is_for_a_film()
        {
            var card = JellyfinLibrary.ToUiSeries(
                Show("series1", "A Wholly Invented Programme"),
                s => $"http://media.invalid/Items/{s.ItemId}/Images/Primary?tag={s.ImageTag}");

            Assert.Equal("http://media.invalid/Items/series1/Images/Primary?tag=tag-series1", card.PosterPath);
        }

        [Fact]
        public void A_series_with_no_id_is_dropped_from_the_list()
        {
            var cards = JellyfinLibrary.ToUiSeriesList(new[]
            {
                Show("series1", "A Wholly Invented Programme"),
                Show("", "A Programme With No Id")
            });

            Assert.Equal("series1", Assert.Single(cards).RemoteId);
        }

        // ---------- what stops a fold ----------

        [Fact]
        public void A_programme_is_never_folded_onto_the_film_of_the_same_name()
        {
            // Fargo, Hannibal, Westworld and Shogun are each a film and a programme, and
            // normalising the titles makes them one. A fold would keep the film and lose the
            // programme from the library entirely, rather than merely showing it beside.
            var local = new UiMovie { Id = 1, Title = "Fargo", Year = 1996 };
            var show = JellyfinLibrary.ToUiSeries(Show("series1", "Fargo", 2014));

            var merged = JellyfinLibrary.Merge(new[] { local }, new[] { show });

            Assert.Equal(2, merged.Count);
            Assert.Contains(merged, m => m.IsSeries);
            Assert.False(local.IsOnServer);
        }

        [Fact]
        public void A_programme_with_the_same_year_as_the_film_is_still_not_folded()
        {
            var local = new UiMovie { Id = 1, Title = "Fargo", Year = 2014 };
            var show = JellyfinLibrary.ToUiSeries(Show("series1", "Fargo", 2014));

            Assert.Equal(2, JellyfinLibrary.Merge(new[] { local }, new[] { show }).Count);
        }

        [Fact]
        public void A_server_film_is_still_folded_onto_its_local_copy()
        {
            // The fold that television must not break. A film in both places is one card carrying
            // both facts, and adding series to the same list must leave that alone.
            var local = new UiMovie { Id = 1, Title = "A Wholly Invented Film", Year = 1994 };
            var remote = JellyfinLibrary.ToUiMovie(new JellyfinMovie
            {
                ItemId = "film1",
                Title = "A Wholly Invented Film",
                Year = 1994
            });

            var merged = JellyfinLibrary.Merge(
                new[] { local },
                new[] { remote, JellyfinLibrary.ToUiSeries(Show("series1", "A Wholly Invented Programme")) });

            Assert.Equal(2, merged.Count);
            Assert.True(local.IsOnServer);
        }

        [Fact]
        public void Searching_the_server_reaches_the_television_too()
        {
            var library = new[]
            {
                JellyfinLibrary.ToUiMovie(new JellyfinMovie { ItemId = "film1", Title = "A Wholly Invented Film" }),
                JellyfinLibrary.ToUiSeries(Show("series1", "A Wholly Invented Programme"))
            };

            var found = JellyfinLibrary.Search(library, "Programme");

            Assert.Equal("A Wholly Invented Programme", Assert.Single(found).Title);
        }

        // ---------- the line under the library ----------

        [Fact]
        public void The_status_line_names_the_television_separately()
        {
            // "0 films on the Jellyfin server" is true of films and false about a library of four
            // hundred programmes, which is what this used to say.
            var status = LibraryStatus.Describe(
                localCount: 3,
                localWithPosters: 2,
                remoteCount: 0,
                hasLocalDatabase: true,
                databasePath: "/tmp/movies.db",
                remoteSeriesCount: 12);

            Assert.Contains("12 series on the Jellyfin server", status);
            Assert.DoesNotContain("0 films", status);
        }

        [Fact]
        public void The_status_line_names_both_when_a_server_holds_both()
        {
            var status = LibraryStatus.Describe(
                localCount: 3,
                localWithPosters: 2,
                remoteCount: 400,
                hasLocalDatabase: true,
                databasePath: "/tmp/movies.db",
                remoteSeriesCount: 12);

            Assert.Contains("400 films and 12 series on the Jellyfin server", status);
        }

        [Fact]
        public void A_film_only_server_reads_exactly_as_it_did_before()
        {
            var status = LibraryStatus.Describe(
                localCount: 3,
                localWithPosters: 2,
                remoteCount: 400,
                hasLocalDatabase: true,
                databasePath: "/tmp/movies.db");

            Assert.Equal("Posters present: 2/3 · 400 films on the Jellyfin server", status);
        }

        [Fact]
        public void A_television_only_server_is_not_an_empty_library()
        {
            // No catalogue on this machine and no films on the server, but twelve programmes: the
            // "no library yet" sentence would send somebody to look for a database that is not the
            // problem.
            var status = LibraryStatus.Describe(
                localCount: 0,
                localWithPosters: 0,
                remoteCount: 0,
                hasLocalDatabase: false,
                databasePath: "/tmp/movies.db",
                remoteSeriesCount: 12);

            Assert.DoesNotContain("No library yet", status);
        }

        // ---------- what a sync says it did ----------

        [Fact]
        public void A_sync_of_films_alone_says_what_it_always_said()
        {
            Assert.Equal("Jellyfin: 412 films from the server.", new JellyfinSyncResult(412, 0).Describe());
            Assert.Equal("Jellyfin: 1 film from the server.", new JellyfinSyncResult(1, 0).Describe());
        }

        [Fact]
        public void A_sync_names_the_television_when_there_is_some()
        {
            Assert.Equal(
                "Jellyfin: 412 films and 12 series from the server.",
                new JellyfinSyncResult(412, 12).Describe());

            Assert.Equal("Jellyfin: 12 series from the server.", new JellyfinSyncResult(0, 12).Describe());

            // "series" is its own plural, which is the sort of thing that reaches a screenshot as
            // "1 seriess" if it is left to a format string.
            Assert.Equal("Jellyfin: 1 series from the server.", new JellyfinSyncResult(0, 1).Describe());
        }

        [Fact]
        public void A_sync_that_found_nothing_does_not_pretend_otherwise()
        {
            Assert.Equal(
                "Jellyfin: the server reported nothing this app can read.",
                new JellyfinSyncResult(0, 0).Describe());
        }

        // ---------- the facts under a programme's title ----------

        [Fact]
        public void A_programme_states_its_seasons_and_episodes_rather_than_a_runtime()
        {
            var facts = DetailFacts.For(new SeriesDetailsVm
            {
                Title = "A Wholly Invented Programme",
                Year = 2011,
                SeasonCount = 5,
                EpisodeCount = 62,
                CommunityRating = 8.4,
                ImdbRating = 9.2
            });

            Assert.Equal(
                new[] { "FROM", "SEASONS", "EPISODES", "IMDB", "JELLYFIN" },
                facts.Select(f => f.Label).ToArray());

            Assert.Equal("5", facts[1].Value);
            Assert.Equal("62", facts[2].Value);

            // Each number under its own source's name, exactly as on a film: they are different
            // measurements of different populations.
            Assert.True(facts[3].IsImdb);
            Assert.True(facts[4].IsServer);

            Assert.False(facts[^1].ShowSeparator);
        }

        [Fact]
        public void A_count_the_server_never_gave_is_left_off_the_row()
        {
            var facts = DetailFacts.For(new SeriesDetailsVm { Title = "A Programme", Year = 2011 });

            Assert.Equal("FROM", Assert.Single(facts).Label);
        }

        [Fact]
        public void A_programme_with_nothing_known_about_it_produces_an_empty_row()
        {
            Assert.Empty(DetailFacts.For(new SeriesDetailsVm()));
        }
    }
}
