using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public sealed class VolumeLocationTests : IDisposable
{
    private readonly TempLog _log = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "urdb-volumes-" + Guid.NewGuid().ToString("N"));
    public VolumeLocationTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(@"D:\Films\One.mkv", "Films/One.mkv", @"Q:\", @"Q:\Films\One.mkv")]
    [InlineData("/Volumes/Films/Movies/One.mkv", "Movies/One.mkv", "/Volumes/Renamed 1", "/Volumes/Renamed 1/Movies/One.mkv")]
    public void Volume_resolution_is_platform_independent(string old, string relative, string mounted, string expected)
    {
        var binding = new LocationBinding("volume", old, "serial:A", relative,
            VolumePaths.RelativeKey(relative, VolumePaths.Style(old)), VolumePaths.Style(old));
        var snapshot = new VolumeSnapshot(new[] { TestVolumes.Mount(mounted, "A") }, true);
        var result = FileLocations.Resolve(binding, snapshot, _ => true, (_, _) => { });
        Assert.True(result.Available);
        Assert.Equal(expected, result.Path);
    }

    [Theory]
    [InlineData("away", LocationState.Disconnected)]
    [InlineData("different", LocationState.WrongDisk)]
    [InlineData("duplicate", LocationState.Ambiguous)]
    [InlineData("no-id", LocationState.IdentityUnavailable)]
    [InlineData("timeout", LocationState.TimedOut)]
    [InlineData("incomplete", LocationState.TimedOut)]
    public void A_bound_file_never_falls_back_to_the_old_path(string scenario, LocationState expected)
    {
        var old = Path.Combine(_root, "old");
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(other);
        var file = Path.Combine(old, "One.mkv");
        File.WriteAllText(file, "wrong or unverified occupant");
        var binding = new LocationBinding("volume", file, "serial:A", "One.mkv", "One.mkv", "posix");
        var mounts = scenario switch
        {
            "away" => Array.Empty<VolumeMount>(),
            "different" => new[] { TestVolumes.Mount(old, "B") },
            "duplicate" => new[] { TestVolumes.Mount(old, "A"), TestVolumes.Mount(other, "A") },
            "no-id" => new[] { new VolumeMount(old, DriveKind.Fixed, new(DriveState.Connected, new(true, "Same label", 1000))) },
            "timeout" => new[] { new VolumeMount(old, DriveKind.Fixed, new(DriveState.NotResponding, DriveProbeResult.Unavailable)) },
            _ => new[] { TestVolumes.Mount(old, "A") }
        };
        var result = FileLocations.Resolve(binding, new(mounts, scenario != "incomplete"));
        Assert.Equal(expected, result.State);
        Assert.Null(result.Path);
    }

    [Fact]
    public void Labels_and_capacity_cannot_bind_a_file()
    {
        var snapshot = new VolumeSnapshot(new[]
        {
            new VolumeMount(_root, DriveKind.Fixed, new(DriveState.Connected, new(true, "Films", 1000, 100)))
        }, true);
        Assert.Throws<IOException>(() => FileLocations.Capture(Path.Combine(_root, "One.mkv"), snapshot));
    }

    [Fact]
    public void Path_only_network_locations_remain_supported_but_require_explicit_consent()
    {
        var path = Path.Combine(_root, "One.mkv");
        File.WriteAllText(path, "synthetic video");
        var snapshot = new VolumeSnapshot(new[] { new VolumeMount(_root, DriveKind.Network,
            new(DriveState.Connected, new(true))) }, true);
        Assert.Throws<IOException>(() => FileLocations.Capture(path, snapshot));
        var binding = FileLocations.Capture(path, snapshot, allowPathOnly: true);
        Assert.Equal("path", binding.Mode);
        Assert.True(FileLocations.Resolve(binding, VolumeSnapshot.Empty).Available);
    }

    [Theory]
    [InlineData("../One.mkv")]
    [InlineData("/Other/One.mkv")]
    [InlineData(@"Q:\One.mkv")]
    [InlineData("Movies/../../One.mkv")]
    public void Invalid_relative_paths_are_rejected_before_any_file_probe(string relative)
    {
        var binding = new LocationBinding("volume", Path.Combine(_root, "One.mkv"), "serial:A", relative, relative, "posix");
        var probed = false;
        var result = FileLocations.Resolve(binding, new(new[] { TestVolumes.Mount(_root, "A") }, true),
            _ => { probed = true; return true; });
        Assert.Equal(LocationState.Invalid, result.State);
        Assert.False(probed);
    }

    [Fact]
    public void Containment_handles_roots_case_and_prefix_lookalikes()
    {
        Assert.True(VolumePaths.TryRelative("/", "/Films/One.mkv", out var relative));
        Assert.Equal("Films/One.mkv", relative);
        Assert.True(VolumePaths.TryRelative(@"D:\Films", @"d:\films\One.mkv", out _));
        Assert.False(VolumePaths.TryRelative("/Films", "/Films Extra/One.mkv", out _));
        Assert.False(VolumePaths.TryRelative("/Films", "/films/One.mkv", out _));
        Assert.Throws<ArgumentException>(() => VolumePaths.Absolute(@"D:Films"));
    }

    [Fact]
    public void Case_rules_come_from_the_volume_not_from_the_host_OS()
    {
        Assert.False(DriveEnumeration.ParseCaseSensitivity("<key>FileSystemPersonality</key><string>APFS</string>"));
        Assert.True(DriveEnumeration.ParseCaseSensitivity("<key>FileSystemPersonality</key><string>Case-sensitive APFS</string>"));
        Assert.Null(DriveEnumeration.ParseCaseSensitivity("not a plist"));
        Assert.Equal(VolumePaths.RelativeKey("Films/One.mkv", "posix-insensitive"),
            VolumePaths.RelativeKey("films/one.mkv", "posix-insensitive"));
        Assert.NotEqual(VolumePaths.RelativeKey("Films/One.mkv", "posix"),
            VolumePaths.RelativeKey("films/one.mkv", "posix"));
        Assert.Equal(@"\\server\share\One.mkv", VolumePaths.Join("//server/share/", "One.mkv"));
    }

    [Fact]
    public void A_link_outside_the_verified_volume_is_not_followed()
    {
        if (OperatingSystem.IsWindows()) return;
        var disk = Path.Combine(_root, "disk");
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(disk);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "One.mkv"), "synthetic video");
        Directory.CreateSymbolicLink(Path.Combine(disk, "linked"), other);
        var binding = new LocationBinding("volume", Path.Combine(disk, "linked", "One.mkv"), "serial:A",
            "linked/One.mkv", "linked/One.mkv", "posix");
        var result = FileLocations.Resolve(binding, new(new[] { TestVolumes.Mount(disk, "A") }, true));
        Assert.Equal(LocationState.Invalid, result.State);
    }

    [Fact]
    public async Task A_new_disk_cannot_steal_an_offline_manual_link_by_name_and_size()
    {
        var a = Path.Combine(_root, "a");
        var b = Path.Combine(_root, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        var first = Path.Combine(a, "Same (2000).mkv");
        File.WriteAllText(first, "same size");
        File.WriteAllText(Path.Combine(b, "Same (2000).mkv"), "same size");
        var volumes = new TestVolumes(a, "A");
        using var conn = Database.Open(Path.Combine(_root, "movies.db"));
        await volumes.ConfirmAsync(conn, a);
        await new ScanService(volumes).ScanAsync(conn, new[] { a });
        var chosen = conn.ExecuteScalar<long>("INSERT INTO movies (title) VALUES ('My manual assignment') RETURNING id");
        PlayTargetResolver.LinkFile(conn, chosen, first, volumes: volumes.Snapshot);
        var originalId = conn.ExecuteScalar<long>("SELECT id FROM files");
        Directory.Move(a, Path.Combine(_root, "offline"));
        volumes.Snapshot = new(new[] { TestVolumes.Mount(b, "B") }, true);
        await volumes.ConfirmAsync(conn, b);
        await new ScanService(volumes).ScanAsync(conn, new[] { a, b });
        Assert.Equal(2, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM files"));
        Assert.Equal(chosen, FileLocations.File(conn, originalId)!.MovieId);
        Assert.True(FileLocations.File(conn, originalId)!.LinkedByHand);
        Assert.Null(FileLocations.File(conn, originalId)!.MissingSince);
    }

    [Fact]
    public async Task A_timed_out_probe_is_shared_until_its_native_call_finishes()
    {
        using var release = new ManualResetEventSlim();
        var probe = new WaitingProbe(release);
        var source = new VolumeSnapshotSource(new Listed(_root), probe, TimeSpan.FromMilliseconds(30));
        try
        {
            var requests = Enumerable.Range(0, 12).Select(_ => source.CaptureAsync(Array.Empty<string>())).ToArray();
            var snapshots = await Task.WhenAll(requests);
            Assert.All(snapshots, s => Assert.Equal(DriveState.NotResponding, Assert.Single(s.Mounts).Probe.State));
            Assert.Equal(1, probe.Calls);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task A_late_probe_result_cannot_identify_the_new_occupant_of_its_root()
    {
        using var release = new ManualResetEventSlim();
        var probe = new ReplacedProbe(release);
        var source = new VolumeSnapshotSource(new Listed(_root), probe, TimeSpan.FromMilliseconds(30));
        try
        {
            var timedOut = await source.CaptureAsync(Array.Empty<string>());
            Assert.Equal(DriveState.NotResponding, Assert.Single(timedOut.Mounts).Probe.State);
            release.Set();
            VolumeSnapshot? current = null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                current = await source.CaptureAsync(Array.Empty<string>());
                Assert.DoesNotContain(current.Mounts, m => m.Id == "serial:OLD");
                if (current.Mounts.Any(m => m.Id == "serial:NEW")) break;
                await Task.Delay(10);
            }
            Assert.Equal("serial:NEW", Assert.Single(current!.Mounts).Id);
            Assert.True(probe.Calls >= 2);
        }
        finally { release.Set(); }
    }

    private sealed class ReplacedProbe(ManualResetEventSlim release) : IDriveProbe
    {
        public int Calls;
        public DriveProbeResult Probe(string root)
        {
            if (Interlocked.Increment(ref Calls) == 1)
            {
                release.Wait();
                return new(true, Serial: "OLD");
            }
            return new(true, Serial: "NEW");
        }
    }

    private sealed class Listed(string root) : IDriveEnumerator
    {
        public IReadOnlyList<MountedVolume> Enumerate() => new[] { new MountedVolume(root, DriveKind.Fixed) };
    }
    private sealed class WaitingProbe(ManualResetEventSlim release) : IDriveProbe
    {
        public int Calls;
        public DriveProbeResult Probe(string root)
        {
            Interlocked.Increment(ref Calls);
            release.Wait();
            return new(true, Serial: "A");
        }
    }
    public void Dispose()
    {
        _log.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
