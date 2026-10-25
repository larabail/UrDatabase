using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// Wrong series?: a person saying which TMDB programme a series on this disk is, and the
    /// catalogue believing them — over the automatic match, over the next scan, and over a
    /// background lookup that was already in flight.
    ///
    /// Every catalogue is a temporary file this class creates and deletes, every poster cache a
    /// temporary folder beside it, and every network answer a <see cref="FakeHttpMessageHandler"/>.
    /// </summary>
    public class SeriesMatchCorrectionTests : IDisposable
    {
        private readonly string _root;
        private readonly string _dbPath;
        private readonly string _posters;
        private readonly string _media;
        private readonly TempLog _log = new();

        private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x02, 0x03 };

        private const string OfficeDetails = @"{
            ""id"": 2316, ""name"": ""The Office"", ""overview"": ""A mockumentary about office workers."",
            ""poster_path"": ""/office-us.jpg"", ""backdrop_path"": ""/office-backdrop.jpg"", ""vote_average"": 8.6,
            ""genres"": [ { ""id"": 35, ""name"": ""Comedy"" } ],
            ""created_by"": [ { ""name"": ""Greg Daniels"" } ],
            ""credits"": { ""cast"": [ { ""name"": ""Steve Carell"", ""character"": ""Michael Scott"" } ], ""crew"": [] },
            ""external_ids"": { ""imdb_id"": ""tt0386676"" } }";

        public SeriesMatchCorrectionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-tvfix-" + Guid.NewGuid().ToString("N"));
            _posters = Path.Combine(_root, "posters");
            _media = Path.Combine(_root, "media");
            Directory.CreateDirectory(_posters);
            Directory.CreateDirectory(_media);
            _dbPath = Path.Combine(_root, "movies.db");
        }

        public void Dispose()
        {
            _log.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---------- fixtures ----------

        /// <summary>A programme with two episodes on disk, as the scan left it: no identification.</summary>
        private void Seed(string title = "The Office", int? year = null)
        {
            Assert.StartsWith(Path.GetTempPath(), _dbPath, StringComparison.Ordinal);

            using var conn = Database.Open(_dbPath);
            conn.Execute(@"
INSERT INTO series (id, title, year) VALUES (1, @title, @year);
INSERT INTO episodes (id, series_id, season_number, episode_number) VALUES (1, 1, 1, 1);
INSERT INTO episodes (id, series_id, season_number, episode_number) VALUES (2, 1, 1, 2);
INSERT INTO files (episode_id, file_path) VALUES (1, '/tv/The.Office.S01E01.mkv');
INSERT INTO files (episode_id, file_path) VALUES (2, '/tv/The.Office.S01E02.mkv');", new { title, year });
        }

        /// <summary>
        /// The same programme after the background pass took it for the wrong one: identified as
        /// tmdb 1, described as it, with its episodes named and marked fetched.
        /// </summary>
        private void SeedWronglyIdentified()
        {
            Seed();
            using var conn = Database.Open(_dbPath);
            conn.Execute(@"
UPDATE series SET tmdb_id = 1, genres = 'Documentary', poster_path = 'https://image.tmdb.org/t/p/w342/wrong.jpg',
       overview = 'The wrong programme.', imdb_id = 'tt0000001', cast_list = 'Somebody Else',
       tmdb_fetched_at = '2026-01-01T00:00:00Z'
WHERE id = 1;
UPDATE episodes SET tmdb_title = 'Wrong episode', overview = 'Wrong plot', runtime_minutes = 30,
       air_date = '1999-01-01', still_path = '/wrong.jpg', tmdb_fetched_at = '2026-01-01T00:00:00Z';");
        }

        private static FakeHttpMessageHandler Tmdb(string details = OfficeDetails, HttpStatusCode status = HttpStatusCode.OK) =>
            FakeHttpMessageHandler.Routed(
                ("/tv/2316/season/1", HttpStatusCode.OK, @"{ ""season_number"": 1, ""episodes"": [
                    { ""season_number"": 1, ""episode_number"": 1, ""name"": ""Pilot"", ""runtime"": 23 },
                    { ""season_number"": 1, ""episode_number"": 2, ""name"": ""Diversity Day"" } ] }"),
                ("/tv/2316", status, details),
                ("/tv/60622", HttpStatusCode.OK, TmdbTelevisionTests.FargoDetails),
                ("/search/tv", HttpStatusCode.OK, TmdbTelevisionTests.FargoSearch));

        private TmdbService Service(HttpMessageHandler handler) =>
            new(apiKey: "not-a-real-key", posterCacheDir: _posters, imageSize: "w342", downloadPosters: false,
                handler: handler, requests: new TmdbRequestScheduler(TimeSpan.Zero));

        private static SeriesChoice TheOffice => new(2316, "The Office", "/office-us.jpg");

        private static int Count(FakeHttpMessageHandler handler, string fragment) =>
            handler.Requests.Count(r => r.Contains(fragment, StringComparison.Ordinal));

        // ---------- choosing a programme ----------

        /// <summary>
        /// The case the background pass leaves for a person: two programmes called The Office and
        /// no year to choose between them. Choosing one identifies and describes it.
        /// </summary>
        [Fact]
        public async Task Choosing_a_refused_programme_identifies_and_describes_it()
        {
            Seed();
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            var result = await SeriesMatchCorrection.ApplyAsync(tmdb, conn, 1, TheOffice, downloadPosters: false, CancellationToken.None);

            Assert.True(result.Described);
            Assert.Null(result.RenamedTo);

            var stored = result.Details;
            Assert.Equal(2316, stored.TmdbId);
            Assert.True(stored.Described);
            Assert.Equal("The Office", stored.Title);
            Assert.Equal("Comedy", stored.Genres);
            Assert.Equal("https://image.tmdb.org/t/p/w342/office-us.jpg", stored.PosterPath);
            Assert.Equal("A mockumentary about office workers.", stored.Overview);
            Assert.Equal("tt0386676", stored.ImdbId);
            Assert.Equal(new[] { "Steve Carell (Michael Scott)" }, stored.Cast);
            Assert.Equal(new[] { "Creator: Greg Daniels" }, stored.Crew);

            // By id: the choice is never re-derived from a search.
            Assert.Equal(0, Count(handler, "/search/"));
            Assert.Equal(1, Count(handler, "/tv/2316"));

            // A name that did not change is not recorded as a discarded one.
            Assert.Null(conn.ExecuteScalar<string?>("SELECT scan_title FROM series WHERE id = 1"));
        }

        /// <summary>
        /// The episodes' TMDB names belong to the programme they were fetched for. Writing the new
        /// id alone would leave them marked fetched, and no season would ever be asked about again.
        /// </summary>
        [Fact]
        public async Task Changing_the_programme_clears_every_episode_described_as_the_old_one()
        {
            SeedWronglyIdentified();
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            await SeriesMatchCorrection.ApplyAsync(tmdb, conn, 1, TheOffice, downloadPosters: false, CancellationToken.None);

            Assert.Equal(2316L, conn.ExecuteScalar<long>("SELECT tmdb_id FROM series WHERE id = 1"));
            Assert.Equal(0L, conn.ExecuteScalar<long>(@"
SELECT COUNT(*) FROM episodes
WHERE tmdb_title IS NOT NULL OR overview IS NOT NULL OR runtime_minutes IS NOT NULL OR air_date IS NOT NULL
   OR still_path IS NOT NULL OR tmdb_fetched_at IS NOT NULL"));

            // And so the next open asks for the corrected programme's season, and names its episodes.
            var asked = await LocalSeriesLookup.RefreshSeasonsAsync(tmdb, conn, 1, force: false, CancellationToken.None);
            Assert.Equal(1, asked);
            Assert.Equal(1, Count(handler, "/tv/2316/season/1"));
            Assert.Equal("Pilot", conn.ExecuteScalar<string>("SELECT tmdb_title FROM episodes WHERE id = 1"));
            Assert.Equal("Diversity Day", conn.ExecuteScalar<string>("SELECT tmdb_title FROM episodes WHERE id = 2"));
        }

        /// <summary>
        /// Choosing the programme it is already identified as is still asking for it afresh: the
        /// episode names are dropped and asked for again, not kept on trust.
        /// </summary>
        [Fact]
        public async Task Choosing_the_same_programme_again_still_asks_for_its_episodes_afresh()
        {
            SeedWronglyIdentified();
            using (var seed = Database.Open(_dbPath))
                seed.Execute("UPDATE series SET tmdb_id = 2316 WHERE id = 1");

            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            await SeriesMatchCorrection.ApplyAsync(tmdb, conn, 1, TheOffice, downloadPosters: false, CancellationToken.None);

            Assert.Equal(0L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM episodes WHERE tmdb_fetched_at IS NOT NULL"));
        }

        /// <summary>
        /// The automatic pass only fills genres and a poster in. A correction exists because the
        /// ones already there are wrong, so it replaces both.
        /// </summary>
        [Fact]
        public async Task A_correction_replaces_the_wrong_programmes_genres_poster_and_description()
        {
            SeedWronglyIdentified();
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            var stored = (await SeriesMatchCorrection.ApplyAsync(tmdb, conn, 1, TheOffice, downloadPosters: false, CancellationToken.None)).Details;

            Assert.Equal("Comedy", stored.Genres);
            Assert.Equal("https://image.tmdb.org/t/p/w342/office-us.jpg", stored.PosterPath);
            Assert.Equal("tt0386676", stored.ImdbId);
            Assert.Equal(new[] { "Steve Carell (Michael Scott)" }, stored.Cast);
        }

        /// <summary>
        /// A download returns a file already on disk untouched, and the old programme's poster is
        /// sitting at <c>series-1.jpg</c>. The chosen one has to go somewhere else or the wrong
        /// artwork comes straight back.
        /// </summary>
        [Fact]
        public async Task A_downloaded_poster_is_cached_under_a_name_the_old_poster_cannot_occupy()
        {
            SeedWronglyIdentified();
            var old = Path.Combine(_posters, LocalSeriesLookup.PosterFileName(1));
            await File.WriteAllBytesAsync(old, Jpeg);
            using (var seed = Database.Open(_dbPath))
                seed.Execute("UPDATE series SET poster_path = @old WHERE id = 1", new { old });

            var images = 0;
            var json = Tmdb();
            using var handler = new FakeHttpMessageHandler(request =>
            {
                if (request.RequestUri!.Host == "image.tmdb.org")
                {
                    images++;
                    var content = new ByteArrayContent(Jpeg);
                    content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                }

                return json.Respond(request);
            });
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            var stored = (await SeriesMatchCorrection.ApplyAsync(tmdb, conn, 1, TheOffice, downloadPosters: true, CancellationToken.None)).Details;

            var expected = Path.Combine(_posters, SeriesMatchCorrection.PosterFileName(1, 2316));
            Assert.Equal(expected, stored.PosterPath);
            Assert.True(File.Exists(expected));
            Assert.Equal(1, images);
            Assert.NotEqual(old, stored.PosterPath);
        }

        [Fact]
        public async Task A_chosen_programme_with_no_poster_does_not_keep_the_wrong_ones()
        {
            SeedWronglyIdentified();
            using var handler = Tmdb(OfficeDetails.Replace(@"""poster_path"": ""/office-us.jpg"",", ""));
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            var stored = (await SeriesMatchCorrection.ApplyAsync(
                tmdb, conn, 1, TheOffice with { PosterPath = null }, downloadPosters: false, CancellationToken.None)).Details;

            Assert.Null(stored.PosterPath);
        }

        /// <summary>
        /// TMDB's search and its record are not always in step: a result listed with no poster may
        /// have one in its details, and that one is used.
        /// </summary>
        [Fact]
        public async Task A_poster_missing_from_the_search_result_is_taken_from_the_record()
        {
            Seed();
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            var stored = (await SeriesMatchCorrection.ApplyAsync(
                tmdb, conn, 1, TheOffice with { PosterPath = null }, downloadPosters: false, CancellationToken.None)).Details;

            Assert.Equal("https://image.tmdb.org/t/p/w342/office-us.jpg", stored.PosterPath);
        }

        /// <summary>
        /// The answer is saved before the description is asked for. A request that fails leaves
        /// the person's choice in place, and the background pass describes it by id — no search —
        /// the next time it runs.
        /// </summary>
        [Fact]
        public async Task A_description_that_fails_keeps_the_choice_for_the_background_pass_to_describe()
        {
            SeedWronglyIdentified();
            using (var failing = Tmdb(status: HttpStatusCode.Unauthorized))
            using (var tmdb = Service(failing))
            using (var conn = Database.Open(_dbPath))
            {
                var result = await SeriesMatchCorrection.ApplyAsync(tmdb, conn, 1, TheOffice, downloadPosters: false, CancellationToken.None);

                Assert.False(result.Described);
                Assert.Equal(2316, result.Details.TmdbId);
                Assert.False(result.Details.Described);
                Assert.Equal("https://image.tmdb.org/t/p/w342/office-us.jpg", result.Details.PosterPath);

                // Nothing of the wrong programme is left to describe this one in the meantime.
                Assert.Null(result.Details.Genres);
                Assert.Null(result.Details.Overview);
                Assert.Null(result.Details.ImdbId);
                Assert.Empty(result.Details.Cast);
                Assert.False(LocalSeriesLookup.IsComplete(result.Details));
            }

            using var handler = Tmdb();
            using var later = Service(handler);
            using var db = Database.Open(_dbPath);

            var identified = await LocalSeriesLookup.IdentifyAsync(later, db, 1, downloadPosters: false, CancellationToken.None);

            Assert.True(identified.Matched);
            Assert.Equal(0, Count(handler, "/search/"));
            var stored = LocalSeriesLookup.Read(db, 1)!;
            Assert.Equal(2316, stored.TmdbId);
            Assert.True(stored.Described);
            Assert.Equal("Comedy", stored.Genres);
        }

        // ---------- the name ----------

        [Fact]
        public async Task The_programme_takes_TMDBs_name_and_keeps_the_scans_for_the_next_scan()
        {
            Seed(title: "Office US");
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            var result = await SeriesMatchCorrection.ApplyAsync(tmdb, conn, 1, TheOffice, downloadPosters: false, CancellationToken.None);

            Assert.Equal("The Office", result.RenamedTo);
            Assert.Equal("The Office", result.Details.Title);
            Assert.Equal("Office US", conn.ExecuteScalar<string>("SELECT scan_title FROM series WHERE id = 1"));

            // A second correction keeps the name the scan parsed, not the first correction's.
            await SeriesMatchCorrection.ApplyAsync(
                tmdb, conn, 1, new SeriesChoice(60622, "Fargo", "/fargo.jpg"), downloadPosters: false, CancellationToken.None);

            Assert.Equal("Fargo", conn.ExecuteScalar<string>("SELECT title FROM series WHERE id = 1"));
            Assert.Equal("Office US", conn.ExecuteScalar<string>("SELECT scan_title FROM series WHERE id = 1"));
        }

        [Fact]
        public async Task A_result_with_no_name_does_not_rename_the_programme()
        {
            Seed(title: "Office US");
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            var result = await SeriesMatchCorrection.ApplyAsync(
                tmdb, conn, 1, TheOffice with { Name = null }, downloadPosters: false, CancellationToken.None);

            Assert.Null(result.RenamedTo);
            Assert.Equal("Office US", result.Details.Title);
            Assert.Null(conn.ExecuteScalar<string?>("SELECT scan_title FROM series WHERE id = 1"));
        }

        // ---------- the next scan ----------

        /// <summary>
        /// The files still say what they always said. Without the scan's name to resolve them by,
        /// the renamed programme would fork into a second, unidentified one on the next scan — and
        /// the scan must leave the correction itself exactly as the person made it.
        /// </summary>
        [Fact]
        public async Task A_rescan_after_a_correction_creates_no_duplicate_and_undoes_nothing()
        {
            Write("TV", "Fargo.S01E01.mkv");
            await ScanAsync();

            using (var handler = Tmdb())
            using (var tmdb = Service(handler))
            using (var conn = Database.Open(_dbPath))
            {
                var id = conn.ExecuteScalar<long>("SELECT id FROM series");
                await SeriesMatchCorrection.ApplyAsync(
                    tmdb, conn, id, new SeriesChoice(60622, "Fargo (Anthology)", "/fargo.jpg"), downloadPosters: false, CancellationToken.None);
            }

            Write("TV", "Fargo.S01E02.mkv");
            await ScanAsync();

            using var db = Database.Open(_dbPath);
            Assert.Equal(1L, db.ExecuteScalar<long>("SELECT COUNT(*) FROM series"));
            Assert.Equal(2L, db.ExecuteScalar<long>("SELECT COUNT(*) FROM episodes WHERE series_id = (SELECT id FROM series)"));

            var stored = LocalSeriesLookup.Read(db, db.ExecuteScalar<long>("SELECT id FROM series"))!;
            Assert.Equal("Fargo (Anthology)", stored.Title);
            Assert.Equal(60622, stored.TmdbId);
            Assert.True(stored.Described);
            Assert.Equal("https://image.tmdb.org/t/p/w342/fargo.jpg", stored.PosterPath);
            Assert.Equal("Crime, Science Fiction, Fantasy", stored.Genres);
            Assert.Equal("Fargo", db.ExecuteScalar<string>("SELECT scan_title FROM series"));
        }

        /// <summary>
        /// A renamed programme must not take another programme's scan name away from it, whichever
        /// was catalogued first: scan names are all registered before any chosen name.
        /// </summary>
        [Fact]
        public async Task A_chosen_name_never_captures_another_programmes_files()
        {
            using (var conn = Database.Open(_dbPath))
                conn.Execute(@"
INSERT INTO series (id, title, scan_title) VALUES (1, 'The Office', 'Office US');
INSERT INTO series (id, title) VALUES (2, 'The Office');");

            using var db = Database.Open(_dbPath);
            var catalogue = await EpisodeCatalogue.LoadAsync(db);

            Assert.True(catalogue.TryResolveSeries(new ParsedEpisode("The Office", null, 1, 1, ""), out var office, out _));
            Assert.Equal(2, office);
            Assert.True(catalogue.TryResolveSeries(new ParsedEpisode("Office US", null, 1, 1, ""), out var renamed, out _));
            Assert.Equal(1, renamed);
            Assert.Equal(2, catalogue.SeriesCount);
        }

        [Fact]
        public async Task A_folder_renamed_to_the_chosen_name_still_lands_on_the_same_programme()
        {
            using (var conn = Database.Open(_dbPath))
                conn.Execute("INSERT INTO series (id, title, scan_title) VALUES (1, 'The Office', 'Office US');");

            using var db = Database.Open(_dbPath);
            var catalogue = await EpisodeCatalogue.LoadAsync(db);

            Assert.True(catalogue.TryResolveSeries(new ParsedEpisode("The Office", null, 1, 1, ""), out var id, out _));
            Assert.Equal(1, id);
        }

        // ---------- the background pass ----------

        /// <summary>
        /// The pass reads the row, then waits on TMDB. If somebody chooses the programme by hand in
        /// that gap, the pass's answer is about the programme it read and must not overwrite theirs.
        /// </summary>
        [Fact]
        public async Task A_background_lookup_in_flight_does_not_overwrite_a_choice_made_meanwhile()
        {
            Seed(title: "Fargo", year: 2014);

            var json = FakeHttpMessageHandler.Routed(
                ("/search/tv", HttpStatusCode.OK, TmdbTelevisionTests.FargoSearch),
                ("/tv/60622", HttpStatusCode.OK, TmdbTelevisionTests.FargoDetails));
            using var handler = new FakeHttpMessageHandler(request =>
            {
                // The person chooses while the pass is waiting for the description.
                if (request.RequestUri!.AbsolutePath.StartsWith("/3/tv/", StringComparison.Ordinal))
                {
                    using var other = Database.Open(_dbPath);
                    SeriesMatchCorrection.RecordAsync(other, 1, 2316, "https://image.tmdb.org/t/p/w342/office-us.jpg", null)
                        .GetAwaiter().GetResult();
                }

                return json.Respond(request);
            });
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            var result = await LocalSeriesLookup.IdentifyAsync(tmdb, conn, 1, downloadPosters: false, CancellationToken.None);

            Assert.False(result.Matched);
            var stored = LocalSeriesLookup.Read(conn, 1)!;
            Assert.Equal(2316, stored.TmdbId);
            Assert.Null(stored.Overview);
            Assert.Null(stored.Genres);
            Assert.Equal("https://image.tmdb.org/t/p/w342/office-us.jpg", stored.PosterPath);
        }

        [Fact]
        public async Task A_guarded_description_of_another_programme_writes_nothing()
        {
            SeedWronglyIdentified();
            using var conn = Database.Open(_dbPath);

            var written = await LocalSeriesLookup.SaveDescriptionAsync(conn, 1, new SeriesDescription(
                    99, null, "Drama", "Another programme.", null, null, null, Array.Empty<string>(), Array.Empty<string>()),
                keepOtherIdentification: true);

            Assert.False(written);
            Assert.Equal(1L, conn.ExecuteScalar<long>("SELECT tmdb_id FROM series WHERE id = 1"));
            Assert.Equal("The wrong programme.", conn.ExecuteScalar<string>("SELECT overview FROM series WHERE id = 1"));
            Assert.Equal(2L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM episodes WHERE tmdb_title IS NOT NULL"));
        }

        // ---------- the loader ----------

        [Fact]
        public async Task Without_a_key_the_loader_corrects_nothing()
        {
            Seed();

            Assert.Null(await new LocalSeriesLoader(_dbPath).CorrectAsync(1, TheOffice, false, CancellationToken.None));
            Assert.Null(await new LocalSeriesLoader(_dbPath, () => null).CorrectAsync(1, TheOffice, false, CancellationToken.None));

            using var conn = Database.Open(_dbPath);
            Assert.Null(conn.ExecuteScalar<long?>("SELECT tmdb_id FROM series WHERE id = 1"));
        }

        [Fact]
        public async Task The_loader_corrects_through_the_client_it_is_given()
        {
            Seed();
            using var handler = Tmdb();
            var loader = new LocalSeriesLoader(_dbPath, () => Service(handler));

            var result = await loader.CorrectAsync(1, TheOffice, false, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(2316, result!.Details.TmdbId);
        }

        // ---------- telling the library ----------

        /// <summary>
        /// The identification is committed before the description is asked for, so leaving the
        /// screen mid-fetch cancels a correction that is already in the catalogue. Whoever has to
        /// refresh the library must hear about it then, not only when the whole thing succeeds.
        /// </summary>
        [Fact]
        public async Task A_correction_cancelled_after_it_was_recorded_still_reports_that_it_was()
        {
            Seed(title: "Office US");
            using var cts = new CancellationTokenSource();
            var json = Tmdb();
            using var handler = new FakeHttpMessageHandler(request =>
            {
                // The person presses Back while TMDB is describing the programme.
                if (request.RequestUri!.AbsolutePath.StartsWith("/3/tv/2316", StringComparison.Ordinal))
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }

                return json.Respond(request);
            });
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            var recorded = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SeriesMatchCorrection.ApplyAsync(
                tmdb, conn, 1, TheOffice, downloadPosters: false, cts.Token, onRecorded: () => recorded++));

            Assert.Equal(1, recorded);
            Assert.Equal(2316L, conn.ExecuteScalar<long>("SELECT tmdb_id FROM series WHERE id = 1"));
            Assert.Equal("The Office", conn.ExecuteScalar<string>("SELECT title FROM series WHERE id = 1"));
        }

        [Fact]
        public async Task A_correction_reports_that_it_was_recorded_once_and_before_it_returns()
        {
            Seed();
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            var recorded = 0;
            var describedWhenTold = true;
            await SeriesMatchCorrection.ApplyAsync(tmdb, conn, 1, TheOffice, downloadPosters: false, CancellationToken.None,
                onRecorded: () =>
                {
                    recorded++;
                    describedWhenTold = LocalSeriesLookup.Read(conn, 1)!.Described;
                });

            Assert.Equal(1, recorded);
            Assert.False(describedWhenTold);
        }

        [Fact]
        public async Task A_correction_that_fails_before_anything_is_recorded_reports_nothing()
        {
            using var handler = Tmdb();
            using var tmdb = Service(handler);
            using var conn = Database.Open(_dbPath);

            var recorded = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => SeriesMatchCorrection.ApplyAsync(
                tmdb, conn, 1, TheOffice, downloadPosters: false, CancellationToken.None, onRecorded: () => recorded++));

            Assert.Equal(0, recorded);
        }

        [Fact]
        public async Task The_loader_passes_the_recorded_signal_through()
        {
            Seed();
            using var handler = Tmdb();
            var loader = new LocalSeriesLoader(_dbPath, () => Service(handler));

            var recorded = 0;
            await loader.CorrectAsync(1, TheOffice, false, CancellationToken.None, onRecorded: () => recorded++);

            Assert.Equal(1, recorded);
        }

        // ---------- the schema ----------

        [Fact]
        public void A_catalogue_from_before_corrections_gains_the_scan_title_column()
        {
            using (var old = Database.Connect(_dbPath))
            {
                old.Execute(@"
CREATE TABLE series (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, year INTEGER, genres TEXT,
                     poster_path TEXT, created_at TEXT);
INSERT INTO series (id, title) VALUES (1, 'Fargo');");
            }

            using var conn = Database.Open(_dbPath);

            Assert.True(Database.ColumnExists(conn, "series", "scan_title"));
            Assert.Null(conn.ExecuteScalar<string?>("SELECT scan_title FROM series WHERE id = 1"));
        }

        private async Task ScanAsync()
        {
            using var conn = Database.Open(_dbPath);
            await new ScanService().ScanAsync(conn, new[] { _media });
        }

        private void Write(params string[] parts)
        {
            var path = Path.Combine(new[] { _media }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "video " + path);
        }
    }
}
