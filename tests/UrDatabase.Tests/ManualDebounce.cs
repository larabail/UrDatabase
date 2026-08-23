using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Tests
{
    /// <summary>
    /// A debounce that waits for a test to say so instead of for the clock.
    ///
    /// <see cref="SearchCoordinator{TResult}"/> takes its wait as a parameter precisely so this is
    /// possible. Asserting on real milliseconds would mean either a slow suite or a flaky one: the
    /// interesting assertion is "nothing has run yet", and against a real timer that is only ever
    /// true for as long as the machine happens to be keeping up.
    /// </summary>
    internal sealed class ManualDebounce
    {
        private readonly List<TaskCompletionSource> _waiting = new();

        /// <summary>Hand this to the coordinator in place of <c>Task.Delay</c>.</summary>
        public Task Wait(TimeSpan _, CancellationToken ct)
        {
            // Asynchronous continuations, so releasing a wait never runs the rest of a search on
            // the thread that released it. Inline continuations would make the ordering these
            // tests assert on depend on which thread got there first.
            var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_waiting) _waiting.Add(wait);

            // Superseding a request cancels its wait, which is what a later keystroke does.
            ct.Register(() => wait.TrySetCanceled(ct));

            return wait.Task;
        }

        /// <summary>How many requests have started waiting, cancelled ones included.</summary>
        public int Started
        {
            get { lock (_waiting) return _waiting.Count; }
        }

        /// <summary>
        /// Lets every wait through. The cancelled ones are already finished and ignore this, so
        /// only the request nothing superseded goes on to run.
        /// </summary>
        public void ReleaseAll()
        {
            TaskCompletionSource[] pending;
            lock (_waiting) pending = _waiting.ToArray();

            foreach (var wait in pending) wait.TrySetResult();
        }
    }
}
