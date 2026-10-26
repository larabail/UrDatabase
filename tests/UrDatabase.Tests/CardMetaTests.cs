using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Dapper;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The line under a card's title: year, runtime and IMDb rating for a film, year, seasons and
    /// IMDb rating for a series. The rule lives in <see cref="CardMeta"/> so it can be tested here
    /// rather than eyeballed in the window.
    ///
    /// The repository tests read a real SQLite file in a temporary directory; nothing here touches
    /// the real app data directory or a network.
    /// </summary>
    public sealed class CardMetaTests : IDisposable
    {
        private readonly TempLog _log = new();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "urdb-cardmeta-" + Guid.NewGuid().ToString("N"));
        private string DbPath => Path.Combine(_dir, "movies.db");

        public CardMetaTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            _log.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static UiMovie Film(int? year = 2025, int? runtime = 135, double? imdb = 7.6, double? rating = null) => new()
        {
            Title = "A Wholly Invented Film",
            Year = year,
            RuntimeMinutes = runtime,
            ImdbRating = imdb,
            Rating = rating ?? imdb
        };

        private static UiMovie Series(int? year = 2011, int? seasons = 3, double? imdb = null, double? rating = null) => new()
        {
            Title = "A Wholly Invented Programme",
            Kind = MediaKind.Series,
            Year = year,
            SeasonCount = seasons,
            ImdbRating = imdb,
            Rating = rating ?? imdb
        };

        // ---------- films ----------

        [Fact]
        public void A_film_shows_its_year_runtime_and_IMDb_rating()
        {
            Assert.Equal("2025 · 2h 15m · ★ 7.6", Film().MetaLine);
        }

        [Fact]
        public void A_film_with_no_runtime_leaves_no_empty_separator()
        {
            Assert.Equal("2025 · ★ 7.6", Film(runtime: null).MetaLine);
            Assert.Equal("2025 · ★ 7.6", Film(runtime: 0).MetaLine);
        }

        [Fact]
        public void A_film_with_no_rating_ends_at_its_runtime()
        {
            Assert.Equal("2025 · 2h 15m", Film(imdb: null).MetaLine);
        }

        [Fact]
        public void A_film_with_no_year_starts_at_what_it_knows()
        {
            Assert.Equal("2h 15m · ★ 7.6", Film(year: null).MetaLine);
        }

        [Fact]
        public void A_film_that_knows_nothing_has_an_empty_line()
        {
            Assert.Equal("", Film(year: null, runtime: null, imdb: null).MetaLine);
        }

        [Fact]
        public void A_TMDB_or_server_rating_is_never_starred_as_IMDb()
        {
            // Rating is what the filter uses, and is a TMDB average or a Jellyfin community rating
            // when no IMDb one has been fetched. The card must not print it under the IMDb star.
            var film = Film(imdb: null, rating: 6.9);

            Assert.Equal("2025 · 2h 15m", film.MetaLine);
            Assert.DoesNotContain("6.9", film.MetaLine);
            Assert.DoesNotContain("IMDb", film.CardTooltip);
        }

        [Fact]
        public void When_both_are_known_the_IMDb_rating_is_the_one_printed()
        {
            var film = Film(imdb: 7.6, rating: 6.9);

            Assert.EndsWith("★ 7.6", film.MetaLine);
            Assert.Contains("IMDb 7.6", film.CardTooltip);
        }

        // ---------- series ----------

        [Fact]
        public void A_series_shows_its_seasons_and_rating_but_no_runtime()
        {
            var show = Series(imdb: 8.4);
            show.RuntimeMinutes = 45;

            Assert.Equal("2011 · 3 seasons · ★ 8.4", show.MetaLine);
        }

        [Fact]
        public void A_series_with_no_rating_keeps_the_line_it_had()
        {
            Assert.Equal("2011 · 3 seasons", Series().MetaLine);
            Assert.Equal("2011 · 1 season", Series(seasons: 1).MetaLine);
            Assert.Equal("3 seasons · ★ 8.4", Series(year: null, imdb: 8.4).MetaLine);
        }

        [Fact]
        public void A_series_with_only_a_server_rating_is_not_starred()
        {
            Assert.Equal("2011 · 3 seasons", Series(rating: 8.2).MetaLine);
        }

        // ---------- episodes ----------

        [Fact]
        public void An_episode_keeps_its_place_in_the_programme_and_nothing_else()
        {
            var episode = new UiMovie
            {
                Kind = MediaKind.Episode, Title = "Show", Year = 2020, SeasonNumber = 1, EpisodeNumber = 1,
                RuntimeMinutes = 50, ImdbRating = 8.1
            };

            Assert.Equal("S1E1", episode.MetaLine);
        }

        // ---------- the parts ----------

        [Theory]
        [InlineData(135, "2h 15m")]
        [InlineData(120, "2h")]
        [InlineData(60, "1h")]
        [InlineData(61, "1h 1m")]
        [InlineData(45, "45m")]
        [InlineData(1, "1m")]
        [InlineData(0, "")]
        [InlineData(-5, "")]
        [InlineData(null, "")]
        public void Runtimes_are_printed_in_hours_and_minutes(int? minutes, string expected)
        {
            Assert.Equal(expected, CardMeta.Runtime(minutes));
        }

        [Theory]
        [InlineData(7.6, "★ 7.6")]
        [InlineData(8.0, "★ 8.0")]
        [InlineData(7.25, "★ 7.3")]
        [InlineData(10.0, "★ 10.0")]
        [InlineData(0.0, "")]
        [InlineData(10.5, "")]
        [InlineData(null, "")]
        public void Ratings_are_printed_to_one_decimal_place(double? rating, string expected)
        {
            Assert.Equal(expected, CardMeta.Rating(rating));
        }

        [Fact]
        public void Not_a_number_is_not_a_rating()
        {
            Assert.Equal("", CardMeta.Rating(double.NaN));
        }

        [Fact]
        public void The_line_is_the_same_whatever_the_current_culture()
        {
            var before = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
                Assert.Equal("2025 · 2h 15m · ★ 7.6", Film().MetaLine);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = before;
            }
        }

        // ---------- a card already on screen ----------

        [Fact]
        public void Facts_landing_on_a_card_redraw_its_line()
        {
            var film = Film(runtime: null);
            var changed = new List<string?>();
            ((INotifyPropertyChanged)film).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            film.ApplyFacts(new FilmFacts(98, 6.1, new List<string>(), new List<string>()));

            Assert.Contains(nameof(UiMovie.MetaLine), changed);
            Assert.Equal("2025 · 1h 38m · ★ 7.6", film.MetaLine);
        }

        [Fact]
        public void A_TMDB_rating_from_the_facts_pass_does_not_get_a_star()
        {
            var film = Film(imdb: null);

            film.ApplyFacts(new FilmFacts(null, 6.1, new List<string>(), new List<string>()));

            Assert.Equal(6.1, film.Rating);
            Assert.Equal("2025 · 2h 15m", film.MetaLine);
        }

        [Fact]
        public void A_server_twin_folded_into_a_local_film_lends_its_runtime_but_not_a_star()
        {
            var local = Film(runtime: null, imdb: null);
            var server = new UiMovie { Source = MovieSource.Jellyfin, RemoteId = "x", RuntimeMinutes = 100, Rating = 7.9 };

            local.AdoptServerCopy(server);

            Assert.Equal(7.9, local.Rating);
            Assert.Equal("2025 · 1h 40m", local.MetaLine);
        }

        [Fact]
        public void A_server_film_never_has_an_IMDb_rating_of_its_own()
        {
            var card = JellyfinLibrary.ToUiMovie(new JellyfinMovie
            {
                ItemId = "abc", Title = "Server Film", Year = 2024, RuntimeMinutes = 95, CommunityRating = 7.2
            });

            Assert.Null(card.ImdbRating);
            Assert.Equal("2024 · 1h 35m", card.MetaLine);
        }

        // ---------- read from the catalogue ----------

        [Fact]
        public void The_library_read_separates_the_IMDb_rating_from_the_TMDB_one()
        {
            using (var conn = Database.Open(DbPath))
            {
                conn.Execute(@"
INSERT INTO movies (id, title, year, runtime_minutes, rating) VALUES (1, 'Rated', 2025, 135, 6.5);
INSERT INTO movies (id, title, year, runtime_minutes, rating) VALUES (2, 'TMDB only', 2024, 90, 6.8);
INSERT INTO imdb_ratings (imdb_id, movie_id, rating, fetched_at) VALUES ('tt1', 1, 7.6, '2026-01-01');
INSERT INTO imdb_ratings (imdb_id, movie_id, rating, fetched_at) VALUES ('tt2', 2, NULL, '2026-01-01');");
            }

            var films = new MovieRepository(DbPath).Query(null);
            var rated = films.Single(m => m.Id == 1);
            var tmdbOnly = films.Single(m => m.Id == 2);

            Assert.Equal(7.6, rated.ImdbRating);
            Assert.Equal(7.6, rated.Rating);
            Assert.Equal("2025 · 2h 15m · ★ 7.6", rated.MetaLine);

            Assert.Null(tmdbOnly.ImdbRating);
            Assert.Equal(6.8, tmdbOnly.Rating);
            Assert.Equal("2024 · 1h 30m", tmdbOnly.MetaLine);
        }

        [Fact]
        public void A_local_programme_wears_the_IMDb_rating_cached_for_its_IMDb_id()
        {
            using (var conn = Database.Open(DbPath))
            {
                conn.Execute(@"
INSERT INTO series (id, title, year, imdb_id, rating) VALUES (1, 'Rated Show', 2014, 'tt-show', 8.0);
INSERT INTO episodes (id, series_id, season_number, episode_number) VALUES (1, 1, 1, 1);
INSERT INTO files (episode_id, file_path) VALUES (1, '/tv/Rated.S01E01.mkv');
INSERT INTO series (id, title, year, rating) VALUES (2, 'Unrated Show', 2015, 7.7);
INSERT INTO episodes (id, series_id, season_number, episode_number) VALUES (2, 2, 1, 1);
INSERT INTO files (episode_id, file_path) VALUES (2, '/tv/Unrated.S01E01.mkv');
INSERT INTO imdb_ratings (imdb_id, movie_id, rating, fetched_at) VALUES ('tt-show', NULL, 8.9, '2026-01-01');");
            }

            var shows = new MovieRepository(DbPath).Query(null).Where(m => m.IsSeries).ToList();

            Assert.Equal("2014 · 1 season · ★ 8.9", shows.Single(s => s.Title == "Rated Show").MetaLine);
            Assert.Equal("2015 · 1 season", shows.Single(s => s.Title == "Unrated Show").MetaLine);
        }
    }
}
