using System;
using System.Collections.Generic;
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
    /// The drive check: which drives a library lives on, which of them answered, and what the
    /// banner says about the ones that did not. Every probe here is a fake, apart from the one
    /// test that points the real probe at a temporary directory, so nothing depends on what is
    /// plugged into the machine running the suite.
    /// </summary>
    public class DriveStatusTests : IDisposable
    {
        private readonly TempLog _log = new();
        private readonly string _root;

        public DriveStatusTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-drives-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            _log.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- which drive a path is on ----------------------------------------------------------

        [Theory]
        [InlineData(@"D:\Films\Alien (1979)\Alien.mkv", @"D:\")]
        [InlineData(@"d:\Films", @"D:\")]
        [InlineData(@"E:", @"E:\")]
        [InlineData(@"\\nas\movies\Alien.mkv", @"\\nas\movies\")]
        [InlineData(@"//nas/movies/Alien.mkv", @"\\nas\movies\")]
        [InlineData("/Volumes/Films/Alien (1979)/Alien.mkv", "/Volumes/Films")]
        [InlineData("/Volumes/Films", "/Volumes/Films")]
        [InlineData("/Users/ana/Movies/Alien.mkv", "/")]
        [InlineData("/media/ana/Films/Alien", "/media/ana/Films")]
        [InlineData("/run/media/ana/Films/Alien", "/run/media/ana/Films")]
        [InlineData("/mnt/films/Alien", "/mnt/films")]
        public void A_path_names_the_volume_it_lives_on(string path, string expected) =>
            Assert.Equal(expected, DriveVolumes.VolumeOf(path));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Films/Alien.mkv")]
        [InlineData(@"\\nas")]
        public void A_path_that_names_no_drive_names_none(string? path) =>
            Assert.Null(DriveVolumes.VolumeOf(path));

        [Fact]
        public void A_macOS_disk_is_not_mistaken_for_the_boot_volume()
        {
            // The trap the whole class exists to avoid: every external disk on a Mac is under "/",
            // so a check of the path's root would report an unplugged disk as present.
            Assert.NotEqual(DriveVolumes.VolumeOf("/Users/ana/Movies"), DriveVolumes.VolumeOf("/Volumes/Films/x.mkv"));
        }

        [Fact]
        public void Drives_come_from_both_the_watch_folders_and_the_catalogue()
        {
            var drives = DriveVolumes.Collect(
                new[] { @"D:\Films", @"d:\More films", @"E:\Series" },
                new (string, long?)[]
                {
                    (@"D:\Films\Alien.mkv", 1),
                    (@"D:\Films\Alien.extended.mkv", 1),   // a second print is not a second film
                    (@"D:\More films\Heat.mkv", 2),
                    (@"F:\Old\Ran.mkv", 3),                // only the catalogue knows about F:
                    (@"F:\Old\orphan.mkv", null),
                    ("relative.mkv", 4)
                });

            Assert.Equal(new[] { @"D:\", @"E:\", @"F:\" }, drives.Select(d => d.Root));

            var d = drives[0];
            Assert.Equal(2, d.FilmCount);
            Assert.Equal(new[] { @"D:\Films", @"d:\More films" }, d.WatchFolders);

            Assert.Equal(0, drives[1].FilmCount);
            Assert.Equal(1, drives[2].FilmCount);
            Assert.Empty(drives[2].WatchFolders);
        }

        [Fact]
        public void A_file_directly_on_a_mount_point_is_not_probed_as_a_folder()
        {
            var drives = DriveVolumes.Collect(null, new (string, long?)[] { ("/media/films/Alien.mkv", 1) });

            Assert.Equal("/media/films", drives.Single().Root);
        }

        [Fact]
        public void Nothing_configured_and_nothing_scanned_is_no_drives() =>
            Assert.Empty(DriveVolumes.Collect(null, null));

        // ---- checking them ---------------------------------------------------------------------

        [Fact]
        public async Task Each_drive_is_reported_as_the_probe_found_it()
        {
            var probe = new FakeProbe
            {
                [@"D:\"] = new DriveProbeResult(true, "Films", 2_000, 500),
                [@"E:\"] = DriveProbeResult.Unavailable
            };

            var reports = await new DriveMonitor(probe).CheckAsync(new[]
            {
                new DriveUsage(@"D:\", new[] { @"D:\Films" }, 10),
                new DriveUsage(@"E:\", new[] { @"E:\Series" }, 4)
            });

            var d = reports.Single(r => r.Root == @"D:\");
            Assert.Equal(DriveState.Connected, d.State);
            Assert.False(d.IsDown);
            Assert.Equal("Films", d.Label);
            Assert.Equal(500, d.FreeBytes);
            Assert.Equal(10, d.FilmCount);

            var e = reports.Single(r => r.Root == @"E:\");
            Assert.Equal(DriveState.Disconnected, e.State);
            Assert.True(e.IsDown);
            Assert.Equal(4, e.FilmCount);
        }

        [Fact]
        public async Task A_drive_that_never_answers_is_reported_without_waiting_for_it()
        {
            using var never = new ManualResetEventSlim(false);
            var probe = new FakeProbe { [@"D:\"] = new DriveProbeResult(true) };
            probe.Hang(@"\\nas\movies\", never);

            try
            {
                // Generous for a probe that answers at once, tiny next to one that never does.
                var started = DateTime.UtcNow;
                var reports = await new DriveMonitor(probe, TimeSpan.FromSeconds(2)).CheckAsync(new[]
                {
                    new DriveUsage(@"D:\", Array.Empty<string>(), 1),
                    new DriveUsage(@"\\nas\movies\", Array.Empty<string>(), 7)
                });

                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
                Assert.Equal(DriveState.Connected, reports.Single(r => r.Root == @"D:\").State);
                Assert.Equal(DriveState.NotResponding, reports.Single(r => r.Root == @"\\nas\movies\").State);
            }
            finally
            {
                never.Set();
            }
        }

        [Fact]
        public async Task A_probe_that_throws_counts_as_the_drive_being_away()
        {
            var probe = new FakeProbe();
            probe.Throw(@"D:\");

            var reports = await new DriveMonitor(probe).CheckAsync(new[] { new DriveUsage(@"D:\", Array.Empty<string>(), 1) });

            Assert.Equal(DriveState.Disconnected, reports.Single().State);
        }

        [Fact]
        public async Task The_catalogue_supplies_drives_and_ignores_files_a_scan_found_gone()
        {
            var db = Path.Combine(_root, "movies.db");
            using (var conn = Database.Open(db))
            {
                conn.Execute("INSERT INTO movies (id, title) VALUES (1, 'Alien'), (2, 'Heat'), (3, 'Ran')");
                conn.Execute(
                    "INSERT INTO files (movie_id, file_path, missing_since) VALUES " +
                    @"(1, 'Q:\Films\Alien.mkv', NULL), " +
                    @"(2, 'Q:\Films\Heat.mkv', NULL), " +
                    // Recorded as deleted by a completed scan: a retired disk stays retired.
                    @"(3, 'R:\Old\Ran.mkv', '2026-01-01T00:00:00Z')");
            }

            var probe = new FakeProbe { [@"Q:\"] = DriveProbeResult.Unavailable, [@"S:\"] = new DriveProbeResult(true) };

            var reports = await Monitor(probe).CheckDrivesAsync(new[] { @"S:\Series" }, db);

            Assert.Equal(new[] { @"Q:\", @"S:\" }, reports.Select(r => r.Root));
            Assert.Equal(DriveState.Disconnected, reports[0].State);
            Assert.Equal(2, reports[0].FilmCount);
            Assert.Equal(DriveState.Connected, reports[1].State);
        }

        [Fact]
        public async Task Checking_without_a_catalogue_does_not_create_one()
        {
            var db = Path.Combine(_root, "never", "movies.db");
            var probe = new FakeProbe { [@"S:\"] = DriveProbeResult.Unavailable };

            var reports = await Monitor(probe).CheckDrivesAsync(new[] { @"S:\Films" }, db);

            Assert.Equal(DriveState.Disconnected, reports.Single().State);
            Assert.False(File.Exists(db));
            Assert.False(Directory.Exists(Path.GetDirectoryName(db)));
        }

        [Fact]
        public void The_real_probe_tells_a_present_folder_from_an_absent_one()
        {
            Assert.True(FileSystemDriveProbe.Instance.Probe(_root).IsAvailable);
            Assert.False(FileSystemDriveProbe.Instance.Probe(Path.Combine(_root, "unplugged")).IsAvailable);
        }

        // ---- what gets said --------------------------------------------------------------------

        [Fact]
        public void A_healthy_library_raises_no_banner()
        {
            var reports = new[] { Report(@"D:\", DriveState.Connected, 10) };

            Assert.Null(DriveStatusText.Headline(reports));
            Assert.Equal("", DriveStatusText.Detail(reports));
            Assert.Null(DriveStatusText.Headline(Array.Empty<DriveReport>()));
            Assert.Null(DriveStatusText.Headline(null));
        }

        [Fact]
        public void One_drive_away_is_named_with_what_it_costs()
        {
            var reports = new[] { Report(@"D:\", DriveState.Connected, 10), Report(@"E:\", DriveState.Disconnected, 312) };

            Assert.Equal(@"E:\ is not connected", DriveStatusText.Headline(reports));
            Assert.Equal("312 films on it cannot be played until it is back.", DriveStatusText.Detail(reports));
        }

        [Fact]
        public void A_share_that_did_not_answer_says_so_rather_than_claiming_it_is_unplugged()
        {
            var reports = new[] { Report(@"\\nas\movies\", DriveState.NotResponding, 1) };

            Assert.Equal(@"\\nas\movies\ is not responding", DriveStatusText.Headline(reports));
            Assert.Equal("1 film on it cannot be played until it is back.", DriveStatusText.Detail(reports));
        }

        [Fact]
        public void Several_drives_away_are_counted_and_listed()
        {
            var reports = new[] { Report(@"E:\", DriveState.Disconnected, 3), Report(@"F:\", DriveState.NotResponding, 0) };

            Assert.Equal("2 drives are not available", DriveStatusText.Headline(reports));
            Assert.Equal(@"E:\, F:\. 3 films on them cannot be played until they are back.", DriveStatusText.Detail(reports));
        }

        [Fact]
        public void A_drive_holding_only_an_empty_library_folder_still_says_what_it_costs()
        {
            var reports = new[] { Report(@"E:\", DriveState.Disconnected, 0) };

            Assert.Equal("A scan will skip the library folders on it until it is back.", DriveStatusText.Detail(reports));
        }

        [Fact]
        public void The_dialog_lists_every_drive_with_the_missing_ones_first()
        {
            var reports = new[]
            {
                new DriveReport(@"C:\", DriveState.Connected, new[] { @"C:\Films" }, 40, "Windows", 2L << 40, 1L << 40),
                Report(@"E:\", DriveState.Disconnected, 312)
            };

            var text = DriveStatusText.Describe(reports);

            Assert.True(text.IndexOf(@"E:\", StringComparison.Ordinal) < text.IndexOf(@"C:\", StringComparison.Ordinal));
            Assert.Contains(@"E:\ — NOT CONNECTED", text);
            Assert.Contains("312 films", text);
            Assert.Contains(@"C:\ (Windows) — connected", text);
            Assert.Contains($"{ByteSize.Describe(1L << 40)} free of {ByteSize.Describe(2L << 40)}", text);
            Assert.Contains(@"Library folder: C:\Films", text);
        }

        [Fact]
        public async Task A_drive_with_files_but_no_library_folder_does_not_raise_the_alarm()
        {
            // A disk that died and whose library folder was then removed in Settings: its files
            // are never walked again, so nothing ever marks them missing. The same goes for a
            // copy linked by hand from outside every library folder. Neither may hold the banner
            // up on every launch with no way to clear it.
            var db = Path.Combine(_root, "movies.db");
            using (var conn = Database.Open(db))
            {
                conn.Execute("INSERT INTO movies (id, title) VALUES (1, 'Ran')");
                conn.Execute(@"INSERT INTO files (movie_id, file_path) VALUES (1, 'R:\Old\Ran.mkv')");
            }

            var probe = new FakeProbe { [@"S:\"] = new DriveProbeResult(true) };

            var reports = await Monitor(probe).CheckDrivesAsync(new[] { @"S:\Films" }, db);

            var retired = reports.Single(r => r.Root == @"R:\");
            Assert.Equal(DriveState.Disconnected, retired.State);
            Assert.False(retired.RaisesAlarm);

            Assert.Null(DriveStatusText.Headline(reports));
            Assert.Equal("", DriveStatusText.Detail(reports));

            // Still visible to somebody who goes looking.
            var dialog = DriveStatusText.Describe(reports);
            Assert.Contains(@"R:\ — NOT CONNECTED", dialog);
            Assert.Contains("No library folder", dialog);
        }

        [Fact]
        public void Only_drives_holding_a_library_folder_are_counted_in_the_banner()
        {
            var reports = new[]
            {
                Report(@"E:\", DriveState.Disconnected, 3),
                new DriveReport(@"R:\", DriveState.Disconnected, Array.Empty<string>(), 9)
            };

            Assert.Equal(@"E:\ is not connected", DriveStatusText.Headline(reports));
            Assert.Equal("3 films on it cannot be played until it is back.", DriveStatusText.Detail(reports));
        }

        [Fact]
        public void The_dialog_explains_an_empty_answer() =>
            Assert.Contains("no drives to check", DriveStatusText.Describe(Array.Empty<DriveReport>()));

        /// <summary>A monitor that sees no drives plugged in but the ones a test names.</summary>
        private static DriveMonitor Monitor(IDriveProbe probe, params MountedVolume[] mounted) =>
            new(probe, drives: new FakeDrives(mounted));

        private static DriveReport Report(string root, DriveState state, int films) =>
            new(root, state, new[] { root + "Films" }, films);

        private sealed class FakeDrives : IDriveEnumerator
        {
            private readonly IReadOnlyList<MountedVolume> _mounted;

            public FakeDrives(IReadOnlyList<MountedVolume> mounted) => _mounted = mounted;

            public IReadOnlyList<MountedVolume> Enumerate() => _mounted;
        }

        private sealed class FakeProbe : IDriveProbe
        {
            private readonly Dictionary<string, DriveProbeResult> _answers = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, ManualResetEventSlim> _hangs = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _throws = new(StringComparer.OrdinalIgnoreCase);

            public DriveProbeResult this[string root] { set => _answers[root] = value; }

            public void Hang(string root, ManualResetEventSlim until) => _hangs[root] = until;

            public void Throw(string root) => _throws.Add(root);

            public DriveProbeResult Probe(string root)
            {
                if (_hangs.TryGetValue(root, out var gate)) gate.Wait();
                if (_throws.Contains(root)) throw new IOException("the drive is on fire");
                return _answers.TryGetValue(root, out var answer) ? answer : DriveProbeResult.Unavailable;
            }
        }
    }
}
