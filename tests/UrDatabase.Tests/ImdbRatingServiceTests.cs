using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class ImdbRatingServiceTests : IDisposable
    {
        private readonly string _dir;
        private readonly SqliteConnection _conn;

        public ImdbRatingServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-rating-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _conn = Database.Open(Path.Combine(_dir, "movies.db"));
            SeedMovies();
        }

        /// <summary>
        /// The cache row references movies(id), and the app only ever caches a rating for a movie
        /// it just read from the catalogue, so the tests mirror that.
        /// </summary>
        private void SeedMovies()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO movies (id, title, year) VALUES (1, 'Fight Club', 1999);
INSERT INTO movies (id, title, year) VALUES (2, 'The Shawshank Redemption', 1994);
INSERT INTO movies (id, title, year) VALUES (42, 'Fight Club', 1999);";
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            _conn.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        /// <summary>Counts calls so cache behaviour can be asserted without any network.</summary>
        private sealed class CountingLookup : IImdbRatingLookup
        {
            private readonly double? _rating;

            public CountingLookup(double? rating, bool available = true)
            {
                _rating = rating;
                IsAvailable = available;
            }

            public bool IsAvailable { get; }
            public int Calls { get; private set; }

            public Task<double?> LookupAsync(string imdbId, CancellationToken ct = default)
            {
                Calls++;
                return Task.FromResult(_rating);
            }
        }

        [Fact]
        public async Task Fetches_and_returns_a_rating()
        {
            var lookup = new CountingLookup(7.3);
            using var svc = new ImdbRatingService(lookup);

            var rating = await svc.GetRatingAsync(_conn, "tt0137523", movieId: 1);

            Assert.Equal(7.3, rating);
            Assert.Equal(1, lookup.Calls);
        }

        [Fact]
        public async Task A_cached_rating_causes_no_second_request()
        {
            var lookup = new CountingLookup(7.3);
            using var svc = new ImdbRatingService(lookup);

            var first = await svc.GetRatingAsync(_conn, "tt0137523", movieId: 1);
            var second = await svc.GetRatingAsync(_conn, "tt0137523", movieId: 1);

            Assert.Equal(7.3, first);
            Assert.Equal(7.3, second);
            Assert.Equal(1, lookup.Calls);
        }

        [Fact]
        public async Task A_cached_absence_of_a_rating_also_causes_no_second_request()
        {
            // The daily quota is what makes this matter: "no rating" must be remembered too.
            var lookup = new CountingLookup(null);
            using var svc = new ImdbRatingService(lookup);

            Assert.Null(await svc.GetRatingAsync(_conn, "tt0137523", movieId: 1));
            Assert.Null(await svc.GetRatingAsync(_conn, "tt0137523", movieId: 1));

            Assert.Equal(1, lookup.Calls);
        }

        [Fact]
        public async Task A_cached_rating_is_served_even_when_the_lookup_is_unavailable()
        {
            var seeding = new CountingLookup(7.3);
            using (var svc = new ImdbRatingService(seeding))
                await svc.GetRatingAsync(_conn, "tt0137523", movieId: 1);

            var offline = new CountingLookup(null, available: false);
            using var offlineSvc = new ImdbRatingService(offline);

            Assert.Equal(7.3, await offlineSvc.GetRatingAsync(_conn, "tt0137523", movieId: 1));
            Assert.Equal(0, offline.Calls);
        }

        [Fact]
        public async Task A_movie_without_an_imdb_id_skips_the_lookup_entirely()
        {
            var lookup = new CountingLookup(7.3);
            using var svc = new ImdbRatingService(lookup);

            Assert.Null(await svc.GetRatingAsync(_conn, null, movieId: 1));
            Assert.Null(await svc.GetRatingAsync(_conn, "   ", movieId: 1));

            Assert.Equal(0, lookup.Calls);
        }

        [Fact]
        public async Task An_unavailable_lookup_returns_no_rating_without_calling_out()
        {
            var lookup = new CountingLookup(7.3, available: false);
            using var svc = new ImdbRatingService(lookup);

            Assert.Null(await svc.GetRatingAsync(_conn, "tt0137523", movieId: 1));
            Assert.Equal(0, lookup.Calls);
            Assert.False(svc.IsConfigured);
        }

        [Fact]
        public async Task The_cached_row_records_the_movie_and_when_it_was_fetched()
        {
            using var svc = new ImdbRatingService(new CountingLookup(7.3));
            await svc.GetRatingAsync(_conn, "tt0137523", movieId: 42);

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT movie_id, rating, fetched_at, source FROM imdb_ratings WHERE imdb_id = 'tt0137523'";
            using var reader = cmd.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal(42L, reader.GetInt64(0));
            Assert.Equal(7.3, reader.GetDouble(1));
            Assert.False(string.IsNullOrWhiteSpace(reader.GetString(2)));
            Assert.Equal("omdb", reader.GetString(3));
        }

        [Fact]
        public async Task Different_movies_are_cached_separately()
        {
            var lookup = new CountingLookup(7.3);
            using var svc = new ImdbRatingService(lookup);

            await svc.GetRatingAsync(_conn, "tt0137523", movieId: 1);
            await svc.GetRatingAsync(_conn, "tt0111161", movieId: 2);

            Assert.Equal(2, lookup.Calls);
        }
    }
}
