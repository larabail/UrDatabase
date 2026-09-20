using System;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services;

/// <summary>One request pace and rate-limit cooldown shared by the app's TMDB clients.</summary>
public sealed class TmdbRequestScheduler
{
    internal static TmdbRequestScheduler Shared { get; } = new();

    private readonly object _sync = new();
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private DateTimeOffset _nextRequest;
    private DateTimeOffset _retryAt;

    public TmdbRequestScheduler() : this(TimeSpan.FromMilliseconds(100)) { }

    internal TmdbRequestScheduler(TimeSpan minimumInterval, TimeProvider? clock = null)
    {
        if (minimumInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        _interval = minimumInterval;
        _clock = clock ?? TimeProvider.System;
    }

    internal int RetryAfterSeconds
    {
        get
        {
            lock (_sync)
                return (int)Math.Clamp(Math.Ceiling((_retryAt - _clock.GetUtcNow()).TotalSeconds), 0, int.MaxValue);
        }
    }

    internal async Task WaitAsync(CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                TimeSpan delay;
                lock (_sync)
                {
                    var now = _clock.GetUtcNow();
                    var due = _nextRequest > _retryAt ? _nextRequest : _retryAt;
                    delay = due - now;
                    if (delay <= TimeSpan.Zero)
                    {
                        _nextRequest = now + _interval;
                        return;
                    }
                }

                // Recheck after waking: another in-flight response can extend the shared pause.
                await Task.Delay(delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay, _clock, ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    internal int Pause(RetryConditionHeaderValue? retryAfter, int consecutiveLimits)
    {
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            var delay = retryAfter?.Delta
                ?? (retryAfter?.Date is { } date
                    ? date - now
                    : TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Clamp(consecutiveLimits - 1, 0, 5)))));
            // A zero/past header must not turn a persistent 429 into a tight request loop.
            if (delay < TimeSpan.FromMilliseconds(100)) delay = TimeSpan.FromMilliseconds(100);
            var until = now + (delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue - now : delay);
            if (until > _retryAt) _retryAt = until;
            return RetryAfterSeconds;
        }
    }
}
