using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    /// Marking a copy of a film as having a problem — bad audio, a low resolution, the wrong
    /// aspect ratio — and finding the mark again on the card and on the details screen.
    ///
    /// The store is tested against a real SQLite file in a temporary directory, because what
    /// matters is what SQLite keeps and what an older catalogue without the table does.
    /// </summary>
    public class FilmProblemTests : IDisposable
    {
        private readonly TempLog _log = new();
        private readonly string _root;
        private readonly string _dbPath;

        public FilmProblemTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-problems-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _dbPath = Path.Combine(_root, "movies.db");
        }

        public void Dispose()
        {
            _log.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---------------------------------------------------------------- codes and labels

        [Fact]
        public void Every_kind_survives_being_written_and_read_back()
        {
            var encoded = FilmProblems.Encode(FilmProblems.All);

            Assert.Equal(FilmProblems.All, FilmProblems.Decode(encoded));
        }

        [Fact]
        public void Codes_are_stable_strings_rather_than_enum_numbers()
        {
            Assert.Equal(
                "audio,low-resolution,aspect-ratio",
                FilmProblems.Encode(new[] { FilmProblemKind.AspectRatio, FilmProblemKind.Audio, FilmProblemKind.LowResolution }));
        }

        [Fact]
        public void Ticking_the_same_box_twice_is_one_problem()
        {
            Assert.Equal(
                new[] { FilmProblemKind.Audio },
                FilmProblems.Normalise(new[] { FilmProblemKind.Audio, FilmProblemKind.Audio }));
        }

        [Fact]
        public void A_code_written_by_a_newer_build_is_skipped_rather_than_failing_the_read()
        {
            Assert.Equal(
                new[] { FilmProblemKind.Audio, FilmProblemKind.Subtitles },
                FilmProblems.Decode("audio, dolby-vision-tint ,SUBTITLES,"));
        }

        [Fact]
        public void Every_kind_has_a_label_a_person_can_read()
        {
            foreach (var kind in FilmProblems.All)
                Assert.False(string.IsNullOrWhiteSpace(FilmProblems.Label(kind)));

            Assert.Equal("Low resolution", FilmProblems.Label(FilmProblemKind.LowResolution));
            Assert.Equal("Wrong aspect ratio", FilmProblems.Label(FilmProblemKind.AspectRatio));
        }

        [Fact]
        public void The_summary_names_the_problems_and_then_the_note()
        {
            var both = new FilmProblemReport("local:1", new[] { FilmProblemKind.Audio, FilmProblemKind.AspectRatio }, "  out of sync  ");
            var noteOnly = new FilmProblemReport("local:1", Array.Empty<FilmProblemKind>(), "last reel missing");
            var kindsOnly = new FilmProblemReport("local:1", new[] { FilmProblemKind.LowResolution }, null);

            Assert.Equal("Audio, Wrong aspect ratio — out of sync", FilmProblems.Summary(both));
            Assert.Equal("last reel missing", FilmProblems.Summary(noteOnly));
            Assert.Equal("Low resolution", FilmProblems.Summary(kindsOnly));
            Assert.Equal("", FilmProblems.Summary(null));
        }

        [Fact]
        public void A_report_with_no_ticks_and_no_note_is_empty_but_a_note_alone_is_not()
        {
            Assert.True(new FilmProblemReport("local:1", Array.Empty<FilmProblemKind>(), "   ").IsEmpty);
            Assert.False(new FilmProblemReport("local:1", Array.Empty<FilmProblemKind>(), "skips at 1:02").IsEmpty);
        }

        // ---------------------------------------------------------------- identity

        /// <summary>
        /// The details screen saves under one key and the library looks up under another. If the
        /// two ever disagree, a problem is saved and silently never shown on the card.
        /// </summary>
        [Fact]
        public void The_details_screen_and_the_card_agree_on_which_film_a_report_is_about()
        {
            var local = new UiMovie { Id = 7, Title = "Ran" };
            var server = new UiMovie { Source = MovieSource.Jellyfin, RemoteId = "abc", Title = "Ran" };
            var both = new UiMovie { Id = 7, RemoteId = "abc", Title = "Ran", HasFileHere = true };
            var gone = new UiMovie { Id = 7, RemoteId = "abc", Title = "Ran", HasFileMissing = true };

            Assert.Equal(local.Key, new MovieDetailsVm { LocalId = 7 }.FilmKey);
            Assert.Equal(server.Key, new MovieDetailsVm { RemoteId = "abc", IsRemote = true }.FilmKey);
            Assert.Equal(both.Key, new MovieDetailsVm { LocalId = 7, RemoteId = "abc", IsOnServer = true }.FilmKey);

            // A catalogued film whose file is gone is opened as a server film, but keeps its
            // catalogue identity — and so its report.
            Assert.True(gone.IsRemote);
            Assert.Equal(gone.Key, new MovieDetailsVm { LocalId = 7, RemoteId = "abc", IsRemote = true }.FilmKey);
        }

        [Fact]
        public void A_screen_with_no_film_behind_it_has_nothing_to_key_a_report_on()
        {
            Assert.Null(new MovieDetailsVm().FilmKey);
        }

        // ---------------------------------------------------------------- the store

        [Fact]
        public void A_saved_report_is_read_back_whole()
        {
            using var conn = Database.Open(_dbPath);

            FilmProblemStore.Save(conn, new FilmProblemReport(
                "local:1", new[] { FilmProblemKind.LowResolution, FilmProblemKind.Audio }, "  only 480p, mono  "));

            var report = FilmProblemStore.Load(conn, "local:1");

            Assert.NotNull(report);
            Assert.Equal("local:1", report!.FilmKey);
            Assert.Equal(new[] { FilmProblemKind.Audio, FilmProblemKind.LowResolution }, report.Kinds);
            Assert.Equal("only 480p, mono", report.Note);
            Assert.NotNull(report.FlaggedAt);
        }

        [Fact]
        public void Saving_again_replaces_the_report_rather_than_adding_to_it()
        {
            using var conn = Database.Open(_dbPath);

            FilmProblemStore.Save(conn, new FilmProblemReport("local:1", new[] { FilmProblemKind.Audio }, "hiss"));
            FilmProblemStore.Save(conn, new FilmProblemReport("local:1", new[] { FilmProblemKind.AspectRatio }, null));

            var report = FilmProblemStore.Load(conn, "local:1");

            Assert.Equal(new[] { FilmProblemKind.AspectRatio }, report!.Kinds);
            Assert.Equal("", report.Note);
            Assert.Equal(1L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM film_problems"));
        }

        [Fact]
        public void Clearing_a_film_forgets_its_report_and_leaves_the_others()
        {
            using var conn = Database.Open(_dbPath);

            FilmProblemStore.Save(conn, new FilmProblemReport("local:1", new[] { FilmProblemKind.Audio }, null));
            FilmProblemStore.Save(conn, new FilmProblemReport("jellyfin:abc", new[] { FilmProblemKind.Subtitles }, null));

            FilmProblemStore.Clear(conn, "local:1");

            Assert.Null(FilmProblemStore.Load(conn, "local:1"));
            Assert.NotNull(FilmProblemStore.Load(conn, "jellyfin:abc"));
        }

        [Fact]
        public void Unticking_everything_and_saving_is_the_same_as_clearing()
        {
            using var conn = Database.Open(_dbPath);

            FilmProblemStore.Save(conn, new FilmProblemReport("local:1", new[] { FilmProblemKind.Audio }, "hiss"));
            FilmProblemStore.Save(conn, new FilmProblemReport("local:1", Array.Empty<FilmProblemKind>(), " "));

            Assert.Null(FilmProblemStore.Load(conn, "local:1"));
            Assert.Equal(0L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM film_problems"));
        }

        [Fact]
        public void Every_report_comes_back_keyed_by_film()
        {
            using var conn = Database.Open(_dbPath);

            FilmProblemStore.Save(conn, new FilmProblemReport("local:1", new[] { FilmProblemKind.Audio }, null));
            FilmProblemStore.Save(conn, new FilmProblemReport("jellyfin:abc", new[] { FilmProblemKind.Picture }, null));

            var all = FilmProblemStore.LoadAll(conn);

            Assert.Equal(new[] { "jellyfin:abc", "local:1" }, all.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(new[] { FilmProblemKind.Picture }, all["jellyfin:abc"].Kinds);
        }

        /// <summary>
        /// The library's read path connects without migrating, so the first launch after an
        /// upgrade reads a catalogue that has no such table yet. That must be "nothing reported",
        /// not a library that fails to load.
        /// </summary>
        [Fact]
        public void A_catalogue_that_predates_the_table_has_nothing_reported()
        {
            using (var conn = Database.Connect(_dbPath))
            {
                conn.Execute("CREATE TABLE movies (id INTEGER PRIMARY KEY, title TEXT NOT NULL)");

                Assert.Empty(FilmProblemStore.LoadAll(conn));
                Assert.Null(FilmProblemStore.Load(conn, "local:1"));
            }

            Assert.Empty(FilmProblemStore.LoadAll(_dbPath));
        }

        [Fact]
        public void Reading_reports_never_creates_a_catalogue()
        {
            var missing = Path.Combine(_root, "nothing-here.db");

            Assert.Empty(FilmProblemStore.LoadAll(missing));
            Assert.False(File.Exists(missing));
        }

        [Fact]
        public void An_existing_library_gains_the_table_when_it_is_opened()
        {
            using (var old = new SqliteConnection($"Data Source={_dbPath}"))
            {
                old.Open();
                old.Execute(@"
CREATE TABLE movies (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, year INTEGER);
INSERT INTO movies (title, year) VALUES ('Ran', 1985);");
            }

            using var conn = Database.Open(_dbPath);

            Assert.True(Database.TableExists(conn, "film_problems"));
            Assert.Equal("Ran", conn.ExecuteScalar<string>("SELECT title FROM movies WHERE id = 1"));

            FilmProblemStore.Save(conn, new FilmProblemReport("local:1", new[] { FilmProblemKind.Audio }, null));
            Assert.NotNull(FilmProblemStore.Load(conn, "local:1"));
        }

        // ---------------------------------------------------------------- the card

        [Fact]
        public void A_card_with_a_report_carries_the_badge_and_says_why()
        {
            var card = new UiMovie { Id = 1, Title = "Ran" };
            var changed = new List<string?>();
            ((INotifyPropertyChanged)card).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            card.ProblemReport = new FilmProblemReport("local:1", new[] { FilmProblemKind.LowResolution }, "VHS rip");

            Assert.True(card.HasProblem);
            Assert.Equal("Problem", card.ProblemBadge);
            Assert.Equal("Problem: Low resolution — VHS rip", card.ProblemTooltip);
            Assert.Contains("Problem: Low resolution — VHS rip", card.CardTooltip);
            Assert.Contains(nameof(UiMovie.HasProblem), changed);
            Assert.Contains(nameof(UiMovie.CardTooltip), changed);
        }

        [Fact]
        public void An_empty_report_does_not_put_a_badge_on_the_card()
        {
            var card = new UiMovie { Id = 1, Title = "Ran" };

            card.ProblemReport = new FilmProblemReport("local:1", Array.Empty<FilmProblemKind>(), "");

            Assert.False(card.HasProblem);
            Assert.Equal("", card.ProblemTooltip);
            Assert.Equal("Ran", card.CardTooltip);
        }

        [Fact]
        public void Stamping_puts_each_report_on_its_own_card_and_takes_a_cleared_one_off()
        {
            var local = new UiMovie { Id = 1, Title = "Ran" };
            var server = new UiMovie { Source = MovieSource.Jellyfin, RemoteId = "abc", Title = "Heat" };
            var clean = new UiMovie { Id = 2, Title = "Manhattan" };

            // A card that carried a badge from an earlier read, whose report has since been cleared.
            clean.ProblemReport = new FilmProblemReport("local:2", new[] { FilmProblemKind.Audio }, null);

            var reports = new Dictionary<string, FilmProblemReport>
            {
                ["local:1"] = new("local:1", new[] { FilmProblemKind.Audio }, null),
                ["jellyfin:abc"] = new("jellyfin:abc", new[] { FilmProblemKind.AspectRatio }, null),
            };

            FilmProblems.Stamp(new[] { local, server, clean }, reports);

            Assert.True(local.HasProblem);
            Assert.Equal(new[] { FilmProblemKind.AspectRatio }, server.ProblemReport!.Kinds);
            Assert.False(clean.HasProblem);
        }

        // ---------------------------------------------------------------- the library read

        [Fact]
        public void The_library_read_badges_the_films_that_have_a_problem()
        {
            using (var conn = Database.Open(_dbPath))
            {
                conn.Execute("INSERT INTO movies (title, year) VALUES ('Ran', 1985), ('Heat', 1995)");
                var ranId = conn.ExecuteScalar<long>("SELECT id FROM movies WHERE title = 'Ran'");

                FilmProblemStore.Save(conn, new FilmProblemReport($"local:{ranId}", new[] { FilmProblemKind.Audio }, null));
                FilmProblemStore.Save(conn, new FilmProblemReport("jellyfin:srv", new[] { FilmProblemKind.LowResolution }, null));
            }

            var server = JellyfinLibrary.ToUiMovie(new JellyfinMovie { ItemId = "srv", Title = "Solaris", Year = 1972 });

            var view = new LibraryLoader(new MovieRepository(_dbPath)).Load(null, new[] { server });

            Assert.True(view.All.Single(m => m.Title == "Ran").HasProblem);
            Assert.False(view.All.Single(m => m.Title == "Heat").HasProblem);
            Assert.True(view.All.Single(m => m.Title == "Solaris").HasProblem);
        }

        [Fact]
        public void A_cleared_report_takes_the_badge_off_at_the_next_read()
        {
            long ranId;
            using (var conn = Database.Open(_dbPath))
            {
                conn.Execute("INSERT INTO movies (title, year) VALUES ('Ran', 1985)");
                ranId = conn.ExecuteScalar<long>("SELECT id FROM movies WHERE title = 'Ran'");
                FilmProblemStore.Save(conn, new FilmProblemReport($"local:{ranId}", new[] { FilmProblemKind.Audio }, null));
            }

            var loader = new LibraryLoader(new MovieRepository(_dbPath));
            Assert.True(loader.Load(null).All.Single().HasProblem);

            using (var conn = Database.Open(_dbPath))
                FilmProblemStore.Clear(conn, $"local:{ranId}");

            Assert.False(loader.Load(null).All.Single().HasProblem);
        }
    }
}
