using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

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
        private readonly int _maxConcurrency;
        private readonly SemaphoreSlim _gate;
        private readonly Action<string>? _onFailure;
        /// <summary>
        /// Films this loader has already taken on since the last explicit refresh.
        /// </summary>
        /// <remarks>
        /// It stopped being a record of what is in flight when the shelves began rebuilding
        /// themselves as genres arrive. Every rebuild offers the loader every film again, and a
        /// set that forgot a film the moment its fetch ended would let each of those rebuilds
        /// re-ask TMDB about every film it had refused to match — which on a few thousand films is
        /// thousands of requests for an answer already known to be "no", and the surest way to be
        /// rate limited out of the ones that would have succeeded.
        ///
        /// Refresh forgets completed attempts, but not work still in flight. Regrouping alone
        /// never retries a failed lookup.
        /// </remarks>
        private readonly object _attemptLock = new();
        private readonly HashSet<long> _attempted = new();
        private readonly HashSet<long> _inFlight = new();
        private enum LookupOutcome { Pending, Matched, Unmatched, Failed }
        private readonly Dictionary<long, LookupOutcome> _outcomes = new();
        private MetadataProgress _progress = new();
        private readonly ConcurrentDictionary<Task, byte> _queued = new();
        private readonly CancellationTokenSource _stopping = new();

        /// <summary>
        /// Films waiting to be looked up, and how many are waiting or being looked up right now.
        ///
        /// The queue is the whole point of this class's shape. <see cref="Queue"/> is called from
        /// a UI event handler, once per film the library is missing a poster for, and it used to
        /// start a task apiece: a library of six thousand films produced several thousand tasks
        /// and as many linked cancellation registrations, all created on the interface thread and
        /// then immediately parked on a semaphore four of them could hold. The work was correctly
        /// limited and the bookkeeping for it was not. Now the request is a record in a queue —
        /// which is what it always was — and a fixed number of workers take turns at it.
        /// </summary>
        private readonly ConcurrentQueue<Request> _pending = new();

        /// <summary>
        /// Films whose length, rating and people are still to be asked for, behind
        /// <see cref="_pending"/>.
        /// </summary>
        /// <remarks>
        /// A queue of its own, and always the second one a worker looks in. The request that
        /// fetches these is extra to the one that finds a film's poster and genres, and on a
        /// library of thousands it would otherwise double the time before the shelves sort
        /// themselves out — which is the wait people actually notice. So artwork and genres go
        /// first for every film, and the filters fill in behind them.
        /// </remarks>
        private readonly ConcurrentQueue<Request> _facts = new();

        /// <summary>Films a facts request has been queued for since the last Refresh, and those still waiting.</summary>
        private readonly HashSet<long> _factsAsked = new();
        private readonly HashSet<long> _factsWaiting = new();

        private int _outstanding;
        private int _workers;

        /// <summary>One film to look up, and who to tell about it.</summary>
        /// <param name="FactsFor">
        /// Set on a request for a film's length, rating and people, to the TMDB film to ask about.
        /// Null on the ordinary lookup.
        /// </param>
        private readonly record struct Request(
            long MovieId,
            string Title,
            int? Year,
            Action<Enrichment> OnFetched,
            CancellationToken Token,
            int? FactsFor = null);

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
            HttpMessageHandler? handler = null,
            TmdbRequestScheduler? requests = null)
        {
            _cfg = cfg;
            _dbPath = dbPath;
            _maxConcurrency = Math.Max(1, maxConcurrency);
            _gate = new SemaphoreSlim(_maxConcurrency);
            _onFailure = onFailure;

            _tmdb = new TmdbService(
                apiKey: cfg.TmdbApiKey ?? "",
                posterCacheDir: cfg.PosterCacheDir ?? "",
                imageSize: cfg.TmdbImageSize ?? "w342",
                downloadPosters: cfg.DownloadPosters,
                handler: handler,
                requests: requests);
        }

        /// <summary>How many fetches could start right now. For tests; the gate itself stays private.</summary>
        internal int AvailableSlots => _gate.CurrentCount;

        /// <summary>How many queued fetches have not finished yet. For tests.</summary>
        internal int Pending => Volatile.Read(ref _outstanding);

        public MetadataProgress Progress
        {
            get
            {
                lock (_attemptLock)
                    return _progress with
                    {
                        RetryAfterSeconds = _progress.Pending > 0 ? _tmdb.RateLimitRetryAfterSeconds : 0
                    };
            }
        }

        /// <summary>Allows another attempt at missing metadata without replacing running workers.</summary>
        public async Task RetryMissingAsync(CancellationToken ct = default)
        {
            await _tmdb.RetryGenreListAsync(ct);
            lock (_attemptLock)
            {
                _attempted.IntersectWith(_inFlight);
                foreach (var id in _outcomes.Where(entry => entry.Value != LookupOutcome.Pending
                                 && !_inFlight.Contains(entry.Key))
                             .Select(entry => entry.Key).ToArray())
                    _outcomes.Remove(id);
                _progress = new MetadataProgress(
                    _outcomes.Count(entry => entry.Value == LookupOutcome.Pending),
                    _outcomes.Count(entry => entry.Value == LookupOutcome.Matched),
                    _outcomes.Count(entry => entry.Value == LookupOutcome.Unmatched),
                    _outcomes.Count(entry => entry.Value == LookupOutcome.Failed));

                _factsAsked.IntersectWith(_factsWaiting);
            }
        }

        /// <summary>
        /// Whether this loader has already queued, or ruled out, a request for a film's length,
        /// rating and people since the last Refresh. Lets the window stop offering a film that is
        /// complete in every other respect each time the shelves are rebuilt.
        /// </summary>
        public bool HasAskedForFacts(long movieId)
        {
            lock (_attemptLock)
                return _factsAsked.Contains(movieId);
        }

        /// <summary>
        /// Queues the request for a film's length, rating and people, once per film until Refresh.
        /// Called by a worker that has just learned which TMDB film this is, or found it already
        /// knew and that these were all that was missing.
        /// </summary>
        private void QueueFacts(long movieId, int tmdbId, Action<Enrichment> onFetched, CancellationToken ct)
        {
            if (_disposed) return;

            lock (_attemptLock)
            {
                if (!_factsAsked.Add(movieId)) return;
                _factsWaiting.Add(movieId);
            }

            Interlocked.Increment(ref _outstanding);
            _facts.Enqueue(new Request(movieId, "", null, onFetched, ct, FactsFor: tmdbId));

            StartWorkerIfNeeded();
        }

        /// <summary>Records that a film cannot be asked about, so the window stops offering it.</summary>
        private void RuleOutFacts(long movieId)
        {
            lock (_attemptLock)
                _factsAsked.Add(movieId);
        }

        private void RegisterProgress(long movieId)
        {
            lock (_attemptLock)
                if (_outcomes.TryAdd(movieId, LookupOutcome.Pending))
                    _progress = _progress with { Pending = _progress.Pending + 1 };
        }

        private void CompleteProgress(long movieId, LookupOutcome outcome)
        {
            lock (_attemptLock)
            {
                if (!_outcomes.TryGetValue(movieId, out var previous) || previous != LookupOutcome.Pending) return;
                _outcomes[movieId] = outcome;
                _progress = new MetadataProgress(
                    _progress.Pending - 1,
                    _progress.Matched + (outcome == LookupOutcome.Matched ? 1 : 0),
                    _progress.Unmatched + (outcome == LookupOutcome.Unmatched ? 1 : 0),
                    _progress.Failed + (outcome == LookupOutcome.Failed ? 1 : 0));
            }
        }

        private bool BeginAttempt(long movieId)
        {
            lock (_attemptLock)
            {
                if (!_attempted.Add(movieId)) return false;
                RegisterProgress(movieId);
                _inFlight.Add(movieId);
                return true;
            }
        }

        /// <summary>
        /// Takes a film to look up. Returns immediately: this is called from the UI thread, once
        /// per film, and a library hands over thousands in a single loop.
        /// </summary>
        /// <remarks>
        /// Queued rather than started. The alternative, a task per film, is how a closing window
        /// came to abandon work it had started — nothing held the tasks — and then, once they were
        /// held, how warming a large library came to allocate one of them per film on the
        /// interface thread. A record in a queue costs neither.
        /// </remarks>
        public void Queue(long movieId, string title, int? year, Action<Enrichment> onFetched, CancellationToken ct)
        {
            if (_disposed || string.IsNullOrWhiteSpace(_cfg.TmdbApiKey)) return;

            RegisterProgress(movieId);
            Interlocked.Increment(ref _outstanding);
            _pending.Enqueue(new Request(movieId, title, year, onFetched, ct));

            StartWorkerIfNeeded();
        }

        /// <summary>
        /// Makes sure somebody is going to take the queue, without ever running more workers than
        /// the configured concurrency.
        /// </summary>
        /// <remarks>
        /// The compare-exchange loop rather than a lock: this is called once per film, and a lock
        /// on the interface thread for six thousand consecutive calls is a lock worth not taking.
        /// </remarks>
        private void StartWorkerIfNeeded()
        {
            while (true)
            {
                var running = Volatile.Read(ref _workers);
                if (running >= _maxConcurrency) return;

                if (Interlocked.CompareExchange(ref _workers, running + 1, running) != running) continue;

                // Counted before the worker exists, not inside it. A Dispose landing in the gap
                // would otherwise see nothing in flight and hand the shared client back, leaving
                // the worker about to start to fetch through a disposed client.
                Interlocked.Increment(ref _active);

                Track(Task.Run(DrainQueueAsync));
                return;
            }
        }

        /// <summary>
        /// Takes films off the queue until there are none left.
        /// </summary>
        /// <remarks>
        /// It deliberately does not stop when the loader does. Everything in the queue was
        /// accepted before the stop — <see cref="Queue"/> refuses anything after it — and a fetch
        /// TMDB may already have answered is one write away from being useful forever. What bounds
        /// the wait is <see cref="StopAsync"/>'s deadline and the token behind it, not a worker
        /// walking away from work it agreed to do.
        ///
        /// The re-check after standing down closes the race that would otherwise strand a film:
        /// a worker can find the queue empty at the same moment <see cref="Queue"/> finds the
        /// worker count full, and without looking again the item would sit there until something
        /// else happened to be queued.
        /// </remarks>
        private async Task DrainQueueAsync()
        {
            // _active was raised by whoever started this worker, and is held for its whole life
            // rather than only while a fetch runs — so the shared client is never released out
            // from under a worker that is between two films.
            try
            {
                while (true)
                {
                    // Artwork and genres first, every time a worker comes back for more: see
                    // _facts for why the extra request waits behind them.
                    while (_pending.TryDequeue(out var request) || _facts.TryDequeue(out request))
                    {
                        try
                        {
                            if (request.FactsFor is int tmdbId)
                                await FetchFactsAsync(request, tmdbId).ConfigureAwait(false);
                            else
                                await FetchAsync(request).ConfigureAwait(false);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _outstanding);
                        }
                    }

                    Interlocked.Decrement(ref _workers);

                    if (_pending.IsEmpty && _facts.IsEmpty) return;

                    // Something arrived as this was standing down. Take the post back if nobody
                    // else already has, and otherwise leave it to them.
                    while (true)
                    {
                        var running = Volatile.Read(ref _workers);
                        if (running >= _maxConcurrency) return;

                        if (Interlocked.CompareExchange(ref _workers, running + 1, running) == running) break;
                    }
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref _active) == 0 && _disposed) ReleaseClient();
            }
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

        /// <summary>
        /// Looks one film up now, on the calling task. The public way in for anything that wants
        /// to await a single fetch; the library goes through <see cref="Queue"/> instead.
        /// </summary>
        public Task EnsurePosterAsync(long movieId, string title, int? year, Action<Enrichment> onFetched, CancellationToken ct)
        {
            if (_disposed || string.IsNullOrWhiteSpace(_cfg.TmdbApiKey)) return Task.CompletedTask;

            RegisterProgress(movieId);
            return FetchAsync(new Request(movieId, title, year, onFetched, ct));
        }

        /// <summary>
        /// The fetch itself, with no admission check of its own.
        /// </summary>
        /// <remarks>
        /// That omission is deliberate and is the reason this is separate from
        /// <see cref="EnsurePosterAsync"/>. A worker reaches here with a film the loader accepted
        /// before it was told to stop, and re-testing the flag at this point would silently drop
        /// everything still in the queue at the moment a window closed — which is the bug the
        /// drain was written to fix, reintroduced one level down.
        /// </remarks>
        private async Task FetchAsync(Request request)
        {
            var (movieId, title, year, onFetched, ct, _) = request;

            if (string.IsNullOrWhiteSpace(_cfg.TmdbApiKey)) return;

            // Tracked rather than assumed. WaitAsync throws when the token is already cancelled,
            // which happens on window close, and the release below would then hand back a slot
            // that was never taken — inflating the gate past maxConcurrency and letting the next
            // library warm as many concurrent fetches as it liked.
            var acquired = false;
            var attempted = false;

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

                // What the catalogue already knows, read before anything decides whether to ask
                // TMDB. SAFE against NULL/DBNull in either column.
                var known = await ReadKnownAsync(conn, movieId, token);

                // Answered from the catalogue, and this is the only thing a film asked about a
                // second time ever gets — which is why it comes before the guard below rather
                // than after it.
                //
                // Every library read builds fresh UiMovie objects, and the window then offers the
                // loader every poster-less film again, with callbacks closing over the new ones.
                // A guard that returned silently here would drop those, so the artwork reached
                // the database and never the card: posters that only appeared after a restart,
                // which is most of the bug this set out to fix, reintroduced by the fix for it.
                var hasArtwork = HasArtwork(known.Found.PosterPath);
                if (hasArtwork || !string.IsNullOrWhiteSpace(known.Found.Genres))
                    onFetched(new Enrichment(hasArtwork ? known.Found.PosterPath : null, known.Found.Genres));
                if (hasArtwork && !string.IsNullOrWhiteSpace(known.Found.Genres))
                {
                    // Complete apart from, at most, its length, rating and people. Those are asked
                    // for behind everything else, and only when there is an identification to ask
                    // with. A film with artwork and genres but no TMDB id — chosen by hand, or
                    // enriched by a build older than the id column — is left alone rather than
                    // re-identified from its title, which could pick a different film from the one
                    // its poster shows. Correcting the match on its details screen supplies an id.
                    if (!known.FactsKnown)
                    {
                        if (known.TmdbId is int id) QueueFacts(movieId, id, onFetched, ct);
                        else RuleOutFacts(movieId);
                    }

                    CompleteProgress(movieId, LookupOutcome.Matched);
                    return;
                }

                // Only now is a request being considered, so only now does it count as an attempt.
                // A film already asked about stops here: there is nothing stored to report and
                // nothing left to learn until Refresh. See the field for why asking again
                // instead would spend the key on answers already known to be "no".
                if (!BeginAttempt(movieId)) return;
                attempted = true;

                // The search that finds the artwork also says what kind of film it is, so genres
                // cost nothing extra here. Before this, nothing in the app ever wrote the genres
                // column for a scanned film, and every one of them sat in a single Uncategorised
                // bucket for the life of the library.
                int? tmdbId = known.TmdbId;
                string? posterPath;
                string? genres;
                FilmFacts? facts = null;
                if (tmdbId is int knownId)
                {
                    // With the credits appended when the filters still need them: the same one
                    // request answers both questions, so a film already identified never costs a
                    // second one.
                    var details = known.FactsKnown
                        ? await _tmdb.GetDetailsByIdAsync(knownId, token)
                        : await _tmdb.GetFilmFactsAsync(knownId, token);
                    if (details is null)
                        throw new HttpRequestException("TMDB did not return this film. Press Refresh to retry.");
                    posterPath = details.PosterPath;
                    genres = CreditLine.Genres(details);
                    if (!known.FactsKnown) facts = FilmFactsStore.From(details);
                }
                else
                {
                    (tmdbId, posterPath, genres) = await _tmdb.SearchFilmAsync(title, year, token);
                }

                // Identification is what this turns on, not artwork. TMDB confidently knows plenty
                // of films it holds no poster for, and the guard here used to refuse those outright
                // — throwing away an id and a set of genres that had already been fetched and paid
                // for. Since a film is only ever asked about once, that left it uncategorised for
                // good: the column would never be written on this launch or any other.
                if (tmdbId is null)
                {
                    // Nothing to ask TMDB about its length or cast with, either.
                    RuleOutFacts(movieId);
                    CompleteProgress(movieId, LookupOutcome.Unmatched);
                    return;
                }

                string? pathToStore = hasArtwork ? known.Found.PosterPath : null;

                if (!hasArtwork && !string.IsNullOrWhiteSpace(posterPath))
                {
                    var url = _tmdb.BuildImageUrlPublic(posterPath!);

                    // download; if it fails, fall back to URL so UI can still load online
                    pathToStore = _cfg.DownloadPosters
                        ? await _tmdb.DownloadForPublic(url, $"{movieId}.jpg", token) ?? url
                        : url;
                }

                // Through the lane, not straight at the database. Up to four of these run at once
                // by design, and every one of them is a writer; without a turn to take they queue
                // on the SQLite write lock instead, where losing is reported as an error rather
                // than as a wait.
                //
                // The id is stored beside the poster so the details screen describes the film the
                // artwork belongs to, and so a person correcting the match has something to
                // correct rather than a poster from nowhere. A null path leaves the column alone,
                // which is what a film with no artwork wants — it must not blank a poster somebody
                // chose by hand.
                await MovieMatch.SaveAsync(conn, movieId, tmdbId.Value, pathToStore, genres: genres, ct: token);
                if (facts is not null) await FilmFactsStore.SaveAsync(conn, movieId, facts, token);
                CompleteProgress(movieId, LookupOutcome.Matched);

                onFetched(new Enrichment(pathToStore,
                    string.IsNullOrWhiteSpace(known.Found.Genres) ? genres : known.Found.Genres)
                {
                    ArtworkRepaired = !hasArtwork && known.Found.HasPoster
                        && string.Equals(known.Found.PosterPath, pathToStore, StringComparison.Ordinal),
                    Facts = facts
                });

                // Found by a search, so the length, rating and people are still to be asked for —
                // behind every other film's artwork and genres rather than ahead of them.
                if (facts is null && !known.FactsKnown) QueueFacts(movieId, tmdbId.Value, onFetched, ct);
                else RuleOutFacts(movieId);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                lock (_attemptLock)
                {
                    if (attempted) _attempted.Remove(movieId);
                    if ((attempted || !_inFlight.Contains(movieId))
                        && _outcomes.TryGetValue(movieId, out var outcome) && outcome == LookupOutcome.Pending)
                    {
                        _outcomes.Remove(movieId);
                        _progress = _progress with { Pending = _progress.Pending - 1 };
                    }
                }
            }
            catch (Exception ex)
            {
                CompleteProgress(movieId, LookupOutcome.Failed);
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

                if (attempted)
                {
                    lock (_attemptLock)
                        _inFlight.Remove(movieId);
                }

                if (Interlocked.Decrement(ref _active) == 0 && _disposed) ReleaseClient();
            }
        }

        private static bool HasArtwork(string? path) =>
            !string.IsNullOrWhiteSpace(path)
            && ((Uri.TryCreate(path, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                || File.Exists(PlatformPaths.Expand(path)));

        /// <summary>
        /// The artwork, genres and identification the catalogue already holds for a film, and
        /// whether its length, rating and people have been asked for.
        /// </summary>
        /// <remarks>
        /// Both columns, not just the poster. A film looked up in an earlier pass has its genres
        /// written down too, and a card rebuilt by a later library read has to be told about them
        /// or it stays in the Uncategorised bucket while the database says otherwise.
        /// </remarks>
        private static async Task<Known> ReadKnownAsync(SqliteConnection conn, long movieId, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT poster_path, genres, tmdb_id, facts_fetched_at IS NOT NULL FROM movies WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", movieId);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return new Known(Enrichment.None, null, false);

            int? tmdbId = null;
            if (!reader.IsDBNull(2))
            {
                var id = reader.GetInt64(2);
                if (id > 0 && id <= int.MaxValue) tmdbId = (int)id;
            }

            return new Known(
                new Enrichment(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1)),
                tmdbId,
                !reader.IsDBNull(3) && reader.GetInt64(3) != 0);
        }

        private readonly record struct Known(Enrichment Found, int? TmdbId, bool FactsKnown);

        /// <summary>
        /// Asks TMDB for a film's length, rating and people, writes them down, and reports them.
        /// </summary>
        /// <remarks>
        /// Quieter than <see cref="FetchAsync"/> about failing. These fill in a filter rather than
        /// a card, and the artwork and genres the person is looking at have already arrived; a
        /// failure is logged and left for the next launch or Refresh, whose timestamp is still
        /// unset, rather than put in the status line over something more useful.
        ///
        /// What is reported alongside the facts is what the catalogue now holds for the film, not
        /// a blank: whoever is listening may keep only the last answer it was given.
        /// </remarks>
        private async Task FetchFactsAsync(Request request, int tmdbId)
        {
            var (movieId, _, _, onFetched, ct, _) = request;

            var acquired = false;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
            var token = linked.Token;

            Interlocked.Increment(ref _active);

            try
            {
                if (string.IsNullOrWhiteSpace(_cfg.TmdbApiKey)) return;

                await _gate.WaitAsync(token);
                acquired = true;

                var details = await _tmdb.GetFilmFactsAsync(tmdbId, token);
                if (details is null)
                {
                    AppLog.Write("posters.log", $"movieId={movieId} TMDB returned no details for {tmdbId}; length, rating and cast left unknown.");
                    return;
                }

                var facts = FilmFactsStore.From(details);

                using var conn = Database.Open(_dbPath);
                await FilmFactsStore.SaveAsync(conn, movieId, facts, token);

                var known = await ReadKnownAsync(conn, movieId, token);
                var poster = HasArtwork(known.Found.PosterPath) ? known.Found.PosterPath : null;

                onFetched(new Enrichment(poster, known.Found.Genres) { Facts = facts });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Not an answer. Asked again after the next Refresh or launch.
                lock (_attemptLock) _factsAsked.Remove(movieId);
            }
            catch (Exception ex)
            {
                AppLog.Write("posters.log", $"movieId={movieId} facts for {tmdbId}: {ex}");
            }
            finally
            {
                if (acquired) _gate.Release();

                lock (_attemptLock) _factsWaiting.Remove(movieId);

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
            MarkStopped();

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
        /// Publishes the stop, and makes sure the read of <c>_active</c> that follows it cannot
        /// be ordered ahead of it.
        ///
        /// A volatile write only has release semantics, which does not stop a later read of a
        /// different field moving in front of it. Without the fence, this and a fetch finishing
        /// at the same instant can both conclude the other will release the client: the fetch
        /// decrements <c>_active</c> to zero and reads a stale <c>false</c> here, while this
        /// reads the count before the decrement lands. Neither releases, and the shared client —
        /// the resource this whole change exists to bound — is leaked for the life of the
        /// process. The fetch's own side is already fenced by its interlocked decrement.
        /// </summary>
        private void MarkStopped()
        {
            _disposed = true;
            Thread.MemoryBarrier();
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
            MarkStopped();

            if (Volatile.Read(ref _active) == 0) ReleaseClient();
        }
    }
}
