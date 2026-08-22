using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    public sealed class PosterAutoLoader : IDisposable
    {
        private readonly AppConfig _cfg;
        private readonly string _dbPath;
        private readonly SemaphoreSlim _gate;
        private readonly ConcurrentDictionary<long, byte> _inflight = new();

        public PosterAutoLoader(AppConfig cfg, string dbPath, int maxConcurrency = 4)
        {
            _cfg = cfg;
            _dbPath = dbPath;
            _gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        }

        public async Task EnsurePosterAsync(long movieId, string title, int? year, Action<string?> onFetched, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_cfg.TmdbApiKey)) return;
            if (!_inflight.TryAdd(movieId, 0)) return;

            try
            {
                await _gate.WaitAsync(ct);

                using var conn = Database.Open(_dbPath);

                // SAFE read of poster_path (it may be NULL/DBNull)
                string? existing;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT poster_path FROM movies WHERE id=@id";
                    cmd.Parameters.AddWithValue("@id", movieId);
                    var o = await cmd.ExecuteScalarAsync(ct);
                    existing = (o == null || o is DBNull) ? null : Convert.ToString(o);
                }

                if (!string.IsNullOrWhiteSpace(existing))
                {
                    onFetched(existing);
                    return;
                }

                using var tmdb = new TmdbService(
                    apiKey: _cfg.TmdbApiKey ?? "",
                    posterCacheDir: _cfg.PosterCacheDir ?? "",
                    imageSize: _cfg.TmdbImageSize ?? "w342",
                    downloadPosters: _cfg.DownloadPosters
                );

                var (_, posterPath) = await tmdb.SearchPosterAsync(title, year, ct);
                if (string.IsNullOrWhiteSpace(posterPath)) return;

                string? pathToStore;
                var url = tmdb.BuildImageUrlPublic(posterPath!);

                if (_cfg.DownloadPosters)
                {
                    // download; if it fails, fall back to URL so UI can still load online
                    pathToStore = await tmdb.DownloadForPublic(url, $"{movieId}.jpg", ct) ?? url;
                }
                else
                {
                    pathToStore = url;
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE movies SET poster_path=@p WHERE id=@id";
                    cmd.Parameters.AddWithValue("@p", pathToStore ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@id", movieId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                onFetched(pathToStore);
            }
            catch (OperationCanceledException)
            {
                // ignore on window close / app exit
            }
            catch (Exception ex)
            {
                AppLog.Write("posters.log", $"movieId={movieId} {ex}");
            }
            finally
            {
                _gate.Release();
                _inflight.TryRemove(movieId, out _);
            }
        }

        public void Dispose() => _gate.Dispose();
    }
}
