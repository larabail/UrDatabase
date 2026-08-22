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
        private readonly Action<string>? _onFailure;
        private readonly ConcurrentDictionary<long, byte> _inflight = new();
        private volatile bool _disposed;

        /// <param name="onFailure">
        /// Told about a poster this could not finish, once per failure, with a message fit to put
        /// in front of somebody. Optional, and the log is written either way — but a loader given
        /// no callback is back to the behaviour that made this a bug report: posters quietly
        /// missing, and the reason only in a file nobody opens.
        /// </param>
        public PosterAutoLoader(AppConfig cfg, string dbPath, int maxConcurrency = 4, Action<string>? onFailure = null)
        {
            _cfg = cfg;
            _dbPath = dbPath;
            _gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
            _onFailure = onFailure;
        }

        /// <summary>How many fetches could start right now. For tests; the gate itself stays private.</summary>
        internal int AvailableSlots => _gate.CurrentCount;

        public async Task EnsurePosterAsync(long movieId, string title, int? year, Action<string?> onFetched, CancellationToken ct)
        {
            if (_disposed) return;
            if (string.IsNullOrWhiteSpace(_cfg.TmdbApiKey)) return;
            if (!_inflight.TryAdd(movieId, 0)) return;

            // Tracked rather than assumed. WaitAsync throws when the token is already cancelled,
            // which happens on window close, and the release below would then hand back a slot
            // that was never taken — inflating the gate past maxConcurrency and letting the next
            // library warm as many concurrent fetches as it liked.
            var acquired = false;

            try
            {
                await _gate.WaitAsync(ct);
                acquired = true;

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

                // Through the lane, not straight at the database. Up to four of these run at once
                // by design, and every one of them is a writer; without a turn to take they queue
                // on the SQLite write lock instead, where losing is reported as an error rather
                // than as a wait.
                await DatabaseWriteLane.RunAsync(conn, async token =>
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE movies SET poster_path=@p WHERE id=@id";
                    cmd.Parameters.AddWithValue("@p", pathToStore ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@id", movieId);
                    await cmd.ExecuteNonQueryAsync(token);
                }, ct);

                onFetched(pathToStore);
            }
            catch (OperationCanceledException)
            {
                // ignore on window close / app exit
            }
            catch (Exception ex)
            {
                AppLog.Write("posters.log", $"movieId={movieId} {ex}");

                _onFailure?.Invoke(DatabaseWriteLane.IsTransientLockFailure(ex)
                    ? $"Could not save the poster for “{title}”: the library was still in use. It will be fetched again next time."
                    : $"Could not fetch the poster for “{title}”: {ex.Message}");
            }
            finally
            {
                if (acquired) _gate.Release();
                _inflight.TryRemove(movieId, out _);
            }
        }

        /// <summary>
        /// Stops new fetches starting. It deliberately does not dispose the gate: a fetch can sit
        /// in the write lane for several seconds, and closing the window under one would have the
        /// release above throw ObjectDisposedException on a task nobody is left to observe.
        /// SemaphoreSlim only needs disposing once AvailableWaitHandle has been touched, and
        /// nothing here touches it, so not disposing removes that race rather than guarding it.
        ///
        /// Fetches already running are left to finish and their results dropped, because the
        /// window they would have updated is gone. Awaiting them here instead is #11.
        /// </summary>
        public void Dispose() => _disposed = true;
    }
}
