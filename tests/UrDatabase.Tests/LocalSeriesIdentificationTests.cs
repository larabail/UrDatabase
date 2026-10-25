using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// A programme on this disk identified against TMDB once, described in the catalogue, and its
    /// episodes named a season at a time when it is opened.
    ///
    /// Every catalogue here is a temporary file this class creates and deletes, and every network
    /// answer comes from a <see cref="FakeHttpMessageHandler"/>.
    /// </summary>
    public class LocalSeriesIdentificationTests : IDisposable
    {
        private readonly string _root;
        private readonly string _dbPath;
        private readonly TempLog _log = new();

        public LocalSeriesIdentificationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-tvid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _dbPath = Path.Combine(_root, "movies.db");
        }

        public void Dispose()
        {
            _log.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---------- fixtures ----------

        /// <summary>Fargo (2014) with three episodes over two seasons, and a film that shares its id.</summary>
        private void Seed(int? year = 2014, string title = "Fargo")
        {
            using var conn = Database.Open(_dbPath);
            conn.Execute(@"
INSERT INTO movies (id, title, year) VALUES (1, 'Fargo', 1996);
INSERT INTO series (id, title, year) VALUES (1, @title, @year);
INSERT INTO episodes (id, series_id, season_number, episode_number, title) VALUES (1, 1, 1, 1, 'Crocodiles Dilemma');
INSERT INTO episodes (id, series_id, season_number, episode_number) VALUES (2, 1, 1, 2);
INSERT INTO episodes (id, series_id, season_number, episode_number) VALUES (3, 1, 1, 3);
INSERT INTO episodes (id, series_id, season_number, episode_number) VALUES (4, 1, 2, 1);
INSERT INTO files (episode_id, file_path) VALUES (1, '/tv/Fargo.S01E01.mkv');
INSERT INTO files (episode_id, file_path) VALUES (2, '/tv/Fargo.S01E02.mkv');
INSERT INTO files (episode_id, file_path) VALUES (3, '/tv/Fargo.S01E03.mkv');
INSERT INTO files (episode_id, file_path) VALUES (4, '/tv/Fargo.S02E01.mkv');", new { title, year });
        }

        private static FakeHttpMessageHandler Tmdb(string? seasonTwo = null) => FakeHttpMessageHandler.Routed(
            ("/search/tv", HttpStatusCode.OK, TmdbTelevisionTests.FargoSearch),
            ("/tv/60622/season/1", HttpStatusCode.OK, TmdbTelevisionTests.FargoSeasonOne),
            ("/tv/60622/season/2", seasonTwo is null ? HttpStatusCode.NotFound : HttpStatusCode.OK, seasonTwo ?? "{}"),
            ("/tv/60622", HttpStatusCode.OK, TmdbTelevisionTests.FargoDetails));

        private static TmdbService Service(FakeHttpMessageHandler handler, string key = "not-a-real-key") =>
            new(apiKey: key, posterCacheDir: "", imageSize: "w342", downloadPosters: false, handler: handler,
                requests: new TmdbRequestScheduler(TimeSpan.Zero));

        private AppConfig Configured(string key = "not-a-real-key") => new()
        {
            TmdbApiKey = key,
            DatabasePath = _dbPath,
            PosterCacheDir = Path.Combine(_root, "posters"),
            DownloadPosters = false
        };

        private T Scalar<T>(string sql)
        {
            using var conn = Database.Open(_dbPath);
            return conn.ExecuteScalar<T>(sql)!;
        }

        private static int Count(FakeHttpMessageHandler handler, string fragment) =>
            handler.Requests.Count(r => r.Contains(fragment, StringComparison.Ordinal));

        // ---------- identification ----------

        [Fact]
        public async Task A_programme_is_identified_and_described_without_being_renamed()
        {
            Seed();
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            var result = await LocalSeriesLookup.IdentifyAsync(tmdb, conn, 1, downloadPosters: false, CancellationToken.None);

            Assert.True(result.Matched);
            Assert.Equal("Crime, Science Fiction, Fantasy", result.Found.Genres);
            Assert.Equal("https://image.tmdb.org/t/p/w342/fargo.jpg", result.Found.PosterPath);

            var stored = LocalSeriesLookup.Read(conn, 1)!;
            Assert.Equal("Fargo", stored.Title);
            Assert.Equal(60622, stored.TmdbId);
            Assert.True(stored.Described);
            Assert.Equal("A crime anthology set in the snow.", stored.Overview);
            Assert.Equal("/fargo-backdrop.jpg", stored.BackdropPath);
            Assert.Equal(8.3, stored.Rating);
            Assert.Equal("tt2802850", stored.ImdbId);
            Assert.Equal(new[] { "Billy Bob Thornton (Lorne Malvo)" }, stored.Cast);
            Assert.Equal(new[] { "Creator: Noah Hawley" }, stored.Crew);
            Assert.Equal("Crime, Science Fiction, Fantasy", stored.Genres);

            // One search and one description: never a request per episode, never the film catalogue.
            Assert.Equal(2, handler.CallCount);
            Assert.Equal(0, Count(handler, "/season/"));
            Assert.Equal(0, Count(handler, "/search/movie"));

            // The film that shares the programme's id is untouched.
            Assert.Null(conn.ExecuteScalar<string?>("SELECT poster_path FROM movies WHERE id = 1"));
            Assert.Null(conn.ExecuteScalar<long?>("SELECT tmdb_id FROM movies WHERE id = 1"));
        }

        [Fact]
        public async Task A_refused_match_writes_nothing_and_leaves_the_programme_for_a_person_to_choose()
        {
            Seed(year: null, title: "The Office");
            using var handler = FakeHttpMessageHandler.Routed(("/search/tv", HttpStatusCode.OK, @"{ ""results"": [
                { ""id"": 2316, ""name"": ""The Office"", ""first_air_date"": ""2005-03-24"" },
                { ""id"": 2996, ""name"": ""The Office"", ""first_air_date"": ""2001-07-09"" } ] }"));
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            var result = await LocalSeriesLookup.IdentifyAsync(tmdb, conn, 1, downloadPosters: false, CancellationToken.None);

            Assert.False(result.Matched);
            var stored = LocalSeriesLookup.Read(conn, 1)!;
            Assert.Null(stored.TmdbId);
            Assert.False(stored.Described);
            Assert.Null(stored.PosterPath);
            Assert.Null(stored.Genres);
            Assert.Equal(0, Count(handler, "/tv/"));
        }

        /// <summary>
        /// An identification already stored — perhaps chosen by a person — is described as it is,
        /// not re-derived from the title, which could find a different programme.
        /// </summary>
        [Fact]
        public async Task A_stored_identification_is_described_without_searching_again()
        {
            Seed();
            using (var conn = Database.Open(_dbPath))
                conn.Execute("UPDATE series SET tmdb_id = 60622 WHERE id = 1");

            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using var db = Database.Open(_dbPath);

            var result = await LocalSeriesLookup.IdentifyAsync(tmdb, db, 1, downloadPosters: false, CancellationToken.None);

            Assert.True(result.Matched);
            Assert.Equal(0, Count(handler, "/search/"));
            Assert.True(LocalSeriesLookup.Read(db, 1)!.Described);
        }

        /// <summary>A person's genres are not overruled by TMDB's on every launch.</summary>
        [Fact]
        public async Task Genres_already_stored_are_kept()
        {
            Seed();
            using (var conn = Database.Open(_dbPath))
                conn.Execute("UPDATE series SET genres = 'Comedy' WHERE id = 1");

            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using var db = Database.Open(_dbPath);

            var result = await LocalSeriesLookup.IdentifyAsync(tmdb, db, 1, downloadPosters: false, CancellationToken.None);

            Assert.Equal("Comedy", result.Found.Genres);
            Assert.Equal("Comedy", LocalSeriesLookup.Read(db, 1)!.Genres);
        }

        /// <summary>
        /// A downloaded poster is cached under its own name. <c>series</c> and <c>movies</c> both
        /// number from one, and the film's <c>1.jpg</c> must never be taken for programme 1's.
        /// </summary>
        [Fact]
        public void A_programme_poster_is_cached_apart_from_the_film_with_the_same_id()
        {
            Assert.Equal("series-1.jpg", LocalSeriesLookup.PosterFileName(1));
            Assert.NotEqual("1.jpg", LocalSeriesLookup.PosterFileName(1));
        }

        [Fact]
        public async Task A_failed_description_throws_rather_than_being_recorded_as_an_answer()
        {
            Seed();
            using var handler = FakeHttpMessageHandler.Routed(
                ("/search/tv", HttpStatusCode.OK, TmdbTelevisionTests.FargoSearch),
                ("/tv/60622", HttpStatusCode.Unauthorized, "{}"));
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            await Assert.ThrowsAnyAsync<System.Net.Http.HttpRequestException>(
                () => LocalSeriesLookup.IdentifyAsync(tmdb, conn, 1, downloadPosters: false, CancellationToken.None));

            Assert.Null(LocalSeriesLookup.Read(conn, 1)!.TmdbId);
        }

        // ---------- episodes ----------

        [Fact]
        public async Task Opening_an_identified_programme_names_its_episodes_from_TMDB_once()
        {
            Seed();
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using (var conn = Database.Open(_dbPath))
                await LocalSeriesLookup.IdentifyAsync(tmdb, conn, 1, downloadPosters: false, CancellationToken.None);

            var loader = new LocalSeriesLoader(_dbPath);
            var before = handler.CallCount;

            var episodes = await loader.RefreshEpisodesAsync(tmdb, 1, force: false, CancellationToken.None);

            Assert.NotNull(episodes);
            var first = episodes!.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 1);
            Assert.Equal("The Crocodile's Dilemma", first.TmdbTitle);
            Assert.Equal("Crocodiles Dilemma", first.Title);
            Assert.Equal("A stranger arrives in Bemidji.", first.Overview);
            Assert.Equal(68, first.RuntimeMinutes);
            Assert.Equal("2014-04-15", first.AirDate);

            // Two seasons, two requests — not one per episode.
            Assert.Equal(2, handler.CallCount - before);

            // The second visit reads the catalogue and asks nothing.
            Assert.Null(await loader.RefreshEpisodesAsync(tmdb, 1, force: false, CancellationToken.None));
            Assert.Equal(2, handler.CallCount - before);

            // And the cached names are what the screen reads first.
            var cached = loader.Load(1).Single(e => e.Id == 1);
            Assert.Equal("The Crocodile's Dilemma", cached.TmdbTitle);
        }

        /// <summary>
        /// An episode TMDB does not list — here the whole of a season it does not have — is marked
        /// as asked about, or it would cost a request on every open for ever.
        /// </summary>
        [Fact]
        public async Task An_episode_TMDB_does_not_list_is_not_asked_about_again()
        {
            Seed();
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using (var conn = Database.Open(_dbPath))
                await LocalSeriesLookup.IdentifyAsync(tmdb, conn, 1, downloadPosters: false, CancellationToken.None);

            var loader = new LocalSeriesLoader(_dbPath);
            var episodes = await loader.RefreshEpisodesAsync(tmdb, 1, force: false, CancellationToken.None);

            Assert.Null(episodes!.Single(e => e.EpisodeNumber == 3).TmdbTitle);
            Assert.Null(episodes!.Single(e => e.SeasonNumber == 2).TmdbTitle);
            Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM episodes WHERE tmdb_fetched_at IS NULL"));
        }

        [Fact]
        public async Task A_manual_refresh_asks_about_every_season_again()
        {
            Seed();
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using (var conn = Database.Open(_dbPath))
                await LocalSeriesLookup.IdentifyAsync(tmdb, conn, 1, downloadPosters: false, CancellationToken.None);

            var loader = new LocalSeriesLoader(_dbPath);
            await loader.RefreshEpisodesAsync(tmdb, 1, force: false, CancellationToken.None);
            var before = handler.CallCount;

            Assert.NotNull(await loader.RefreshEpisodesAsync(tmdb, 1, force: true, CancellationToken.None));
            Assert.Equal(2, handler.CallCount - before);
        }

        [Fact]
        public async Task An_unidentified_programme_asks_TMDB_nothing_about_its_episodes()
        {
            Seed();
            using var handler = Tmdb();
            using var tmdb = Service(handler);

            Assert.Null(await new LocalSeriesLoader(_dbPath).RefreshEpisodesAsync(tmdb, 1, force: true, CancellationToken.None));
            Assert.Equal(0, handler.CallCount);
        }

        /// <summary>A loader given no TMDB client is the catalogue reader layer 1 shipped.</summary>
        [Fact]
        public async Task Without_a_key_the_episode_list_is_only_ever_read_from_the_catalogue()
        {
            Seed();
            using (var conn = Database.Open(_dbPath))
                conn.Execute("UPDATE series SET tmdb_id = 60622, tmdb_fetched_at = '2026-01-01' WHERE id = 1");

            Assert.Null(await new LocalSeriesLoader(_dbPath).RefreshEpisodesAsync(1, force: true, CancellationToken.None));
            Assert.Null(await new LocalSeriesLoader(_dbPath, () => null).RefreshEpisodesAsync(1, force: true, CancellationToken.None));
        }

        /// <summary>
        /// Episode names belong to the programme they were fetched for. A corrected identification
        /// clears them so the corrected programme's seasons are asked about.
        /// </summary>
        [Fact]
        public async Task A_changed_identification_forgets_the_episodes_of_the_old_one()
        {
            Seed();
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);
            await LocalSeriesLookup.IdentifyAsync(tmdb, conn, 1, downloadPosters: false, CancellationToken.None);
            await new LocalSeriesLoader(_dbPath).RefreshEpisodesAsync(tmdb, 1, force: false, CancellationToken.None);

            await LocalSeriesLookup.SaveDescriptionAsync(conn, 1, new SeriesDescription(
                99, null, null, "Another programme.", null, null, null, Array.Empty<string>(), Array.Empty<string>()));

            Assert.Equal(99L, conn.ExecuteScalar<long>("SELECT tmdb_id FROM series WHERE id = 1"));
            Assert.Equal(0L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM episodes WHERE tmdb_title IS NOT NULL OR tmdb_fetched_at IS NOT NULL"));
        }

        [Fact]
        public async Task Describing_the_same_identification_again_keeps_the_episode_names()
        {
            Seed();
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);
            await LocalSeriesLookup.IdentifyAsync(tmdb, conn, 1, downloadPosters: false, CancellationToken.None);
            await new LocalSeriesLoader(_dbPath).RefreshEpisodesAsync(tmdb, 1, force: false, CancellationToken.None);

            conn.Execute("UPDATE series SET tmdb_fetched_at = NULL WHERE id = 1");
            await LocalSeriesLookup.IdentifyAsync(tmdb, conn, 1, downloadPosters: false, CancellationToken.None);

            Assert.Equal("The Crocodile's Dilemma", conn.ExecuteScalar<string>("SELECT tmdb_title FROM episodes WHERE id = 1"));
        }

        // ---------- the row ----------

        [Fact]
        public void A_described_episode_shows_TMDB_name_plot_and_length_over_its_filename()
        {
            var row = SeriesGrouping.ToRow(new LocalEpisode
            {
                SeasonNumber = 1, EpisodeNumber = 1, Title = "Crocodiles Dilemma",
                TmdbTitle = "The Crocodile's Dilemma", Overview = "A stranger arrives.", RuntimeMinutes = 68,
                FilePath = "/tv/Fargo.S01E01.mkv"
            });

            Assert.Equal("The Crocodile's Dilemma", row.Title);
            Assert.Equal("A stranger arrives.", row.Overview);
            Assert.Equal("68 min", row.Runtime);
        }

        [Fact]
        public void An_undescribed_episode_reads_as_it_did_before_anything_was_looked_up()
        {
            var row = SeriesGrouping.ToRow(new LocalEpisode
            {
                SeasonNumber = 1, EpisodeNumber = 2, FilePath = "/tv/Fargo.S01E02.mkv"
            });

            Assert.Equal("Episode 2", row.Title);
            Assert.Equal("Fargo.S01E02.mkv", row.Overview);
            Assert.Equal("", row.Runtime);
        }

        /// <summary>
        /// Firefly on DVD puts "Serenity" first; TMDB's aired order puts "The Train Job" there.
        /// Matched on the number alone, the row would wear the other episode's name and plot.
        /// The file named itself, so its name stands and nothing of TMDB's is mixed into the row.
        /// </summary>
        [Fact]
        public void A_file_whose_own_title_disagrees_with_TMDB_keeps_its_title_and_takes_nothing_from_TMDB()
        {
            var row = SeriesGrouping.ToRow(new LocalEpisode
            {
                SeasonNumber = 1, EpisodeNumber = 1, Title = "Serenity",
                TmdbTitle = "The Train Job", Overview = "A train heist goes wrong.", RuntimeMinutes = 42,
                FilePath = "/tv/Firefly.S01E01.Serenity.mkv"
            });

            Assert.Equal("Serenity", row.Title);
            Assert.Equal("Firefly.S01E01.Serenity.mkv", row.Overview);
            Assert.Equal("", row.Runtime);
        }

        /// <summary>TMDB numbers specials however it likes; season 0 is never described from it.</summary>
        [Fact]
        public void A_special_is_never_described_by_TMDB_episode_number()
        {
            var row = SeriesGrouping.ToRow(new LocalEpisode
            {
                SeasonNumber = 0, EpisodeNumber = 1,
                TmdbTitle = "Some Other Special", Overview = "Not this one.", RuntimeMinutes = 30,
                FilePath = "/tv/Show.S00E01.mkv"
            });

            Assert.Equal("Episode 1", row.Title);
            Assert.Equal("Show.S00E01.mkv", row.Overview);
            Assert.Equal("", row.Runtime);
        }

        [Fact]
        public void An_untitled_file_takes_TMDB_name_plot_and_length()
        {
            var row = SeriesGrouping.ToRow(new LocalEpisode
            {
                SeasonNumber = 1, EpisodeNumber = 2,
                TmdbTitle = "The Rooster Prince", Overview = "Lester hides.", RuntimeMinutes = 47,
                FilePath = "/tv/Fargo.S01E02.mkv"
            });

            Assert.Equal("The Rooster Prince", row.Title);
            Assert.Equal("Lester hides.", row.Overview);
            Assert.Equal("47 min", row.Runtime);
        }

        [Theory]
        [InlineData("Crocodiles Dilemma", "The Crocodile's Dilemma", true)]
        [InlineData("the train job", "The Train Job", true)]
        [InlineData("Serenity", "The Train Job", false)]
        [InlineData(null, "The Train Job", true)]
        [InlineData("", "The Train Job", true)]
        public void A_file_title_and_a_TMDB_name_agree_once_normalised(string? file, string tmdb, bool agree)
        {
            Assert.Equal(agree, LocalSeriesLookup.TitlesAgree(file, tmdb));
        }

        /// <summary>The catalogue must not hold another episode's description on this row either.</summary>
        [Fact]
        public async Task A_season_in_another_order_stores_nothing_on_an_episode_whose_title_disagrees()
        {
            Seed();
            using (var conn = Database.Open(_dbPath))
                conn.Execute("UPDATE episodes SET title = 'Serenity' WHERE id = 2");

            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using (var conn = Database.Open(_dbPath))
                await LocalSeriesLookup.IdentifyAsync(tmdb, conn, 1, downloadPosters: false, CancellationToken.None);

            var episodes = await new LocalSeriesLoader(_dbPath).RefreshEpisodesAsync(tmdb, 1, force: false, CancellationToken.None);

            var disagreeing = episodes!.Single(e => e.Id == 2);
            Assert.Null(disagreeing.TmdbTitle);
            Assert.Null(disagreeing.Overview);
            Assert.Null(disagreeing.RuntimeMinutes);
            Assert.Equal("Serenity", SeriesGrouping.ToRow(disagreeing).Title);

            // Marked as asked about, so it is not asked about on every open.
            Assert.NotNull(Scalar<string?>("SELECT tmdb_fetched_at FROM episodes WHERE id = 2"));

            // The episode whose title agrees still gets TMDB's description.
            Assert.Equal("The Crocodile's Dilemma", episodes!.Single(e => e.Id == 1).TmdbTitle);
        }

        [Fact]
        public async Task Specials_are_not_asked_about()
        {
            Seed();
            using (var conn = Database.Open(_dbPath))
                conn.Execute(@"
INSERT INTO episodes (id, series_id, season_number, episode_number) VALUES (5, 1, 0, 1);
INSERT INTO files (episode_id, file_path) VALUES (5, '/tv/Fargo.S00E01.mkv');");

            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using (var conn = Database.Open(_dbPath))
                await LocalSeriesLookup.IdentifyAsync(tmdb, conn, 1, downloadPosters: false, CancellationToken.None);

            await new LocalSeriesLoader(_dbPath).RefreshEpisodesAsync(tmdb, 1, force: true, CancellationToken.None);

            Assert.Equal(0, Count(handler, "/season/0"));
            Assert.Null(Scalar<string?>("SELECT tmdb_title FROM episodes WHERE id = 5"));
        }

        [Fact]
        public void A_missing_episode_says_so_even_when_TMDB_has_described_it()
        {
            var row = SeriesGrouping.ToRow(new LocalEpisode
            {
                SeasonNumber = 1, EpisodeNumber = 1, TmdbTitle = "Pilot", Overview = "Plot.",
                AnyPath = "/tv/Show.S01E01.mkv"
            });

            Assert.Equal("Pilot", row.Title);
            Assert.StartsWith("Not found by the last scan", row.Overview, StringComparison.Ordinal);
        }

        // ---------- the page ----------

        [Fact]
        public void Without_a_key_an_unidentified_programme_reads_exactly_as_before()
        {
            var vm = LocalSeriesPage.Build(Card(), details: null, tmdbConfigured: false);

            Assert.Equal(LocalSeriesPage.FilesAttribution, LocalSeriesPage.Attribution(vm));
            Assert.Equal(
                "No summary yet: this programme was found on this computer and has not been looked up.",
                LocalSeriesPage.MissingOverview(vm));
            Assert.Equal("Not known yet for a programme found on this computer.", LocalSeriesPage.MissingCast(vm));
            Assert.Null(vm.ImdbId);
            Assert.Null(vm.BackdropUrl);
        }

        [Fact]
        public async Task An_identified_programme_opens_with_TMDB_description_cast_backdrop_and_imdb_id()
        {
            Seed();
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using (var conn = Database.Open(_dbPath))
                await LocalSeriesLookup.IdentifyAsync(tmdb, conn, 1, downloadPosters: false, CancellationToken.None);

            var details = new LocalSeriesLoader(_dbPath).LoadDetails(1);
            var vm = LocalSeriesPage.Build(Card(), details, tmdbConfigured: true);

            Assert.True(vm.IsIdentified);
            Assert.Equal(60622, vm.TmdbId);
            Assert.Equal("A crime anthology set in the snow.", vm.Overview);
            Assert.Equal("tt2802850", vm.ImdbId);
            Assert.Equal("https://image.tmdb.org/t/p/w1280/fargo-backdrop.jpg", vm.BackdropUrl);
            Assert.Equal(new[] { "Billy Bob Thornton (Lorne Malvo)" }, vm.TopCast);
            Assert.Equal("Crime, Science Fiction, Fantasy", vm.Genres);
            Assert.Equal(LocalSeriesPage.TmdbAttribution, LocalSeriesPage.Attribution(vm));
            Assert.Null(vm.CommunityRating);
        }

        [Fact]
        public void A_programme_TMDB_could_not_confirm_says_so_when_a_key_is_configured()
        {
            var vm = LocalSeriesPage.Build(Card(), details: null, tmdbConfigured: true);

            Assert.False(vm.IsIdentified);
            Assert.Equal(LocalSeriesPage.FilesAttribution, LocalSeriesPage.Attribution(vm));
            Assert.Contains("TMDB has not confirmed", LocalSeriesPage.MissingOverview(vm), StringComparison.Ordinal);
        }

        private static UiMovie Card() => new()
        {
            Id = 1, Title = "Fargo", Year = 2014, Kind = MediaKind.Series, Source = MovieSource.Local,
            SeasonCount = 2, EpisodeCount = 4
        };

        // ---------- the background pass ----------

        [Fact]
        public async Task The_background_pass_identifies_a_programme_and_reports_its_card()
        {
            Seed();
            using var handler = Tmdb();
            using var loader = new PosterAutoLoader(Configured(), _dbPath, handler: handler,
                requests: new TmdbRequestScheduler(TimeSpan.Zero));

            var reported = new List<Enrichment>();
            loader.QueueSeries(1, found => { lock (reported) reported.Add(found); }, CancellationToken.None);
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            var found = Assert.Single(reported);
            Assert.Equal("Crime, Science Fiction, Fantasy", found.Genres);
            Assert.True(found.HasPoster);
            Assert.Equal(60622L, Scalar<long>("SELECT tmdb_id FROM series WHERE id = 1"));

            // Films are counted in the progress line; programmes are not.
            Assert.Equal(0, loader.Progress.Total);
        }

        /// <summary>
        /// Every library rebuild offers every programme again. A programme already described is
        /// answered from the catalogue, and one TMDB refused is not asked about again until Refresh.
        /// </summary>
        [Fact]
        public async Task A_programme_is_asked_about_once_however_often_it_is_offered()
        {
            Seed(year: null, title: "The Office");
            using var handler = FakeHttpMessageHandler.Routed(("/search/tv", HttpStatusCode.OK, @"{ ""results"": [
                { ""id"": 2316, ""name"": ""The Office"", ""first_air_date"": ""2005-03-24"" },
                { ""id"": 2996, ""name"": ""The Office"", ""first_air_date"": ""2001-07-09"" } ] }"));
            using var loader = new PosterAutoLoader(Configured(), _dbPath, handler: handler,
                requests: new TmdbRequestScheduler(TimeSpan.Zero));

            for (var round = 0; round < 3; round++)
            {
                loader.QueueSeries(1, _ => { }, CancellationToken.None);
                Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));
            }

            var asked = handler.CallCount;
            Assert.True(asked > 0);

            await loader.RetryMissingAsync();
            loader.QueueSeries(1, _ => { }, CancellationToken.None);
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));
            Assert.True(handler.CallCount > asked);
        }

        [Fact]
        public async Task A_described_programme_is_reported_from_the_catalogue_with_no_request()
        {
            Seed();
            using (var first = Tmdb())
            using (var tmdb = Service(first))
            using (var conn = Database.Open(_dbPath))
                await LocalSeriesLookup.IdentifyAsync(tmdb, conn, 1, downloadPosters: false, CancellationToken.None);

            using var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("no request expected"));
            using var loader = new PosterAutoLoader(Configured(), _dbPath, handler: handler,
                requests: new TmdbRequestScheduler(TimeSpan.Zero));

            var reported = new List<Enrichment>();
            loader.QueueSeries(1, found => { lock (reported) reported.Add(found); }, CancellationToken.None);
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            Assert.Equal("Crime, Science Fiction, Fantasy", Assert.Single(reported).Genres);
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task Without_a_key_the_background_pass_leaves_programmes_alone()
        {
            Seed();
            using var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("no request expected"));
            using var loader = new PosterAutoLoader(Configured(key: ""), _dbPath, handler: handler,
                requests: new TmdbRequestScheduler(TimeSpan.Zero));

            loader.QueueSeries(1, _ => throw new InvalidOperationException("nothing to report"), CancellationToken.None);
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(0, handler.CallCount);
            Assert.Null(Scalar<long?>("SELECT tmdb_id FROM series WHERE id = 1"));
        }

        [Fact]
        public async Task A_failed_lookup_is_reported_once_and_records_nothing()
        {
            Seed();
            using var handler = FakeHttpMessageHandler.Json("{}", HttpStatusCode.Unauthorized);
            var failures = new List<string>();
            using var loader = new PosterAutoLoader(Configured(), _dbPath, onFailure: m => { lock (failures) failures.Add(m); },
                handler: handler, requests: new TmdbRequestScheduler(TimeSpan.Zero));

            loader.QueueSeries(1, _ => { }, CancellationToken.None);
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            Assert.Contains("Fargo", Assert.Single(failures), StringComparison.Ordinal);
            Assert.Null(Scalar<long?>("SELECT tmdb_id FROM series WHERE id = 1"));
        }

        // ---------- the schema ----------

        /// <summary>A catalogue made by the build that first filed episodes gains every new column.</summary>
        [Fact]
        public void A_catalogue_from_before_identification_gains_the_columns_it_needs()
        {
            using (var old = Database.Connect(_dbPath))
            {
                old.Execute(@"
CREATE TABLE series (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, year INTEGER, genres TEXT,
                     poster_path TEXT, created_at TEXT);
CREATE TABLE episodes (id INTEGER PRIMARY KEY AUTOINCREMENT, series_id INTEGER NOT NULL, season_number INTEGER,
                       episode_number INTEGER NOT NULL, title TEXT);
INSERT INTO series (id, title, year) VALUES (1, 'Fargo', 2014);");
            }

            using var conn = Database.Open(_dbPath);

            foreach (var column in new[] { "tmdb_id", "overview", "backdrop_path", "rating", "imdb_id", "cast_list", "crew_list", "tmdb_fetched_at" })
                Assert.True(Database.ColumnExists(conn, "series", column), column);
            foreach (var column in new[] { "tmdb_title", "overview", "runtime_minutes", "air_date", "still_path", "tmdb_fetched_at" })
                Assert.True(Database.ColumnExists(conn, "episodes", column), column);

            var stored = LocalSeriesLookup.Read(conn, 1)!;
            Assert.Equal("Fargo", stored.Title);
            Assert.False(stored.Described);
        }
    }
}
