using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class MovieMatchTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _dbPath;

        public MovieMatchTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-match-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _dbPath = Path.Combine(_dir, "movies.db");
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch { /* a locked temp file is not a test failure */ }
        }

        private static long InsertMovie(SqliteConnection conn, string title, int? year, string? poster = null)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO movies (title, year, poster_path) VALUES (@t, @y, @p); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@t", title);
            cmd.Parameters.AddWithValue("@y", (object?)year ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p", (object?)poster ?? DBNull.Value);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        private static string? ReadPoster(SqliteConnection conn, long id)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT poster_path FROM movies WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", id);
            var value = cmd.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToString(value);
        }

        [Fact]
        public async Task A_corrected_match_survives_the_window_that_made_it()
        {
            using var conn = Database.Open(_dbPath);
            var id = InsertMovie(conn, "El Drama", 2026, "https://image.tmdb.org/t/p/w342/wrong.jpg");

            await MovieMatch.SaveAsync(conn, id, 901, "https://image.tmdb.org/t/p/w342/right.jpg");

            using var reopened = Database.Open(_dbPath);
            Assert.Equal(901, MovieMatch.ReadTmdbId(reopened, id));
            Assert.Equal("https://image.tmdb.org/t/p/w342/right.jpg", ReadPoster(reopened, id));
        }

        [Fact]
        public async Task Correcting_a_match_twice_keeps_the_second_answer()
        {
            using var conn = Database.Open(_dbPath);
            var id = InsertMovie(conn, "El Drama", 2026);

            await MovieMatch.SaveAsync(conn, id, 900, "/first.jpg");
            await MovieMatch.SaveAsync(conn, id, 901, "/second.jpg");

            Assert.Equal(901, MovieMatch.ReadTmdbId(conn, id));
            Assert.Equal("/second.jpg", ReadPoster(conn, id));
        }

        [Fact]
        public async Task A_film_tmdb_has_no_artwork_for_keeps_the_poster_it_already_had()
        {
            using var conn = Database.Open(_dbPath);
            var id = InsertMovie(conn, "El Drama", 2026, "/chosen-by-hand.jpg");

            await MovieMatch.SaveAsync(conn, id, 901, posterPath: null);

            Assert.Equal(901, MovieMatch.ReadTmdbId(conn, id));
            Assert.Equal("/chosen-by-hand.jpg", ReadPoster(conn, id));
        }

        [Fact]
        public void A_film_nothing_has_identified_reads_as_null_rather_than_zero()
        {
            using var conn = Database.Open(_dbPath);
            var id = InsertMovie(conn, "El Drama", 2026);

            Assert.Null(MovieMatch.ReadTmdbId(conn, id));
            Assert.Null(MovieMatch.ReadTmdbId(conn, 999999));
        }




        [Fact]
        public async Task The_whole_correction_round_trip_the_bug_report_asked_for()
        {
            // El Drama (2026) is catalogued, and TMDB's search offers El Sabor del Drama first.
            var handler = FakeHttpMessageHandler.Routed(
                ("search/movie", HttpStatusCode.OK, @"{
                    ""results"": [
                        { ""id"": 900, ""title"": ""El Sabor del Drama"", ""release_date"": ""2019-01-01"", ""poster_path"": ""/wrong.jpg"" },
                        { ""id"": 901, ""title"": ""The Drama"", ""original_title"": ""El Drama"", ""release_date"": ""2026-03-02"", ""poster_path"": ""/right.jpg"" }
                    ]
                }"),
                ("movie/901", HttpStatusCode.OK, @"{ ""id"": 901, ""title"": ""The Drama"", ""overview"": ""The right film."", ""runtime"": 96 }"),
                ("movie/900", HttpStatusCode.OK, @"{ ""id"": 900, ""title"": ""El Sabor del Drama"", ""overview"": ""The wrong film."" }"));

            using var tmdb = new TmdbService("key", posterCacheDir: "", imageSize: "w342", downloadPosters: false, handler: handler);
            using var conn = Database.Open(_dbPath);
            var id = InsertMovie(conn, "El Drama", 2026);

            // The automatic match no longer takes the wrong one; it takes the one whose original
            // title agrees, and records which film that is.
            var (autoId, autoPoster) = await tmdb.SearchPosterAsync("El Drama", 2026, CancellationToken.None);
            Assert.Equal(901, autoId);
            await MovieMatch.SaveAsync(conn, id, autoId!.Value, tmdb.BuildImageUrl(autoPoster!));

            // Had it still got it wrong, the picker shows everything TMDB returned and the user
            // chooses; either way the answer is stored against the film.
            var offered = await tmdb.SearchAsync("El Drama", null, CancellationToken.None);
            Assert.Equal(2, offered.Count);

            var chosen = offered[0];
            await MovieMatch.SaveAsync(conn, id, chosen.Id, tmdb.BuildImageUrl(chosen.PosterPath!));

            // Reopening reads the stored id and asks TMDB for that film, so the correction is not
            // re-derived away by another title search.
            using var reopened = Database.Open(_dbPath);
            var stored = MovieMatch.ReadTmdbId(reopened, id);
            Assert.Equal(900, stored);

            var details = await tmdb.GetDetailsByIdAsync(stored!.Value, CancellationToken.None);
            Assert.Equal("The wrong film.", details!.Overview);
            Assert.EndsWith("/wrong.jpg", ReadPoster(reopened, id));
        }
    }
}
