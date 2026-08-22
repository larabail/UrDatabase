using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>
    /// Fills in the posters a library is missing, a few at a time, and can be told to stop.
    ///
    /// Two things make this more than a loop. The fetches are started from the UI thread and
    /// must not block it, so they run as tasks; and the window that wanted them can close while
    /// they are still running, so somebody has to own them. That owner is this class: it starts
    /// the tasks through <see cref="Queue"/>, keeps hold of them, and <see cref="StopAsync"/>
    /// waits for the ones already running rather than walking away from them.
    /// </summary>
    public sealed class PosterAutoLoader : IDisposable
    {
        /// <summary>
        /// How long a shutdown waits for fetches already in flight before cancelling them.
        ///
        /// A budget, not a promise. Long enough that a request already answered gets its result
        /// written to the database — which is the whole point, since a poster dropped here is
        /// fetched again from scratch on the next launch — and short enough that a window does
        /// not appear to be ignoring the person who closed it.
        /// </summary>
        public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// What a fetch is given to unwind in once the deadline above has passed and its token
        /// has been cancelled. Only long enough to leave the write lane and close a connection.
        /// </summary>
        private static readonly TimeSpan CancellationGrace = TimeSpan.FromMilliseconds(500);

        private readonly AppConfig _cfg;
        private readonly string _dbPath;
        private readonly SemaphoreSlim _gate;
        private readonly Action<string>? _onFailure;
        private readonly ConcurrentDictionary<long, byte> _inflight = new();
        private readonly ConcurrentDictionary<Task, byte> _queued = new();
        private readonly CancellationTokenSource _stopping = new();

        /// <summary>
        /// One TMDB client for the whole library, rather than one per poster.
        ///
        /// Each <see cref="TmdbService"/> owns an <see cref="HttpClient"/>, and one was being
        /// built and thrown away for every film. A client abandoned that way keeps its
        /// connections in TIME_WAIT for minutes after it is collected, so a few hundred films
        /// meant a few hundred sockets — the ordinary way to exhaust the ephemeral port range
        /// and have a machine start refusing connections it has no other reason to refuse.
        /// </summary>
        private readonly TmdbService _tmdb;

        private int _active;
        private int _tmdbDisposed;
        private volatile bool _disposed;

        /// <param name="onFailure">
        /// Told about a poster this could not finish, once per failure, with a message fit to put
        /// in front of somebody. Optional, and the log is written either way — but a loader given
        /// no callback is back to the behaviour that made this a bug report: posters quietly
        /// missing, and the reason only in a file nobody opens.
        /// </param>
        /// <param name="handler">
        /// The seam for tests, handed to the shared <see cref="TmdbService"/>. Left null in the
        /// app, which is the only place a real network is wanted.
        /// </param>
        public PosterAutoLoader(
            AppConfig cfg,
            string dbPath,
            int maxConcurrency = 4,
            Action<string>? onFailure = null,
            HttpMessageHandler? handler = null)
        {
            _cfg = cfg;
            _dbPath = dbPath;
            _gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
            _onFailure = onFailure;

            _tmdb = new TmdbService(
                apiKey: cfg.TmdbApiKey ?? "",
                posterCacheDir: cfg.PosterCacheDir ?? "",
                imageSize: cfg.TmdbImageSize ?? "w342",
                downloadPosters: cfg.DownloadPosters,
                handler: handler);
        }

        /// <summary>How many fetches could start right now. For tests; the gate itself stays private.</summary>
        internal int AvailableSlots => _gate.CurrentCount;

        /// <summary>How many queued fetches have not finished yet. For tests.</summary>
        internal int Pending => _queued.Count;

        /// <summary>
        /// Starts a fetch and keeps hold of it, so that <see cref="StopAsync"/> has something to
        /// wait for. This is what a caller on the UI thread wants: the alternative, discarding
        /// the task, is how a closing window came to abandon work it had started — invisibly,
        /// since nothing was left holding the task to notice it had been dropped.
        /// </summary>
        public void Queue(long movieId, string title, int? year, Action<string?> onFetched, CancellationToken ct)
        {
            if (_disposed) return;

            Track(EnsurePosterAsync(movieId, title, year, onFetched, ct));
        }

        /// <summary>
        /// Records a task until it finishes. The continuation is attached after the task is in
        /// the map, so one that completed while this was running still takes itself out again.
        /// </summary>
        private void Track(Task task)
        {
            if (task.IsCompleted) return;

            _queued[task] = 0;

            _ = task.ContinueWith(
                static (finished, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(finished, out _),
                _queued,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

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

            // The loader's own token joins the caller's, so a shutdown that has run out of
            // patience can cut a fetch short without the caller having to know it exists.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
            var token = linked.Token;

            // Last, so that nothing between here and the try below can leave the count raised.
            Interlocked.Increment(ref _active);

            try
            {
                await _gate.WaitAsync(token);
                acquired = true;

                using var conn = Database.Open(_dbPath);

                // SAFE read of poster_path (it may be NULL/DBNull)
                string? existing;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT poster_path FROM movies WHERE id=@id";
                    cmd.Parameters.AddWithValue("@id", movieId);
                    var o = await cmd.ExecuteScalarAsync(token);
                    existing = (o == null || o is DBNull) ? null : Convert.ToString(o);
                }

                if (!string.IsNullOrWhiteSpace(existing))
                {
                    onFetched(existing);
                    return;
                }

                var (_, posterPath) = await _tmdb.SearchPosterAsync(title, year, token);
                if (string.IsNullOrWhiteSpace(posterPath)) return;

                string? pathToStore;
                var url = _tmdb.BuildImageUrlPublic(posterPath!);

                if (_cfg.DownloadPosters)
                {
                    // download; if it fails, fall back to URL so UI can still load online
                    pathToStore = await _tmdb.DownloadForPublic(url, $"{movieId}.jpg", token) ?? url;
                }
                else
                {
                    pathToStore = url;
                }

                // Through the lane, not straight at the database. Up to four of these run at once
                // by design, and every one of them is a writer; without a turn to take they queue
                // on the SQLite write lock instead, where losing is reported as an error rather
                // than as a wait.
                await DatabaseWriteLane.RunAsync(conn, async laneToken =>
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE movies SET poster_path=@p WHERE id=@id";
                    cmd.Parameters.AddWithValue("@p", pathToStore ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@id", movieId);
                    await cmd.ExecuteNonQueryAsync(laneToken);
                }, token);

                onFetched(pathToStore);
            }
            catch (OperationCanceledException)
            {
                // ignore on window close / app exit
            }
            catch (Exception ex)
            {
                AppLog.Write("posters.log", $"movieId={movieId} {ex}");

                // Nothing is said about a failure that is only the app closing. The shared client
                // is released once the last fetch is out, so one that lost that race fails on a
                // disposed client — true, useless, and about a window that has already gone.
                if (_disposed) return;

                _onFailure?.Invoke(DatabaseWriteLane.IsTransientLockFailure(ex)
                    ? $"Could not save the poster for “{title}”: the library was still in use. It will be fetched again next time."
                    : $"Could not fetch the poster for “{title}”: {ex.Message}");
            }
            finally
            {
                if (acquired) _gate.Release();
                _inflight.TryRemove(movieId, out _);

                if (Interlocked.Decrement(ref _active) == 0 && _disposed) ReleaseClient();
            }
        }

        /// <summary>
        /// Waits for everything queued so far, for up to <paramref name="timeout"/>. False means
        /// some of it was still running when that ran out.
        /// </summary>
        public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            var elapsed = Stopwatch.StartNew();

            while (true)
            {
                var pending = _queued.Keys.ToArray();
                if (pending.Length == 0) return true;

                var remaining = timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) return false;

                var all = Task.WhenAll(pending);

                using var expiry = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var expired = Task.Delay(remaining, expiry.Token);

                var first = await Task.WhenAny(all, expired).ConfigureAwait(false);
                expiry.Cancel(); // a drain that finished early leaves no timer behind

                if (first != all) return false;

                // Faults are already logged and reported where they happened; a drain asks
                // whether the work stopped, not whether it succeeded.
                try { await all.ConfigureAwait(false); } catch { }

                // Round again: warming a genre as the window closed can have queued more.
            }
        }

        /// <summary>
        /// Stops new fetches and waits for the ones already running, then lets the shared client
        /// go. False means the wait ran out and the stragglers had to be cancelled.
        ///
        /// That wait is deliberately uncancelled to begin with. A fetch TMDB has already answered
        /// is one database write away from being useful forever, and cancelling it at that point
        /// throws the answer away and asks the same question again on the next launch.
        /// </summary>
        public async Task<bool> StopAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        {
            _disposed = true;

            var settled = await DrainAsync(timeout ?? DefaultStopTimeout, ct).ConfigureAwait(false);

            if (!settled)
            {
                _stopping.Cancel();
                await DrainAsync(CancellationGrace, CancellationToken.None).ConfigureAwait(false);
            }

            if (Volatile.Read(ref _active) == 0) ReleaseClient();

            return settled;
        }

        /// <summary>
        /// Disposes the shared TMDB client once, and only once, when the last fetch is out — so
        /// a request in flight is never pulled out from under itself.
        /// </summary>
        private void ReleaseClient()
        {
            if (Interlocked.Exchange(ref _tmdbDisposed, 1) != 0) return;

            try { _tmdb.Dispose(); } catch { }
        }

        /// <summary>
        /// Stops new fetches starting, and hands the shared client back once the ones already
        /// running are done. A closing window wants <see cref="StopAsync"/> instead, which is
        /// this with a wait attached; this is for replacing a loader whose configuration has
        /// changed, where there is nobody to wait.
        ///
        /// It deliberately does not dispose the gate: a fetch can sit in the write lane for
        /// several seconds, and closing the window under one would have the release above throw
        /// ObjectDisposedException on a task nobody is left to observe. SemaphoreSlim only needs
        /// disposing once AvailableWaitHandle has been touched, and nothing here touches it, so
        /// not disposing removes that race rather than guarding it. The same reasoning covers the
        /// token source: it is cancelled and never disposed, so a fetch that reads its token a
        /// moment too late gets a cancelled token rather than an exception.
        /// </summary>
        public void Dispose()
        {
            _disposed = true;

            if (Volatile.Read(ref _active) == 0) ReleaseClient();
        }
    }
}
