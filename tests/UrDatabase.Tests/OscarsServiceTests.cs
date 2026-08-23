using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The cache is what keeps a library of four hundred films inside a sixty-requests-a-minute
    /// allowance, so it is tested against a real SQLite file rather than a mock.
    /// </summary>
    public class OscarsServiceTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _dbPath;

        public OscarsServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-oscars-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _dbPath = Path.Combine(_dir, "movies.db");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private SqliteConnection Open() => Database.Open(_dbPath);

        /// <summary>
        /// Counts what it was asked, so a test can prove the network was not touched twice.
        /// </summary>
        private sealed class CountingLookup : IOscarsLookup
        {
            private readonly IReadOnlyList<OscarNomination>? _answer;

            public CountingLookup(IReadOnlyList<OscarNomination>? answer, bool available = true)
            {
                _answer = answer;
                IsAvailable = available;
            }

            public bool IsAvailable { get; }

            public int Calls { get; private set; }

            public List<string> Asked { get; } = new();

            public Task<IReadOnlyList<OscarNomination>?> LookupAsync(string title, CancellationToken ct = default)
            {
                Calls++;
                Asked.Add(title);
                return Task.FromResult(_answer);
            }
        }

        private static List<OscarNomination> Awards() => new()
        {
            new OscarNomination { Ceremony = 2026, Category = "Best Picture", Nominee = "F1", Detail = "Brad Pitt" },
            new OscarNomination { Ceremony = 2026, Category = "Best Sound", Nominee = "F1", Detail = "Gareth John", Won = true }
        };

        [Fact]
        public async Task An_answer_is_asked_for_once_and_read_from_the_cache_after()
        {
            var lookup = new CountingLookup(Awards());
            using var svc = new OscarsService(lookup);
            using var conn = Open();

            var first = await svc.GetAsync(conn, "F1", 2025);
            var second = await svc.GetAsync(conn, "F1", 2025);

            Assert.Equal(2, first.Total);
            Assert.Equal(1, first.Wins);
            Assert.Equal(2, second.Total);
            Assert.Equal(1, lookup.Calls);
        }

        /// <summary>
        /// Almost every film in almost every library was never nominated for anything, so this is
        /// the answer it would be most wasteful to ask twice.
        /// </summary>
        [Fact]
        public async Task No_awards_is_remembered_as_firmly_as_awards_are()
        {
            var lookup = new CountingLookup(new List<OscarNomination>());
            using var svc = new OscarsService(lookup);
            using var conn = Open();

            Assert.False((await svc.GetAsync(conn, "Some Film", 1994)).Any);
            Assert.False((await svc.GetAsync(conn, "Some Film", 1994)).Any);

            Assert.Equal(1, lookup.Calls);
        }

        /// <summary>
        /// The bug this return type exists to prevent: one rate-limited afternoon must not record
        /// "no awards" against a hundred films permanently.
        /// </summary>
        [Fact]
        public async Task A_failed_lookup_is_never_written_to_the_cache()
        {
            var lookup = new CountingLookup(null);
            using var svc = new OscarsService(lookup);
            using var conn = Open();

            Assert.False((await svc.GetAsync(conn, "F1", 2025)).Any);
            Assert.False((await svc.GetAsync(conn, "F1", 2025)).Any);

            Assert.Equal(2, lookup.Calls);
        }

        [Fact]
        public async Task No_key_means_no_request_and_nothing_cached()
        {
            var lookup = new CountingLookup(Awards(), available: false);
            using var svc = new OscarsService(lookup);
            using var conn = Open();

            Assert.False(svc.IsConfigured);
            Assert.False((await svc.GetAsync(conn, "F1", 2025)).Any);
            Assert.Equal(0, lookup.Calls);

            // And nothing was recorded, so configuring a key later asks properly rather than
            // trusting an answer that was never obtained.
            Assert.False(OscarsService.TryReadCache(conn, "F1", 2025, out _));
        }

        [Fact]
        public async Task A_film_with_no_title_is_never_asked_about()
        {
            var lookup = new CountingLookup(Awards());
            using var svc = new OscarsService(lookup);
            using var conn = Open();

            Assert.False((await svc.GetAsync(conn, "", 2025)).Any);
            Assert.False((await svc.GetAsync(conn, null, 2025)).Any);
            Assert.Equal(0, lookup.Calls);
        }

        /// <summary>
        /// The whole title search is cached rather than only the nominations attributed to the
        /// film, so a corrected release year re-attributes what is already on disk.
        /// </summary>
        [Fact]
        public async Task Everything_the_archive_returned_is_kept_so_a_different_year_can_re_read_it()
        {
            var lookup = new CountingLookup(new List<OscarNomination>
            {
                new() { Ceremony = 1938, Category = "Best Writing", Nominee = "A Star Is Born", Won = true },
                new() { Ceremony = 2019, Category = "Best Original Song", Nominee = "A Star Is Born", Won = true }
            });

            using var svc = new OscarsService(lookup);
            using var conn = Open();

            var modern = await svc.GetAsync(conn, "A Star Is Born", 2018);

            Assert.Equal(1, modern.Total);
            Assert.Equal(2019, modern.Ceremony);

            // Both rows survived the write, so the 1937 film reads its own award back out.
            Assert.True(OscarsService.TryReadCache(conn, "A Star Is Born", 2018, out var stored));
            Assert.Equal(2, stored.Count);
        }

        [Fact]
        public async Task A_film_whose_year_is_unknown_gets_its_own_cache_row()
        {
            var lookup = new CountingLookup(Awards());
            using var svc = new OscarsService(lookup);
            using var conn = Open();

            await svc.GetAsync(conn, "F1", null);

            // Zero rather than NULL: SQLite treats every NULL in a primary key as distinct, so a
            // NULL year would give every unknown-year film a row nothing could ever find again.
            Assert.True(OscarsService.TryReadCache(conn, "F1", null, out _));
            Assert.Equal(1, lookup.Calls);

            await svc.GetAsync(conn, "F1", null);
            Assert.Equal(1, lookup.Calls);
        }

        [Fact]
        public async Task Re_asking_replaces_the_stored_answer_rather_than_adding_to_it()
        {
            using var conn = Open();

            OscarsService.WriteCache(conn, "F1", 2025, Awards());
            OscarsService.WriteCache(conn, "F1", 2025, new List<OscarNomination>
            {
                new() { Ceremony = 2026, Category = "Best Sound", Nominee = "F1", Won = true }
            });

            Assert.True(OscarsService.TryReadCache(conn, "F1", 2025, out var stored));
            Assert.Single(stored);

            await Task.CompletedTask;
        }

        [Fact]
        public async Task Everything_a_row_carries_survives_the_round_trip()
        {
            var lookup = new CountingLookup(Awards());
            using var svc = new OscarsService(lookup);
            using var conn = Open();

            await svc.GetAsync(conn, "F1", 2025);
            var reread = await svc.GetAsync(conn, "F1", 2025);

            var win = Assert.Single(reread.Nominations, n => n.Won);
            Assert.Equal(2026, win.Ceremony);
            Assert.Equal("Best Sound", win.Category);
            Assert.Equal("F1", win.Nominee);
            Assert.Equal("Gareth John", win.Detail);
        }
    }
}
