using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Programmes on this disk as the library shows them: one card each, kept apart from the films
    /// that share their ids and their names, and opened as a list of seasons.
    /// </summary>
    public class LocalSeriesLibraryTests : IDisposable
    {
        private readonly string _root;
        private readonly string _dbPath;
        private readonly TempLog _log = new();

        public LocalSeriesLibraryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-localtv-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _dbPath = Path.Combine(_root, "movies.db");
        }

        public void Dispose()
        {
            _log.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void A_programme_is_one_series_card_counted_from_its_episodes()
        {
            Seed();

            var all = new MovieRepository(_dbPath).Query(null);
            var show = all.Single(m => m.IsSeries && m.Title == "Fargo");

            Assert.Equal(MovieSource.Local, show.Source);
            Assert.Equal(2014, show.Year);
            Assert.Equal(2, show.SeasonCount);
            Assert.Equal(3, show.EpisodeCount);
            Assert.True(show.IsOnThisComputer);
            Assert.Equal("2014 · 2 seasons", show.MetaLine);
        }

        [Fact]
        public void A_series_and_a_film_with_the_same_id_are_two_cards()
        {
            Seed();

            var all = new MovieRepository(_dbPath).Query(null);
            var film = all.Single(m => m.IsFilm && m.Id == 1);
            var show = all.Single(m => m.IsSeries && m.Id == 1);

            Assert.Equal("local:1", film.Key);
            Assert.Equal("local:series:1", show.Key);
        }

        [Fact]
        public void A_search_finds_a_programme_by_name()
        {
            Seed();

            var view = new LibraryLoader(new MovieRepository(_dbPath)).Load("fargo");

            Assert.Contains(view.All, m => m.IsSeries && m.Title == "Fargo");
            Assert.Contains(view.All, m => m.IsFilm && m.Title == "Fargo");
        }

        [Fact]
        public void A_programme_whose_every_episode_is_gone_leaves_the_library()
        {
            Seed();

            var view = new LibraryLoader(new MovieRepository(_dbPath)).Load(null);

            Assert.DoesNotContain(view.All, m => m.Title == "Gone Show");
        }

        [Fact]
        public void The_status_line_counts_programmes_apart_from_films()
        {
            Seed();

            var view = new LibraryLoader(new MovieRepository(_dbPath)).Load(null);

            Assert.Equal(1, view.Tally!.LocalCount);
            Assert.Equal(1, view.Tally.LocalSeriesCount);
            Assert.Equal("Posters present: 0/1 · 1 series on this computer", view.Status);
        }

        [Fact]
        public void A_server_film_never_folds_onto_a_local_programme()
        {
            var local = new[]
            {
                new UiMovie { Id = 7, Title = "Fargo", Year = 2014, Kind = MediaKind.Series, HasFileHere = true }
            };
            var server = new[]
            {
                JellyfinLibrary.ToUiMovie(new JellyfinMovie { ItemId = "abc", Title = "Fargo", Year = 2014 })
            };

            var merged = JellyfinLibrary.Merge(local, server);

            Assert.Equal(2, merged.Count);
            Assert.False(merged.Single(m => m.IsSeries).IsOnServer);
        }

        [Fact]
        public void A_local_programme_cannot_go_on_the_watchlist_yet()
        {
            var show = new UiMovie { Id = 3, Title = "Fargo", Kind = MediaKind.Series };

            Assert.False(Watchlist.CanHold(show));
        }

        [Fact]
        public void The_television_filter_counts_local_programmes()
        {
            Seed();

            var all = new LibraryLoader(new MovieRepository(_dbPath)).Load(null).All;

            Assert.Equal(1, LibraryFilter.Count(all, LibraryKind.Television));
            Assert.Equal(1, LibraryFilter.Count(all, LibraryKind.Films));
        }

        [Fact]
        public void Local_episodes_are_grouped_by_season_with_specials_last()
        {
            var groups = SeriesGrouping.GroupLocal(new[]
            {
                new LocalEpisode { Id = 1, SeasonNumber = 2, EpisodeNumber = 1, FilePath = "/tv/s2e1.mkv" },
                new LocalEpisode { Id = 2, SeasonNumber = 0, EpisodeNumber = 1, FilePath = "/tv/special.mkv" },
                new LocalEpisode { Id = 3, SeasonNumber = 1, EpisodeNumber = 2, Title = "Two", FilePath = "/tv/s1e2.mkv" },
                new LocalEpisode { Id = 4, SeasonNumber = 1, EpisodeNumber = 1, FilePath = "/tv/s1e1.mkv" },
            });

            Assert.Equal(new[] { "Season 1", "Season 2", "Specials" }, groups.Select(g => g.Name));
            Assert.Equal(new[] { "S01E01", "S01E02" }, groups[0].Episodes.Select(e => e.Label));
            Assert.Equal(new[] { "Episode 1", "Two" }, groups[0].Episodes.Select(e => e.Title));
            Assert.Equal("/tv/s1e1.mkv", groups[0].Episodes[0].FilePath);
        }

        [Fact]
        public void A_missing_local_episode_says_so_and_is_dimmed()
        {
            var row = SeriesGrouping.ToRow(new LocalEpisode
            {
                SeasonNumber = 1,
                EpisodeNumber = 3,
                AnyPath = Path.Combine("tv", "Show.S01E03.mkv")
            });

            Assert.False(row.IsAvailable);
            Assert.Null(row.FilePath);
            Assert.Equal("Not found by the last scan — Show.S01E03.mkv", row.Overview);
            Assert.True(row.RowOpacity < 1.0);
        }

        [Fact]
        public void The_loader_picks_the_largest_copy_the_last_scan_found()
        {
            using (var conn = Database.Open(_dbPath))
            {
                conn.Execute(@"
INSERT INTO series (id, title) VALUES (1, 'Show');
INSERT INTO episodes (id, series_id, season_number, episode_number) VALUES (1, 1, 1, 1);
INSERT INTO files (episode_id, file_path, size_bytes) VALUES (1, '/tv/small.mkv', 10);
INSERT INTO files (episode_id, file_path, size_bytes) VALUES (1, '/tv/large.mkv', 99);
INSERT INTO files (episode_id, file_path, size_bytes, missing_since) VALUES (1, '/tv/huge.mkv', 999, '2026-01-01');");
            }

            var episode = new LocalSeriesLoader(_dbPath).Load(1).Single();

            Assert.Equal("/tv/large.mkv", episode.FilePath);
            Assert.True(episode.IsAvailable);
        }

        [Fact]
        public void An_older_library_gains_the_episode_link_and_its_index()
        {
            using (var raw = new SqliteConnection($"Data Source={_dbPath}"))
            {
                raw.Open();
                raw.Execute(@"
CREATE TABLE movies (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, year INTEGER, genres TEXT, poster_path TEXT);
CREATE TABLE files (id INTEGER PRIMARY KEY AUTOINCREMENT, movie_id INTEGER REFERENCES movies(id) ON DELETE SET NULL,
                    file_path TEXT NOT NULL, size_bytes INTEGER, created_at TEXT, updated_at TEXT);
INSERT INTO movies (title) VALUES ('Show S01E01');
INSERT INTO files (movie_id, file_path) VALUES (1, '/tv/Show.S01E01.mkv');");
            }

            using var conn = Database.Open(_dbPath);

            Assert.True(Database.ColumnExists(conn, "files", "episode_id"));
            Assert.True(Database.ColumnExists(conn, "files", "linked_by_hand"));
            Assert.True(Database.TableExists(conn, "series"));
            Assert.True(Database.TableExists(conn, "episodes"));
            Assert.Equal(1, conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_files_episode'"));
            Assert.Equal(0, conn.ExecuteScalar<long>("SELECT linked_by_hand FROM files"));
            Assert.Equal(1, conn.ExecuteScalar<long>("SELECT movie_id FROM files"));
        }

        /// <summary>
        /// A film and a programme sharing a name and an id, a second season, and a programme whose
        /// only file the last scan could not find.
        /// </summary>
        private void Seed()
        {
            using var conn = Database.Open(_dbPath);
            conn.Execute(@"
INSERT INTO movies (id, title, year) VALUES (1, 'Fargo', 1996);
INSERT INTO files (movie_id, file_path) VALUES (1, '/films/Fargo (1996).mkv');

INSERT INTO series (id, title, year) VALUES (1, 'Fargo', 2014);
INSERT INTO episodes (id, series_id, season_number, episode_number) VALUES (1, 1, 1, 1);
INSERT INTO episodes (id, series_id, season_number, episode_number) VALUES (2, 1, 1, 2);
INSERT INTO episodes (id, series_id, season_number, episode_number) VALUES (3, 1, 2, 1);
INSERT INTO files (episode_id, file_path) VALUES (1, '/tv/Fargo.S01E01.mkv');
INSERT INTO files (episode_id, file_path) VALUES (2, '/tv/Fargo.S01E02.mkv');
INSERT INTO files (episode_id, file_path) VALUES (3, '/tv/Fargo.S02E01.mkv');

INSERT INTO series (id, title) VALUES (2, 'Gone Show');
INSERT INTO episodes (id, series_id, season_number, episode_number) VALUES (4, 2, 1, 1);
INSERT INTO files (episode_id, file_path, missing_since) VALUES (4, '/tv/Gone.S01E01.mkv', '2026-01-01');");
        }
    }
}
