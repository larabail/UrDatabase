using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// What a scan knows about a library between one run and the next.
    ///
    /// Before this, a scan could only add. It upserted the paths it found and nothing else, so
    /// there was no answer to "what did the last scan not find" and therefore no way to notice a
    /// film had been deleted, no way to tell that from a drive being unplugged, and no way to
    /// follow a file that had simply been dragged into another folder — that arrived as a second
    /// row while the first stayed behind claiming to be the same film.
    ///
    /// Every test here fails against that scanner.
    /// </summary>
    public class ScanLifecycleTests : IDisposable
    {
        private readonly string _root;

        public ScanLifecycleTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- missing -----------------------------------------------------------------------

        [Fact]
        public async Task A_file_deleted_between_two_scans_is_marked_missing_rather_than_forgotten()
        {
            var films = MakeFolder("Films");
            Write(films, "The Matrix (1999).mkv");
            Write(films, "Heat (1995).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            File.Delete(Path.Combine(films, "Heat (1995).mkv"));
            var second = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(1, second.Missing);

            // Marked, not deleted. The row is the only record that the film was ever there, and
            // this process cannot tell "you deleted it" from "that disk is not plugged in".
            Assert.Equal(2L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files"));
            Assert.NotNull(MissingSince(conn, Path.Combine(films, "Heat (1995).mkv")));
            Assert.Null(MissingSince(conn, Path.Combine(films, "The Matrix (1999).mkv")));
        }

        [Fact]
        public async Task A_file_that_comes_back_stops_being_missing()
        {
            var films = MakeFolder("Films");
            var path = Write(films, "The Matrix (1999).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            File.Delete(path);
            await new ScanService().ScanAsync(conn, new[] { films });
            Assert.NotNull(MissingSince(conn, path));

            Write(films, "The Matrix (1999).mkv");
            var third = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Null(MissingSince(conn, path));
            Assert.Equal(0, third.Missing);
            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files"));
        }

        [Fact]
        public async Task A_row_already_missing_keeps_the_date_it_first_went_missing()
        {
            // Any eventual prune counts from this timestamp, so a scan that restamped it every
            // time would mean a row could never age past "missing since a moment ago".
            var films = MakeFolder("Films");
            var path = Write(films, "The Matrix (1999).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            File.Delete(path);
            await new ScanService().ScanAsync(conn, new[] { films });
            var first = MissingSince(conn, path);

            await Task.Delay(20);
            var third = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(first, MissingSince(conn, path));
            Assert.Equal(0, third.Missing);
        }

        [Fact]
        public async Task A_cancelled_scan_marks_nothing_missing()
        {
            // The dangerous case. A cancelled scan stops somewhere arbitrary, so everything it had
            // not reached yet is indistinguishable from everything that is gone — and concluding
            // the second would condemn most of a large library on a scan somebody stopped early.
            var films = MakeFolder("Films");
            Write(films, "The Matrix (1999).mkv");
            Write(films, "Heat (1995).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            File.Delete(Path.Combine(films, "Heat (1995).mkv"));

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var cancelled = await new ScanService().ScanAsync(conn, new[] { films }, null, cts.Token);

            Assert.Equal(ScanStatus.Cancelled, cancelled.Status);
            Assert.Equal(0, cancelled.Missing);
            Assert.Equal(0L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files WHERE missing_since IS NOT NULL"));
        }

        [Fact]
        public async Task A_watch_folder_that_is_not_there_costs_nothing()
        {
            // An unplugged external drive. The folder is gone, every film on it is unreachable,
            // and the correct number of rows to touch is zero.
            var onDisk = MakeFolder("Films");
            var removable = MakeFolder("Removable");
            Write(onDisk, "The Matrix (1999).mkv");
            Write(removable, "Heat (1995).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { onDisk, removable });

            Directory.Delete(removable, recursive: true);
            var second = await new ScanService().ScanAsync(conn, new[] { onDisk, removable });

            Assert.Equal(ScanStatus.Completed, second.Status);
            Assert.Equal(0, second.Missing);
            Assert.Equal(new[] { removable }, second.SkippedRoots);
            Assert.Equal(0L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files WHERE missing_since IS NOT NULL"));
        }

        [Fact]
        public async Task A_scan_of_one_folder_says_nothing_about_another()
        {
            var films = MakeFolder("Films");
            var others = MakeFolder("Others");
            Write(films, "The Matrix (1999).mkv");
            Write(others, "Heat (1995).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films, others });

            var narrower = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(0, narrower.Missing);
            Assert.Null(MissingSince(conn, Path.Combine(others, "Heat (1995).mkv")));
        }

        [Fact]
        public async Task Every_file_the_scan_walks_past_is_stamped_as_seen_including_an_unchanged_one()
        {
            // The column only works if it is written for a file that did not change, which is most
            // of them: a row nobody stamped has to mean a file nothing found.
            var films = MakeFolder("Films");
            Write(films, "The Matrix (1999).mkv");

            using var conn = Database.Open(DbPath);
            var first = await new ScanService().ScanAsync(conn, new[] { films });
            var firstSeen = conn.QuerySingle<string>("SELECT last_seen_at FROM files");

            await Task.Delay(20);
            var second = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(1, second.Unchanged);
            Assert.Equal(0, second.Inserted);
            Assert.Equal(0, second.Updated);

            var secondSeen = conn.QuerySingle<string>("SELECT last_seen_at FROM files");
            Assert.NotNull(secondSeen);
            Assert.NotEqual(firstSeen, secondSeen);

            Assert.Equal(second.ScanId, conn.QuerySingle<long>("SELECT last_seen_scan_id FROM files"));
            Assert.NotEqual(first.ScanId, second.ScanId);
        }

        // ---- moves -------------------------------------------------------------------------

        [Fact]
        public async Task A_film_dragged_into_another_folder_keeps_its_row()
        {
            var films = MakeFolder("Films");
            var from = Write(films, "The Matrix (1999).mkv", "a longer body so the size is distinctive");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            var movieId = conn.QuerySingle<long>("SELECT movie_id FROM files");
            var fileId = conn.QuerySingle<long>("SELECT id FROM files");

            var deeper = Path.Combine(films, "4K");
            Directory.CreateDirectory(deeper);
            var to = Path.Combine(deeper, "The Matrix (1999).mkv");
            File.Move(from, to);

            var second = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(1, second.Moved);
            Assert.Equal(0, second.Inserted);
            Assert.Equal(0, second.Missing);

            // The same row, at the new path, still linked to the same film.
            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files"));
            Assert.Equal(fileId, conn.QuerySingle<long>("SELECT id FROM files"));
            Assert.Equal(to, conn.QuerySingle<string>("SELECT file_path FROM files"));
            Assert.Equal(movieId, conn.QuerySingle<long>("SELECT movie_id FROM files"));
            Assert.Null(MissingSince(conn, to));
        }

        [Fact]
        public async Task A_move_keeps_a_link_a_person_made_by_hand()
        {
            var films = MakeFolder("Films");
            var from = Write(films, "The Matrix (1999).mkv", "a longer body so the size is distinctive");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            // Somebody corrects the scan's guess. That correction has to survive the film moving.
            conn.Execute("INSERT INTO movies (title, year) VALUES ('Something Else', 2020)");
            var chosen = conn.QuerySingle<long>("SELECT id FROM movies WHERE title = 'Something Else'");
            conn.Execute("UPDATE files SET movie_id = @chosen", new { chosen });

            var deeper = Path.Combine(films, "4K");
            Directory.CreateDirectory(deeper);
            File.Move(from, Path.Combine(deeper, "The Matrix (1999).mkv"));

            await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(chosen, conn.QuerySingle<long>("SELECT movie_id FROM files"));
        }

        [Fact]
        public async Task A_second_copy_is_a_duplicate_rather_than_a_move()
        {
            // Same name, same size, but the original is still there. Relinking would quietly throw
            // away one of two real files.
            var films = MakeFolder("Films");
            var original = Write(films, "The Matrix (1999).mkv", "a longer body so the size is distinctive");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            var deeper = Path.Combine(films, "4K");
            Directory.CreateDirectory(deeper);
            File.Copy(original, Path.Combine(deeper, "The Matrix (1999).mkv"));

            var second = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(1, second.Inserted);
            Assert.Equal(0, second.Moved);
            Assert.Equal(2L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files"));
        }

        [Fact]
        public async Task Two_files_that_look_identical_are_not_guessed_between()
        {
            // One name and one size shared by two rows. Which of them became the new file is not
            // knowable from here, and guessing would scramble two links instead of losing one.
            var a = MakeFolder(Path.Combine("Films", "a"));
            var b = MakeFolder(Path.Combine("Films", "b"));
            var films = Path.Combine(_root, "Films");

            Write(a, "The Matrix (1999).mkv", "identical bodies, identical sizes");
            Write(b, "The Matrix (1999).mkv", "identical bodies, identical sizes");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            Directory.Delete(a, recursive: true);
            Directory.Delete(b, recursive: true);
            var c = MakeFolder(Path.Combine("Films", "c"));
            Write(c, "The Matrix (1999).mkv", "identical bodies, identical sizes");

            var second = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(0, second.Moved);
            Assert.Equal(1, second.Inserted);
            Assert.Equal(2, second.Missing);
            Assert.Equal(3L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files"));
        }

        [Fact]
        public async Task A_renamed_file_is_not_claimed_as_a_move()
        {
            // Documenting the limit rather than hiding it. Identity is the name plus the size, so
            // renaming reads as a deletion and an addition — the safe way to be wrong.
            var films = MakeFolder("Films");
            var from = Write(films, "The Matrix (1999).mkv", "a longer body so the size is distinctive");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            File.Move(from, Path.Combine(films, "The Matrix (1999) 1080p.mkv"));
            var second = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(0, second.Moved);
            Assert.Equal(1, second.Inserted);
            Assert.Equal(1, second.Missing);
        }

        // ---- counts ------------------------------------------------------------------------

        [Fact]
        public async Task The_counts_are_separated_rather_than_one_ambiguous_number()
        {
            var films = MakeFolder("Films");
            Write(films, "The Matrix (1999).mkv");
            Write(films, "Heat (1995).mkv");
            var changing = Write(films, "Alien (1979).mkv");

            using var conn = Database.Open(DbPath);
            var first = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(3, first.Inserted);
            Assert.Equal(0, first.Updated);
            Assert.Equal(0, first.Unchanged);

            File.WriteAllText(changing, "a re-encode, so the size and the write time both change");
            Write(films, "Blade Runner (1982).mkv");

            var second = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(1, second.Inserted);
            Assert.Equal(1, second.Updated);
            Assert.Equal(2, second.Unchanged);
            Assert.Equal(0, second.Failed);
            Assert.Equal(0, second.Missing);
            Assert.Equal(4, second.FilesSeen);

            Assert.Equal("Scan complete. 1 added, 1 updated, 2 unchanged.", second.Summary);
        }

        [Fact]
        public async Task A_file_the_scan_cannot_record_is_counted_as_failed_rather_than_lost()
        {
            // A write that fails for one file must not be silent and must not stop the scan. The
            // trigger stands in for whatever does it on a real machine — a disk filling up, a
            // constraint nobody anticipated.
            var films = MakeFolder("Films");
            Write(films, "The Matrix (1999).mkv");
            Write(films, "Heat (1995).mkv");

            using var conn = Database.Open(DbPath);
            conn.Execute("CREATE TRIGGER no_more_films BEFORE INSERT ON movies BEGIN SELECT RAISE(FAIL, 'no'); END;");

            var result = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(2, result.Failed);
            Assert.Equal(0, result.Inserted);
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Contains("2 failed", result.Summary, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Overlapping_watch_folders_do_not_count_a_film_twice()
        {
            var films = MakeFolder("Films");
            var inner = MakeFolder(Path.Combine("Films", "4K"));
            Write(inner, "The Matrix (1999).mkv");

            using var conn = Database.Open(DbPath);
            var result = await new ScanService().ScanAsync(conn, new[] { films, inner });

            Assert.Equal(1, result.FilesSeen);
            Assert.Equal(1, result.Inserted);
            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files"));
        }

        // ---- the scan session ---------------------------------------------------------------

        [Fact]
        public async Task A_scan_records_itself()
        {
            var films = MakeFolder("Films");
            var gone = Path.Combine(_root, "Removable");
            Write(films, "The Matrix (1999).mkv");

            using var conn = Database.Open(DbPath);
            var result = await new ScanService().ScanAsync(conn, new[] { films, gone });

            var row = conn.QuerySingle(
                "SELECT status, started_at, finished_at, roots, skipped_roots, inserted, missing FROM scans WHERE id = @id",
                new { id = result.ScanId });

            Assert.Equal("completed", (string)row.status);
            Assert.NotNull((string?)row.started_at);
            Assert.NotNull((string?)row.finished_at);
            Assert.Equal(1L, (long)row.inserted);
            Assert.Equal(0L, (long)row.missing);
            Assert.Equal(new[] { films }, ScanSessions.Decode((string?)row.roots));
            Assert.Equal(new[] { gone }, ScanSessions.Decode((string?)row.skipped_roots));
        }

        [Fact]
        public async Task A_cancelled_scan_says_so_in_its_own_row()
        {
            // Which is the whole point of recording the status: nothing may be concluded from a
            // scan that did not finish, and a later reader has to be able to tell.
            var films = MakeFolder("Films");
            Write(films, "The Matrix (1999).mkv");

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            using var conn = Database.Open(DbPath);
            var result = await new ScanService().ScanAsync(conn, new[] { films }, null, cts.Token);

            Assert.Equal(ScanStatus.Cancelled, await ScanSessions.StatusOfAsync(conn, result.ScanId));
            Assert.Contains("Nothing was marked missing", result.Summary, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Each_scan_gets_its_own_id_and_they_are_kept()
        {
            var films = MakeFolder("Films");
            Write(films, "The Matrix (1999).mkv");

            using var conn = Database.Open(DbPath);
            var first = await new ScanService().ScanAsync(conn, new[] { films });
            var second = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.NotEqual(first.ScanId, second.ScanId);
            Assert.Equal(2L, conn.QuerySingle<long>("SELECT COUNT(*) FROM scans"));
            Assert.Equal(
                new[] { "completed", "completed" },
                conn.Query<string>("SELECT status FROM scans ORDER BY id").ToArray());
        }

        [Fact]
        public async Task A_scan_that_fails_outright_is_recorded_as_failed_rather_than_left_running()
        {
            var films = MakeFolder("Films");
            Write(films, "The Matrix (1999).mkv");

            // A connection somebody disposed underneath the scan: it gets far enough to open its
            // own row and then cannot begin a transaction.
            var conn = Database.Open(DbPath);
            conn.Dispose();

            await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => new ScanService().ScanAsync(conn, new[] { films }));

            using var verify = Database.Open(DbPath);
            Assert.Equal(
                "failed",
                verify.QuerySingle<string>("SELECT status FROM scans ORDER BY id DESC LIMIT 1"));
        }

        [Fact]
        public async Task A_scan_that_fails_lets_go_of_the_write_lane()
        {
            // Found the hard way. The lane was taken on the line above the one that opened the
            // transaction, so a transaction that could not be opened left the lane held for the
            // life of the process — and the very next thing the scan did was try to take it again
            // to record that it had failed. Nothing threw and nothing was written; the app simply
            // stopped, with no error, forever.
            var films = MakeFolder("Films");
            Write(films, "The Matrix (1999).mkv");

            var conn = Database.Open(DbPath);
            conn.Dispose();

            await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => new ScanService().ScanAsync(conn, new[] { films }));

            // Asserted with a deadline rather than by awaiting, because the failure being guarded
            // against is a wait that never ends, and a hung test says far less than a failed one.
            var write = DatabaseWriteLane.RunAsync(
                conn,
                _ => conn.ExecuteAsync("INSERT INTO movies (title) VALUES ('after the failure')"),
                CancellationToken.None);

            var finished = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(ReferenceEquals(finished, write), "the scan never gave the write lane back");
            await write;
        }

        [Fact]
        public async Task An_unreadable_folder_does_not_condemn_the_films_in_it()
        {
            // macOS refuses a folder outright until it has been granted, and answering a first
            // permission prompt by marking somebody's library missing would be a poor trade.
            if (OperatingSystem.IsWindows()) return;

            var films = MakeFolder("Films");
            var locked = MakeFolder(Path.Combine("Films", "locked"));
            Write(films, "The Matrix (1999).mkv");
            var hidden = Write(locked, "Heat (1995).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });
            Assert.Equal(2L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files"));

            File.SetUnixFileMode(locked, UnixFileMode.None);
            try
            {
                var second = await new ScanService().ScanAsync(conn, new[] { films });

                Assert.Equal(0, second.Missing);
                Assert.Null(MissingSince(conn, hidden));
            }
            finally
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        // ---- helpers -------------------------------------------------------------------------

        private string DbPath => Path.Combine(_root, "movies.db");

        private string MakeFolder(string relative)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(path);
            return path;
        }

        private static string Write(string folder, string name, string body = "x")
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, body);
            return path;
        }

        private static string? MissingSince(Microsoft.Data.Sqlite.SqliteConnection conn, string path) =>
            conn.QuerySingleOrDefault<string?>(
                "SELECT missing_since FROM files WHERE file_path = @path", new { path });
    }
}
