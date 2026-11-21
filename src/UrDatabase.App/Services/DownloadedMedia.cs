using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using UrDatabase.Models;

namespace UrDatabase.Services;

public static class DownloadedMedia
{
    public static Task<PlayTarget> FindAsync(string dbPath, string title, int? year,
        IVolumeSnapshotSource? volumes = null, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            using var conn = Database.Open(dbPath);
            var matches = conn.Query<(long Id, string Title, string? ScanTitle)>(
                    "SELECT id, title, scan_title FROM movies WHERE year IS @year", new { year })
                .Where(m => MovieIndex.NormalizeTitle(m.Title) == MovieIndex.NormalizeTitle(title)
                    || MovieIndex.NormalizeTitle(m.ScanTitle) == MovieIndex.NormalizeTitle(title)).ToList();
            if (matches.Count == 0) return PlayTarget.None;
            var paths = conn.Query<string>("SELECT file_path FROM files WHERE movie_id IN @ids",
                new { ids = matches.Select(m => m.Id).ToArray() });
            var snapshot = await (volumes ?? VolumeSnapshotSource.Shared).CaptureAsync(paths, ct);
            PlayTarget? unavailable = null;
            foreach (var movie in matches)
            {
                var target = PlayTargetResolver.Resolve(conn, movie.Id, title, year, volumes: snapshot);
                if (target.Kind == PlayTargetKind.Linked) return target;
                unavailable ??= target;
            }
            return unavailable ?? PlayTarget.None;
        }, ct).WaitAsync(DriveMonitor.DefaultTimeout + TimeSpan.FromSeconds(2), ct);
}
