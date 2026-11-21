using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;

namespace UrDatabase.Tests;

internal sealed class TestVolumes : IVolumeSnapshotSource
{
    public VolumeSnapshot Snapshot { get; set; }
    public int Captures { get; private set; }
    public TestVolumes(string root, string id = "TEST-A") =>
        Snapshot = new(new[] { Mount(root, id) }, true);
    public static VolumeMount Mount(string root, string id) => new(root, DriveKind.Fixed,
        new(DriveState.Connected, new(true, "Synthetic disk", 1000, 500, id)));
    public Task<VolumeSnapshot> CaptureAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Captures++;
        return Task.FromResult(Snapshot);
    }
    public Task ConfirmAsync(SqliteConnection conn, string path) =>
        FileLocations.ConfirmNewRootAsync(conn, "watch", path, path, Snapshot);
}
