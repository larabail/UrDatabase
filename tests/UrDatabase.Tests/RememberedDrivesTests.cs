using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Every disk, not only the library's: which plugged-in volumes are listed, which are
    /// remembered, how a remembered disk is recognised after Windows gives it another letter, and
    /// how one that has died — and so simply stopped being listed — is noticed and later forgotten.
    ///
    /// No test here asks the machine what is plugged in. Every list of drives is a fake, and every
    /// catalogue is a temporary file this class created and deletes.
    /// </summary>
    public class RememberedDrivesTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime LastWeek = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

        private readonly TempLog _log = new();
        private readonly string _root;
        private readonly string _db;

        public RememberedDrivesTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-known-drives-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _db = Path.Combine(_root, "movies.db");
        }

        public void Dispose()
        {
            _log.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- telling one disk from another -----------------------------------------------------

        [Fact]
        public void A_disk_is_known_by_its_serial_number_before_anything_else() =>
            Assert.Equal("serial:1A2B-3C4D", DriveInventory.IdentityOf(Disk("Films", "1A2B-3C4D")));

        [Fact]
        public void Without_a_serial_a_disk_is_known_by_its_name_and_size() =>
            Assert.Equal("volume:Films|4000", DriveInventory.IdentityOf(new DriveProbeResult(true, "Films", 4000, 10)));

        [Fact]
        public void A_disk_with_nothing_to_tell_it_apart_is_not_identified()
        {
            // Identifying it by its letter would mistake whatever is plugged in there next for it.
            Assert.Null(DriveInventory.IdentityOf(new DriveProbeResult(true)));
            Assert.Null(DriveInventory.IdentityOf(new DriveProbeResult(true, null, 4000, 10)));
            Assert.Null(DriveInventory.IdentityOf(DriveProbeResult.Unavailable));
            Assert.Null(DriveInventory.IdentityOf(null));
        }

        // ---- what is listed --------------------------------------------------------------------

        [Fact]
        public void Every_disk_plugged_in_is_listed_and_remembered_not_only_the_librarys()
        {
            // The report this was written for: the dialog showed the five drives with films on them
            // and none of the other dozen disks Explorer showed.
            var mounted = new[]
            {
                Fixed(@"C:\"), Fixed(@"D:\"), Fixed(@"H:\"), Fixed(@"K:\"), Fixed(@"U:\")
            };
            var probes = Probes(
                (@"C:\", Disk(null, "0001-0001")),
                (@"D:\", Disk("E_SSD1", "0002-0002")),
                (@"H:\", Disk("L_SSD4", "0003-0003")),
                (@"K:\", Disk("K3.0_12TB", "0004-0004")),
                (@"U:\", Disk(null, "0005-0005")));
            var usages = new[] { new DriveUsage(@"D:\", new[] { @"D:\MV" }, 126) };

            var result = DriveInventory.Build(mounted, probes, usages, null, canRemember: true, Now);

            Assert.Equal(new[] { @"C:\", @"D:\", @"H:\", @"K:\", @"U:\" }, result.Reports.Select(r => r.Root));
            Assert.All(result.Reports, r => Assert.True(r.Remembered));
            Assert.Equal(5, result.Seen.Count);

            var d = result.Reports.Single(r => r.Root == @"D:\");
            Assert.Equal(126, d.FilmCount);
            Assert.Equal(new[] { @"D:\MV" }, d.WatchFolders);
            Assert.Equal("E_SSD1", d.Label);

            var k = result.Seen.Single(s => s.LastRoot == @"K:\");
            Assert.Equal("serial:0004-0004", k.Id);
            Assert.Equal("K3.0_12TB", k.Label);
            Assert.Equal(Now, k.LastSeenUtc);
        }

        [Fact]
        public void A_cloud_drive_is_listed_but_never_remembered()
        {
            // Google Drive for desktop mounts a letter and calls it a fixed disk. It is there only
            // while that app runs, so remembering it would cry wolf every time the app was quit.
            var result = DriveInventory.Build(
                new[] { Fixed(@"Q:\") },
                Probes((@"Q:\", new DriveProbeResult(true, "Google Drive", 1000, 500, "ABCD-0001", "FAT32"))),
                null, null, canRemember: true, Now);

            var q = Assert.Single(result.Reports);
            Assert.Equal(DriveKind.Virtual, q.Kind);
            Assert.False(q.Remembered);
            Assert.Empty(result.Seen);
            Assert.Contains("Cloud or virtual drive", DriveStatusText.Describe(result.Reports));
        }

        [Fact]
        public void An_empty_card_reader_or_a_disconnected_share_is_not_listed_at_all()
        {
            var result = DriveInventory.Build(
                new[]
                {
                    new MountedVolume(@"F:\", DriveKind.Removable),
                    new MountedVolume(@"Z:\", DriveKind.Network),
                    new MountedVolume(@"X:\", DriveKind.Optical)
                },
                Probes(
                    (@"F:\", DriveProbeResult.Unavailable),
                    (@"Z:\", DriveProbeResult.Unavailable),
                    (@"X:\", DriveProbeResult.Unavailable)),
                null, null, canRemember: true, Now);

            Assert.Empty(result.Reports);
            Assert.Empty(result.Seen);
        }

        [Fact]
        public void A_stick_or_a_share_that_is_plugged_in_is_listed_but_not_remembered()
        {
            var result = DriveInventory.Build(
                new[] { new MountedVolume(@"F:\", DriveKind.Removable), new MountedVolume(@"Z:\", DriveKind.Network) },
                Probes((@"F:\", Disk("STICK", "1111-1111")), (@"Z:\", Disk("share", "2222-2222"))),
                null, null, canRemember: true, Now);

            Assert.Equal(2, result.Reports.Count);
            Assert.All(result.Reports, r => Assert.False(r.Remembered));
            Assert.Empty(result.Seen);

            var text = DriveStatusText.Describe(result.Reports);
            Assert.Contains("Removable drive; not reported at launch.", text);
            Assert.Contains("Network drive; not reported at launch.", text);
        }

        [Fact]
        public void A_removable_drive_the_library_lives_on_is_remembered()
        {
            // Some USB enclosures call a hard disk removable. One holding films is plainly a disk.
            var result = DriveInventory.Build(
                new[] { new MountedVolume(@"F:\", DriveKind.Removable) },
                Probes((@"F:\", Disk("Films", "1111-1111"))),
                new[] { new DriveUsage(@"F:\", Array.Empty<string>(), 12) },
                null, canRemember: true, Now);

            Assert.True(Assert.Single(result.Reports).Remembered);
            Assert.Equal("serial:1111-1111", Assert.Single(result.Seen).Id);
        }

        [Fact]
        public void The_same_disk_under_two_letters_is_remembered_once()
        {
            var result = DriveInventory.Build(
                new[] { Fixed(@"C:\"), Fixed(@"S:\") },
                Probes((@"C:\", Disk(null, "0001-0001")), (@"S:\", Disk(null, "0001-0001"))),
                null, null, canRemember: true, Now);

            Assert.Equal(@"C:\", Assert.Single(result.Seen).LastRoot);
            Assert.Null(result.Reports.Single(r => r.Root == @"S:\").Id);
        }

        // ---- missing a disk --------------------------------------------------------------------

        [Fact]
        public void A_remembered_disk_that_is_no_longer_listed_is_reported_missing()
        {
            var known = new[] { Known("serial:0004-0004", "K3.0_12TB", @"K:\") };

            var result = DriveInventory.Build(
                new[] { Fixed(@"C:\") },
                Probes((@"C:\", Disk(null, "0001-0001"))),
                null, known, canRemember: true, Now);

            var k = result.Reports.Single(r => r.Root == @"K:\");
            Assert.Equal(DriveState.Disconnected, k.State);
            Assert.Equal("K3.0_12TB", k.Label);
            Assert.True(k.Remembered);
            Assert.True(k.RaisesAlarm);
            Assert.True(k.CanForget);
            Assert.Equal(LastWeek, k.LastSeenUtc);

            // Missing, so not seen: its last sighting must not be moved to today.
            Assert.DoesNotContain(result.Seen, s => s.Id == "serial:0004-0004");

            Assert.Equal(@"K:\ (K3.0_12TB) is not connected", DriveStatusText.Headline(result.Reports));
            Assert.Equal(
                "Nothing in your library is on it. If it has been retired, forget it in Details.",
                DriveStatusText.Detail(result.Reports));
        }

        [Fact]
        public void A_disk_given_another_letter_is_recognised_and_not_reported_missing()
        {
            var known = new[] { Known("serial:0004-0004", "K3.0_12TB", @"K:\") };

            var result = DriveInventory.Build(
                new[] { Fixed(@"M:\") },
                Probes((@"M:\", Disk("K3.0_12TB", "0004-0004"))),
                null, known, canRemember: true, Now);

            var m = Assert.Single(result.Reports);
            Assert.Equal(@"M:\", m.Root);
            Assert.Equal(DriveState.Connected, m.State);
            Assert.Null(DriveStatusText.Headline(result.Reports));

            // Looked for at the new letter from now on.
            Assert.Equal(@"M:\", Assert.Single(result.Seen).LastRoot);
        }

        [Fact]
        public void A_different_disk_at_a_remembered_letter_does_not_hide_that_the_old_one_is_gone()
        {
            var known = new[] { Known("serial:0004-0004", "K3.0_12TB", @"K:\") };

            var result = DriveInventory.Build(
                new[] { Fixed(@"K:\") },
                Probes((@"K:\", Disk("NEW", "9999-9999"))),
                null, known, canRemember: true, Now);

            Assert.Equal(2, result.Reports.Count);
            Assert.Contains(result.Reports, r => r.Label == "NEW" && r.State == DriveState.Connected);
            Assert.Contains(result.Reports, r => r.Label == "K3.0_12TB" && r.State == DriveState.Disconnected);
            Assert.Equal(@"K:\ (K3.0_12TB) is not connected", DriveStatusText.Headline(result.Reports));
        }

        [Fact]
        public void A_remembered_disk_listed_at_its_letter_but_not_answering_is_one_line_not_two()
        {
            var known = new[] { Known("serial:0004-0004", "K3.0_12TB", @"K:\") };

            var result = DriveInventory.Build(
                new[] { Fixed(@"K:\") },
                new Dictionary<string, DriveProbeOutcome>(StringComparer.OrdinalIgnoreCase)
                {
                    [@"K:\"] = new(DriveState.NotResponding, DriveProbeResult.Unavailable)
                },
                null, known, canRemember: true, Now);

            var k = Assert.Single(result.Reports);
            Assert.Equal(DriveState.NotResponding, k.State);
            Assert.Equal("K3.0_12TB", k.Label);
            Assert.True(k.RaisesAlarm);
            Assert.Equal(@"K:\ (K3.0_12TB) is not responding", DriveStatusText.Headline(result.Reports));
        }

        [Fact]
        public void A_missing_library_disk_is_one_line_with_its_films_and_its_name()
        {
            var known = new[] { Known("serial:0003-0003", "E3.0_6TB", @"E:\") };

            var result = DriveInventory.Build(
                null,
                Probes((@"E:\", DriveProbeResult.Unavailable)),
                new[] { new DriveUsage(@"E:\", new[] { @"E:\MV" }, 507) },
                known, canRemember: true, Now);

            var e = Assert.Single(result.Reports);
            Assert.Equal("E3.0_6TB", e.Label);
            Assert.Equal(507, e.FilmCount);
            Assert.True(e.Remembered);
            Assert.Equal(@"E:\ (E3.0_6TB) is not connected", DriveStatusText.Headline(result.Reports));
            Assert.Equal("507 films on it cannot be played until it is back.", DriveStatusText.Detail(result.Reports));
        }

        [Fact]
        public void Without_a_catalogue_nothing_is_remembered_and_nothing_claims_it_will_be_missed()
        {
            var result = DriveInventory.Build(
                new[] { Fixed(@"D:\") },
                Probes((@"D:\", Disk("E_SSD1", "0002-0002"))),
                null, null, canRemember: false, Now);

            Assert.Empty(result.Seen);
            Assert.False(Assert.Single(result.Reports).Remembered);
        }

        [Fact]
        public void A_share_the_library_lives_on_still_raises_the_alarm_without_being_remembered()
        {
            var result = DriveInventory.Build(
                null,
                new Dictionary<string, DriveProbeOutcome>(StringComparer.OrdinalIgnoreCase)
                {
                    [@"\\nas\movies\"] = new(DriveState.NotResponding, DriveProbeResult.Unavailable)
                },
                new[] { new DriveUsage(@"\\nas\movies\", new[] { @"\\nas\movies\Films" }, 3) },
                null, canRemember: true, Now);

            var nas = Assert.Single(result.Reports);
            Assert.False(nas.Remembered);
            Assert.False(nas.CanForget);
            Assert.True(nas.RaisesAlarm);
        }

        // ---- what is said ----------------------------------------------------------------------

        [Fact]
        public void A_missing_disk_says_when_it_was_last_seen()
        {
            var missing = new DriveReport(@"K:\", DriveState.Disconnected, Array.Empty<string>(), 0,
                "K3.0_12TB", 12_000_000_000_000, null, "serial:0004-0004", null, Remembered: true, LastSeenUtc: LastWeek);

            var lines = DriveStatusText.Lines(missing);

            Assert.Contains(lines, l => l.StartsWith("Last seen 18 Oct 2026", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("Forget it if it has been retired", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("disk", StringComparison.Ordinal));
        }

        [Fact]
        public void The_dialog_lists_the_alarming_drives_before_the_quietly_absent_ones()
        {
            var reports = new[]
            {
                new DriveReport(@"C:\", DriveState.Connected, Array.Empty<string>(), 0, Remembered: true, Id: "a"),
                new DriveReport(@"A:\", DriveState.Disconnected, Array.Empty<string>(), 2),
                new DriveReport(@"K:\", DriveState.Disconnected, Array.Empty<string>(), 0, Remembered: true, Id: "k")
            };

            Assert.Equal(new[] { @"K:\", @"A:\", @"C:\" }, DriveStatusText.Ordered(reports).Select(r => r.Root));
        }

        [Fact]
        public void A_mac_disk_is_not_named_twice() =>
            Assert.Equal("/Volumes/Films", DriveStatusText.Name(
                new DriveReport("/Volumes/Films", DriveState.Connected, Array.Empty<string>(), 0, "Films")));

        // ---- which mounts are drives -----------------------------------------------------------

        [Theory]
        [InlineData("/", true)]
        [InlineData("/Volumes/Films", true)]
        [InlineData("/Volumes/Films/", true)]
        [InlineData("/media/ana/Films", true)]
        [InlineData("/mnt/films", true)]
        [InlineData("/System/Volumes/Data", false)]
        [InlineData("/dev", false)]
        [InlineData("/private/var/vm", false)]
        [InlineData("/Volumes/.timemachine", false)]
        [InlineData("/Volumes/com.apple.TimeMachine.localsnapshots", false)]
        [InlineData("", false)]
        public void Only_a_mount_that_is_a_volume_in_its_own_right_counts_as_a_drive(string mount, bool expected) =>
            Assert.Equal(expected, DriveEnumeration.IsUnixVolumeMount(mount));

        [Fact]
        public void Disk_images_are_read_from_what_hdiutil_prints()
        {
            const string output =
                "framework       : 671\n" +
                "================================================\n" +
                "image-path      : /Users/ana/Downloads/UrDatabase-0.31.0-osx-arm64.dmg\n" +
                "/dev/disk4          \tGUID_partition_scheme          \t\n" +
                "/dev/disk4s1        \tApple_HFS                      \t/Volumes/UrDatabase\n" +
                "/dev/disk5s1        \t41504653-0000-11AA-AA11-00306543ECAC\t/Volumes/My Old Backup/\n";

            Assert.Equal(
                new[] { "/Volumes/My Old Backup", "/Volumes/UrDatabase" },
                DriveEnumeration.ParseDiskImageMounts(output).OrderBy(m => m, StringComparer.Ordinal));
            Assert.Empty(DriveEnumeration.ParseDiskImageMounts(null));
        }

        [Theory]
        [InlineData(DriveType.Fixed, DriveKind.Fixed)]
        [InlineData(DriveType.Removable, DriveKind.Removable)]
        [InlineData(DriveType.Network, DriveKind.Network)]
        [InlineData(DriveType.CDRom, DriveKind.Optical)]
        [InlineData(DriveType.Ram, DriveKind.Virtual)]
        [InlineData(DriveType.Unknown, DriveKind.Virtual)]
        public void The_operating_systems_drive_types_map_to_kinds(DriveType type, DriveKind kind) =>
            Assert.Equal(kind, DriveEnumeration.KindOf(type));

        [Fact]
        public void A_letter_with_nothing_behind_it_is_not_a_drive() =>
            Assert.Null(DriveEnumeration.KindOf(DriveType.NoRootDirectory));

        [Theory]
        [InlineData("Google Drive", "FAT32", true)]
        [InlineData("My stuff", "GoogleDriveFS", true)]
        [InlineData("GoogleDrive", "dfsfuse_DFS", true)]
        [InlineData("E3.0_6TB", "NTFS", false)]
        [InlineData(null, "apfs", false)]
        public void A_cloud_drive_is_told_from_a_disk(string? label, string? format, bool expected) =>
            Assert.Equal(expected, DriveEnumeration.LooksVirtual(label, format));

        // ---- the catalogue ---------------------------------------------------------------------

        [Fact]
        public void A_disk_seen_again_keeps_when_it_was_first_seen_and_moves_to_its_new_letter()
        {
            using (var conn = Database.Open(_db))
            {
                KnownDriveStore.Remember(conn, new[] { Known("serial:1", "Films", @"E:\") });
                KnownDriveStore.Remember(conn, new[] { new KnownDrive("serial:1", "Films", @"H:\", 4000, Now) });

                var drive = Assert.Single(KnownDriveStore.Load(conn));
                Assert.Equal(@"H:\", drive.LastRoot);
                Assert.Equal(4000, drive.TotalBytes);
                Assert.Equal(Now, drive.LastSeenUtc);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT first_seen_at FROM known_drives";
                Assert.StartsWith("2026-10-18", (string)cmd.ExecuteScalar()!);
            }
        }

        [Fact]
        public void A_forgotten_disk_is_gone_from_the_catalogue()
        {
            using var conn = Database.Open(_db);
            KnownDriveStore.Remember(conn, new[] { Known("serial:1", "Films", @"E:\"), Known("serial:2", "Old", @"F:\") });

            KnownDriveStore.Forget(conn, "serial:2");

            Assert.Equal("serial:1", Assert.Single(KnownDriveStore.Load(conn)).Id);
        }

        [Fact]
        public void A_catalogue_from_before_remembered_disks_reads_as_remembering_none()
        {
            using (var conn = new SqliteConnection($"Data Source={_db}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE files (id INTEGER PRIMARY KEY, movie_id INTEGER, file_path TEXT NOT NULL, missing_since TEXT)";
                cmd.ExecuteNonQuery();
            }

            Assert.Empty(DriveMonitor.ReadKnownDrives(_db));
        }

        // ---- the whole check, end to end -------------------------------------------------------

        [Fact]
        public async Task A_disk_that_dies_is_missed_at_the_next_launch_until_it_is_forgotten()
        {
            using (Database.Open(_db)) { }

            var drives = new FakeDrives(Fixed(@"C:\"), Fixed(@"K:\"));
            var probe = new FakeProbe
            {
                [@"C:\"] = Disk(null, "0001-0001"),
                [@"K:\"] = Disk("K3.0_12TB", "0004-0004")
            };
            var monitor = new DriveMonitor(probe, drives: drives, clock: () => Now);

            // First launch: both plugged in, both remembered, nothing to say.
            var first = await monitor.CheckDrivesAsync(null, _db);
            Assert.Equal(2, first.Count);
            Assert.Null(DriveStatusText.Headline(first));
            Assert.Equal(2, DriveMonitor.ReadKnownDrives(_db).Count);

            // The disk dies. Windows does not report it missing; it just stops listing it.
            drives.Mounted = new[] { Fixed(@"C:\") };

            var second = await monitor.CheckDrivesAsync(null, _db);
            Assert.Equal(@"K:\ (K3.0_12TB) is not connected", DriveStatusText.Headline(second));
            var dead = second.Single(r => r.IsDown);
            Assert.True(dead.CanForget);

            // The owner says it is retired.
            await DriveMonitor.ForgetAsync(_db, dead.Id!);

            var third = await monitor.CheckDrivesAsync(null, _db);
            Assert.Null(DriveStatusText.Headline(third));
            Assert.Equal(@"C:\", Assert.Single(third).Root);
        }

        [Fact]
        public async Task Checking_without_a_catalogue_lists_the_disks_but_remembers_none_and_creates_nothing()
        {
            var db = Path.Combine(_root, "never", "movies.db");
            var monitor = new DriveMonitor(
                new FakeProbe { [@"D:\"] = Disk("E_SSD1", "0002-0002") },
                drives: new FakeDrives(Fixed(@"D:\")));

            var reports = await monitor.CheckDrivesAsync(null, db);

            Assert.False(Assert.Single(reports).Remembered);
            Assert.False(Directory.Exists(Path.GetDirectoryName(db)));
        }

        [Fact]
        public async Task A_listing_that_fails_still_checks_the_library_drives()
        {
            var monitor = new DriveMonitor(
                new FakeProbe { [@"E:\"] = DriveProbeResult.Unavailable },
                drives: new ThrowingDrives());

            var reports = await monitor.CheckDrivesAsync(new[] { @"E:\MV" }, _db);

            Assert.Equal(@"E:\ is not connected", DriveStatusText.Headline(reports));
        }

        [Fact]
        public async Task A_listing_that_never_finishes_does_not_hold_up_the_check()
        {
            using var never = new ManualResetEventSlim(false);
            try
            {
                var monitor = new DriveMonitor(
                    new FakeProbe { [@"E:\"] = new DriveProbeResult(true) },
                    TimeSpan.FromSeconds(1),
                    new HangingDrives(never));

                var started = DateTime.UtcNow;
                var reports = await monitor.CheckDrivesAsync(new[] { @"E:\MV" }, _db);

                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
                Assert.Equal(DriveState.Connected, Assert.Single(reports).State);
            }
            finally
            {
                never.Set();
            }
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static MountedVolume Fixed(string root) => new(root, DriveKind.Fixed);

        private static DriveProbeResult Disk(string? label, string serial) =>
            new(true, label, 4_000_000_000_000, 1_000_000_000_000, serial, "NTFS");

        private static KnownDrive Known(string id, string label, string root) =>
            new(id, label, root, 12_000_000_000_000, LastWeek);

        private static Dictionary<string, DriveProbeOutcome> Probes(params (string Root, DriveProbeResult Result)[] probes)
        {
            var map = new Dictionary<string, DriveProbeOutcome>(StringComparer.OrdinalIgnoreCase);
            foreach (var (root, result) in probes)
                map[root] = result.IsAvailable ? new(DriveState.Connected, result) : DriveProbeOutcome.Away;
            return map;
        }

        private sealed class FakeDrives : IDriveEnumerator
        {
            public FakeDrives(params MountedVolume[] mounted) => Mounted = mounted;

            public IReadOnlyList<MountedVolume> Mounted { get; set; }

            public IReadOnlyList<MountedVolume> Enumerate() => Mounted;
        }

        private sealed class ThrowingDrives : IDriveEnumerator
        {
            public IReadOnlyList<MountedVolume> Enumerate() => throw new IOException("no");
        }

        private sealed class HangingDrives : IDriveEnumerator
        {
            private readonly ManualResetEventSlim _until;

            public HangingDrives(ManualResetEventSlim until) => _until = until;

            public IReadOnlyList<MountedVolume> Enumerate()
            {
                _until.Wait();
                return Array.Empty<MountedVolume>();
            }
        }

        private sealed class FakeProbe : IDriveProbe
        {
            private readonly Dictionary<string, DriveProbeResult> _answers = new(StringComparer.OrdinalIgnoreCase);

            public DriveProbeResult this[string root] { set => _answers[root] = value; }

            public DriveProbeResult Probe(string root) =>
                _answers.TryGetValue(root, out var answer) ? answer : DriveProbeResult.Unavailable;
        }
    }
}
