using System;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>
    /// Turns a stream of keystrokes into at most one visible result: the newest one.
    ///
    /// Three separate problems live here, and only the first is obvious.
    ///
    /// <em>Too many queries.</em> Typing "matrix" fired six full library reads, five of which
    /// nobody ever saw. Each request waits <see cref="DefaultDebounce"/> before it starts, and a
    /// later request cancels the wait, so a burst of typing costs one read.
    ///
    /// <em>Wasted work.</em> A superseded request is cancelled rather than left to finish, so the
    /// query for "ma" stops as soon as "mat" arrives — as far as it can, which is the third
    /// problem.
    ///
    /// <em>Out-of-order results.</em> Cancellation is cooperative, and SQLite cannot interrupt a
    /// statement that has already started, so a cancelled read still comes back holding rows. If
    /// "ma" happens to be slower than "matrix" — a bigger result set is exactly when that
    /// happens — it returns last and, without a guard, overwrites the results for a word the user
    /// has already finished typing. The token cannot fix that, because by then it has already been
    /// ignored. A monotonic generation number does: every request takes one, and a result is
    /// applied only while its own number is still the newest. That check and the assignment it
    /// guards happen together, so nothing can slip between them.
    /// </summary>
    /// <remarks>
    /// <see cref="PostAsync"/> and <see cref="RefreshAsync"/> capture the synchronisation context
    /// of whoever calls them, and the apply callback runs on it. Call them from the UI thread and
    /// the collections are rebuilt there, with no marshalling and no second place for two searches
    /// to be reordered.
    /// </remarks>
    /// <typeparam name="TResult">Whatever one request produces. This class never looks inside it.</typeparam>
    public sealed class SearchCoordinator<TResult> : IDisposable
    {
        /// <summary>
        /// How long the box has to be quiet before a search runs.
        ///
        /// Fluent typing puts 120-160ms between keystrokes, so anything below that debounces
        /// nothing and the freeze this class exists to fix comes straight back. Past roughly a
        /// quarter of a second the pause before results appear stops reading as "still typing" and
        /// starts reading as a slow app. 200ms sits above the first number and below the second:
        /// a typed word collapses into one query, and the results land close enough to the last
        /// keystroke to feel like a consequence of it.
        /// </summary>
        public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(200);

        private readonly Func<string?, CancellationToken, Task<TResult>> _run;
        private readonly Action<string?, TResult> _apply;
        private readonly Action<Exception>? _onError;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly TimeSpan _debounce;
        private readonly CancellationToken _lifetime;

        private readonly object _gate = new();
        private CancellationTokenSource? _current;
        private long _generation;
        private bool _disposed;

        /// <param name="run">Produces the result for one query. Runs off the UI thread; see <see cref="LibraryLoader.LoadAsync"/>.</param>
        /// <param name="apply">Puts a result on screen. Called at most once per request, never for a superseded one.</param>
        /// <param name="debounce">How long to wait for typing to stop. Defaults to <see cref="DefaultDebounce"/>.</param>
        /// <param name="lifetime">
        /// Cancelled when the owner is finished — the window closing, in this app. Every request
        /// links to it, so a search still running when the window closes stops with it instead of
        /// coming back to touch a window that is gone.
        /// </param>
        /// <param name="onError">
        /// Called when a request fails, and only while that request is still the newest. A failure
        /// nobody is waiting for is not worth a status line.
        /// </param>
        /// <param name="delay">
        /// The wait itself, for tests that would rather not spend real time. Defaults to
        /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
        /// </param>
        public SearchCoordinator(
            Func<string?, CancellationToken, Task<TResult>> run,
            Action<string?, TResult> apply,
            TimeSpan? debounce = null,
            CancellationToken lifetime = default,
            Action<Exception>? onError = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _run = run ?? throw new ArgumentNullException(nameof(run));
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));

            _debounce = debounce ?? DefaultDebounce;
            if (_debounce < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(debounce), "A debounce cannot be negative.");

            _lifetime = lifetime;
            _onError = onError;
            _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        }

        /// <summary>
        /// Somebody is typing. Supersedes anything in flight and runs once the box goes quiet.
        /// </summary>
        /// <returns>
        /// Completes when this request has been applied, superseded or cancelled. The window
        /// discards it; a test awaits it. It never faults — a failure goes to the error callback,
        /// because the caller is an event handler and an exception escaping one would end the
        /// process.
        /// </returns>
        public Task PostAsync(string? query) => Start(query, waitForTypingToStop: true);

        /// <summary>
        /// The library changed underneath — a scan finished, a server synced, the settings were
        /// saved. Same supersession rules, no wait: nobody is typing, and there is nothing to
        /// coalesce with.
        /// </summary>
        public Task RefreshAsync(string? query = null) => Start(query, waitForTypingToStop: false);

        private Task Start(string? query, bool waitForTypingToStop)
        {
            CancellationTokenSource cts;
            long generation;

            lock (_gate)
            {
                if (_disposed) return Task.CompletedTask;

                // Cancel before the newer request exists, so there is never a moment where two
                // requests both believe they are current.
                _current?.Cancel();

                cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
                _current = cts;
                generation = ++_generation;
            }

            // Deliberately outside the lock: this runs synchronously as far as the first await,
            // and holding the lock across the query would serialise the very work being cancelled.
            return RunAsync(query, generation, waitForTypingToStop, cts);
        }

        private async Task RunAsync(string? query, long generation, bool waitForTypingToStop, CancellationTokenSource cts)
        {
            try
            {
                if (waitForTypingToStop && _debounce > TimeSpan.Zero)
                    await _delay(_debounce, cts.Token);

                cts.Token.ThrowIfCancellationRequested();

                var result = await _run(query, cts.Token);

                // Two different questions, and only one of them the token can answer.
                //
                // "Has the owner gone?" is the token's, and it is asked here because a run that
                // ignored its cancellation — a SQLite statement already in flight cannot do
                // otherwise — still comes back holding rows that would land on a closing window.
                if (_lifetime.IsCancellationRequested) return;

                // "Is this still the search anybody wants?" is not the token's, because a
                // superseded request that ignored its cancellation looks identical to one that
                // was never cancelled at all. Only the generation can tell them apart.
                TryApply(generation, query, result);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later keystroke, or the owner is shutting down. Neither is a failure.
            }
            catch (Exception ex)
            {
                if (IsCurrent(generation)) _onError?.Invoke(ex);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_current, cts)) _current = null;
                }

                // Safe here and nowhere else: the request that owns this source has finished with
                // it. Disposing it at the moment of cancellation would pull the token out from
                // under a query still observing it.
                cts.Dispose();
            }
        }

        /// <summary>
        /// The last-write-wins guard. The check and the apply are one atomic step, because a
        /// result that is current when tested and stale by the time it is written is the exact bug
        /// this class exists to prevent.
        /// </summary>
        private void TryApply(long generation, string? query, TResult result)
        {
            lock (_gate)
            {
                if (_disposed || generation != _generation) return;

                _apply(query, result);
            }
        }

        private bool IsCurrent(long generation)
        {
            lock (_gate) return !_disposed && generation == _generation;
        }

        /// <summary>
        /// Stops accepting requests and cancels whatever is in flight. Idempotent. The sources
        /// themselves are disposed by the requests that own them, which may still be unwinding.
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;

                _current?.Cancel();
                _current = null;
            }
        }
    }
}
