using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>An IMDb rating the backfill has just found, for whichever cards are that title.</summary>
    /// <param name="TmdbId">The TMDB film it was found through, when it was found that way.</param>
    public sealed record ImdbRatingFound(string ImdbId, double Rating, int? TmdbId);

    /// <summary>
    /// Fills in IMDb ratings for the whole library in the background, so the star under a card's
    /// title does not wait for somebody to open the film.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything goes through the same cache the details screen uses, <c>imdb_ratings</c>, under
    /// the same rule: a title with any answer recorded — a rating, or "OMDb has none" — is never
    /// asked about again. Only a manual refresh of a title's page forgets a "none".
    /// </para>
    /// <para>
    /// OMDb's free tier is 1,000 requests a day, and this is the one thing in the app that could
    /// spend it all unattended. So it keeps to <see cref="DefaultDailyBudget"/> of them, counting
    /// every request the app made that day including the details screen's, leaves a gap between
    /// requests, and stops for the rest of the day the moment OMDb says the limit is reached or
    /// refuses the key. Days are UTC; see <see cref="OmdbUsage"/>.
    /// </para>
    /// <para>
    /// It runs behind the poster pass rather than beside it: whenever <c>isBusy</c> says artwork,
    /// genres or facts are still being fetched it waits, so a fresh library sorts itself onto its
    /// shelves before any of this starts. With no OMDb key it does nothing at all.
    /// </para>
    /// <para>
    /// A card needs an IMDb id to be rated. A server film and a programme usually carry one; a
    /// local film gets one from TMDB, recorded by the facts pass at no extra cost or, for a film
    /// described before that, asked of TMDB here, once, and kept in <c>tmdb_imdb_ids</c>.
    /// </para>
    /// </remarks>
    public sealed class ImdbRatingBackfill : IDisposable
    {
        /// <summary>
        /// How many OMDb requests a day the backfill will let the app reach before it stops. Half
        /// the free tier, so a day of opening films is never the one that finds the quota spent.
        /// </summary>
        public const int DefaultDailyBudget = 500;

        /// <summary>The gap between two OMDb requests. 500 of them take about a quarter of an hour.</summary>
        public static readonly TimeSpan DefaultSpacing = TimeSpan.FromSeconds(1.5);

        /// <summary>How often a waiting backfill looks again at whether the poster pass has finished.</summary>
        public static readonly TimeSpan DefaultBusyPoll = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Failures in a row after which the backfill gives up until the next library read. A
        /// machine with no network fails every request, and should not do so five hundred times.
        /// </summary>
        internal const int MaxConsecutiveFailures = 5;

        private readonly string _dbPath;
        private readonly IImdbRatingLookup _lookup;
        private readonly Func<int, CancellationToken, Task<string?>>? _resolveImdbId;
        private readonly Func<bool> _isBusy;
        private readonly Action<ImdbRatingFound>? _onRating;
        private readonly int _dailyBudget;
        private readonly TimeSpan _spacing;
        private readonly TimeSpan _busyPoll;
        private readonly Func<DateTime> _utcNow;
        private readonly CancellationTokenSource _stopping = new();

        private readonly object _lock = new();
        private readonly HashSet<string> _offered = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<Target> _withImdbId = new();
        private readonly ConcurrentQueue<Target> _withTmdbId = new();

        private Task _worker = Task.CompletedTask;
        private bool _running;
        private string? _haltedDay;

        private readonly record struct Target(string? ImdbId, int? TmdbId);

        /// <param name="lookup">OMDb, or a fake. Not disposed here: whoever built it owns it.</param>
        /// <param name="resolveImdbId">
        /// Asks TMDB which IMDb title a TMDB film is. Returns the id, or null or empty when TMDB has
        /// none, and throws when it could not be asked. Null when there is no TMDB key, in which
        /// case only cards that already carry an IMDb id are rated.
        /// </param>
        /// <param name="isBusy">True while something more important is using the network.</param>
        /// <param name="onRating">Told about each rating found, on a background thread.</param>
        public ImdbRatingBackfill(
            string dbPath,
            IImdbRatingLookup lookup,
            Func<int, CancellationToken, Task<string?>>? resolveImdbId = null,
            Func<bool>? isBusy = null,
            Action<ImdbRatingFound>? onRating = null,
            int dailyBudget = DefaultDailyBudget,
            TimeSpan? spacing = null,
            TimeSpan? busyPoll = null,
            Func<DateTime>? utcNow = null)
        {
            _dbPath = dbPath ?? "";
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            _resolveImdbId = resolveImdbId;
            _isBusy = isBusy ?? (() => false);
            _onRating = onRating;
            _dailyBudget = Math.Max(0, dailyBudget);
            _spacing = spacing ?? DefaultSpacing;
            _busyPoll = busyPoll ?? DefaultBusyPoll;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>False with no OMDb key, and then <see cref="Offer"/> does nothing.</summary>
        public bool IsEnabled => _lookup.IsAvailable && !string.IsNullOrWhiteSpace(_dbPath);

        /// <summary>
        /// Takes the cards a library read produced and queues every title not yet rated or
        /// offered. Cheap and immediate: it is called from the UI thread after every read, and a
        /// title already offered this session is not offered twice.
        /// </summary>
        public void Offer(IEnumerable<UiMovie> cards)
        {
            if (!IsEnabled || cards is null || _stopping.IsCancellationRequested) return;

            lock (_lock)
            {
                foreach (var card in cards)
                {
                    if (card is null || card.IsEpisode || card.ImdbRating is not null) continue;

                    var imdbId = TmdbImdbIds.Normalise(card.ImdbId);
                    if (imdbId is not null)
                    {
                        if (_offered.Add(imdbId)) _withImdbId.Enqueue(new Target(imdbId, null));
                    }
                    else if (card.IsFilm && card.TmdbId is int tmdbId && tmdbId > 0 && _resolveImdbId is not null)
                    {
                        if (_offered.Add("tmdb:" + tmdbId)) _withTmdbId.Enqueue(new Target(null, tmdbId));
                    }
                }

                StartIfNeeded();
            }
        }

        /// <summary>Completes once nothing is running, including any run started as one ended. For tests.</summary>
        public async Task WhenIdleAsync()
        {
            while (true)
            {
                Task current;
                lock (_lock) current = _worker;

                await current.ConfigureAwait(false);

                lock (_lock)
                    if (ReferenceEquals(current, _worker) && !_running) return;
            }
        }

        /// <summary>How many titles are still waiting. For tests.</summary>
        internal int Waiting => _withImdbId.Count + _withTmdbId.Count;

        private void StartIfNeeded()
        {
            if (_running || _stopping.IsCancellationRequested) return;
            if (_withImdbId.IsEmpty && _withTmdbId.IsEmpty) return;
            if (_haltedDay is not null && _haltedDay == OmdbUsage.DayOf(_utcNow())) return;

            _haltedDay = null;
            _running = true;
            _worker = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            var token = _stopping.Token;
            var failures = 0;

            try
            {
                while (!token.IsCancellationRequested && TryPeek(out var queue, out var target))
                {
                    var step = await ProcessAsync(target, token).ConfigureAwait(false);

                    if (step == Step.HaltForTheDay)
                    {
                        lock (_lock) _haltedDay = OmdbUsage.DayOf(_utcNow());
                        return;
                    }

                    queue.TryDequeue(out _);

                    if (step == Step.Failed)
                    {
                        if (++failures >= MaxConsecutiveFailures)
                        {
                            AppLog.Write("omdb.log", $"backfill: {failures} failures in a row; stopping until the library is read again.");
                            return;
                        }
                    }
                    else if (step == Step.Asked)
                    {
                        failures = 0;
                    }

                    if (step is Step.Asked or Step.Failed && _spacing > TimeSpan.Zero)
                        await Task.Delay(_spacing, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Closing. Whatever was not asked is asked on the next launch.
            }
            catch (Exception ex)
            {
                AppLog.Write("omdb.log", $"backfill stopped: {ex}");
            }
            finally
            {
                lock (_lock)
                {
                    _running = false;

                    // Something may have been offered between the last look and standing down. A
                    // run that stopped on failures does not restart itself; the next read does.
                    if (failures < MaxConsecutiveFailures) StartIfNeeded();
                }
            }
        }

        private bool TryPeek(out ConcurrentQueue<Target> queue, out Target target)
        {
            if (_withImdbId.TryPeek(out target)) { queue = _withImdbId; return true; }
            if (_withTmdbId.TryPeek(out target)) { queue = _withTmdbId; return true; }

            queue = _withImdbId;
            return false;
        }

        private enum Step { Skipped, Asked, Failed, HaltForTheDay }

        private async Task<Step> ProcessAsync(Target target, CancellationToken ct)
        {
            using var conn = Database.Open(_dbPath);

            var imdbId = target.ImdbId;
            if (imdbId is null && target.TmdbId is int tmdbId)
            {
                var (known, recorded) = TmdbImdbIds.Read(conn, tmdbId);
                if (known)
                {
                    imdbId = recorded;
                }
                else
                {
                    await WaitWhileBusyAsync(ct).ConfigureAwait(false);

                    try
                    {
                        var answer = await _resolveImdbId!(tmdbId, ct).ConfigureAwait(false);
                        await TmdbImdbIds.SaveAsync(conn, tmdbId, answer, ct).ConfigureAwait(false);
                        imdbId = TmdbImdbIds.Normalise(answer);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // TMDB, not OMDb: no quota spent, and nothing recorded, so it is asked
                        // again next launch. Not counted towards giving up, which is about OMDb.
                        AppLog.Write("omdb.log", $"backfill: TMDB {tmdbId} imdb id: {ex.Message}");
                        return Step.Skipped;
                    }
                }

                if (imdbId is null) return Step.Skipped;
            }

            if (imdbId is null) return Step.Skipped;

            // Answered already, by the details screen or an earlier launch. Reported anyway: the
            // card that offered it may simply not have been able to find it.
            if (ImdbRatingService.TryReadCache(conn, imdbId, out var cached))
            {
                if (cached is double known) Report(imdbId, known, target.TmdbId);
                return Step.Skipped;
            }

            var day = OmdbUsage.DayOf(_utcNow());
            var usage = ImdbRatingService.TryReadUsage(conn, day);
            if (usage.LimitReached || usage.Requests >= _dailyBudget)
            {
                AppLog.Write("omdb.log",
                    $"backfill: stopping for {day}; {usage.Requests} requests made" + (usage.LimitReached ? ", and OMDb said the limit is reached." : "."));
                return Step.HaltForTheDay;
            }

            await WaitWhileBusyAsync(ct).ConfigureAwait(false);

            var result = await _lookup.LookupResultAsync(imdbId, ct).ConfigureAwait(false);
            await ImdbRatingService.RecordUsageAsync(conn, day, result, ct).ConfigureAwait(false);

            if (result.StopsForTheDay)
            {
                AppLog.Write("omdb.log", $"backfill: OMDb answered {result.Status}; stopping for {day}.");
                return Step.HaltForTheDay;
            }

            if (!result.IsAnswer) return Step.Failed;

            await DatabaseWriteLane.RunAsync(conn, _ =>
            {
                ImdbRatingService.WriteCache(conn, imdbId, null, result.Rating);
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);

            if (result.Rating is double rating) Report(imdbId, rating, target.TmdbId);
            return Step.Asked;
        }

        private async Task WaitWhileBusyAsync(CancellationToken ct)
        {
            while (_isBusy())
                await Task.Delay(_busyPoll, ct).ConfigureAwait(false);
        }

        private void Report(string imdbId, double rating, int? tmdbId)
        {
            try
            {
                _onRating?.Invoke(new ImdbRatingFound(imdbId, rating, tmdbId));
            }
            catch (Exception ex)
            {
                AppLog.Write("omdb.log", $"backfill: reporting {imdbId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Puts every IMDb rating the catalogue already holds onto the cards that are those
        /// titles, with no request. For cards that are not read out of the catalogue by a query
        /// that finds it for them: the server's, which come out of its cache.
        /// </summary>
        public static void StampCached(SqliteConnection conn, IEnumerable<UiMovie> cards)
        {
            foreach (var card in cards)
            {
                if (card is null || card.IsEpisode || card.ImdbRating is not null) continue;

                var imdbId = TmdbImdbIds.Normalise(card.ImdbId);
                if (imdbId is null && card.IsFilm && card.TmdbId is int tmdbId && tmdbId > 0)
                    imdbId = TmdbImdbIds.Read(conn, tmdbId).ImdbId;

                if (imdbId is null) continue;
                card.ImdbId ??= imdbId;

                if (ImdbRatingService.TryReadCache(conn, imdbId, out var rating) && rating is double value)
                    card.ApplyImdbRating(value);
            }
        }

        /// <summary>
        /// Puts a rating that has just arrived onto every card that is that title: by IMDb id, by
        /// the TMDB film it was found through (films only — a programme's TMDB id is a different
        /// catalogue), or by whatever else the caller knows identifies the card.
        /// </summary>
        /// <returns>True when any card's rating changed.</returns>
        public static bool ApplyToCards(
            IEnumerable<UiMovie> cards, string imdbId, double rating, int? tmdbId, Func<UiMovie, bool>? alsoMatches = null)
        {
            var changed = false;

            foreach (var card in cards)
            {
                if (card is null || card.IsEpisode) continue;

                var matches = string.Equals(card.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase)
                    || (tmdbId is int id && card.IsFilm && card.TmdbId == id)
                    || (alsoMatches?.Invoke(card) ?? false);
                if (!matches) continue;

                card.ImdbId ??= imdbId;
                if (card.ImdbRating == rating) continue;

                card.ApplyImdbRating(rating);
                changed = true;
            }

            return changed;
        }

        /// <summary>Stops the backfill. Anything not yet asked is asked on the next launch.</summary>
        public void Dispose()
        {
            _stopping.Cancel();
        }
    }
}
