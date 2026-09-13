using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class SeriesRefreshSessionTests
    {
        [Fact]
        public async Task Refresh_coalesces_with_initial_loading_and_with_other_refreshes()
        {
            using var session = new SeriesRefreshSession();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var first = session.RunAsync(_ => { calls++; return release.Task; });
            var duplicate = session.RunAsync(_ => { calls++; return Task.CompletedTask; });

            Assert.Same(first, duplicate);
            Assert.True(session.IsBusy);
            Assert.Equal(1, calls);
            release.SetResult();
            await first;
            Assert.False(session.IsBusy);

            await session.RunAsync(_ => { calls++; return Task.CompletedTask; });
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task Closing_cancels_work_and_ignores_late_completion_after_reopening()
        {
            var old = new SeriesRefreshSession();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var published = false;
            var pending = old.RunAsync(async _ =>
            {
                await release.Task; // A transport that ignores cancellation.
                if (old.IsCurrent) published = true;
            });

            old.Dispose();
            using var reopened = new SeriesRefreshSession();
            Assert.True(old.Token.IsCancellationRequested);
            Assert.False(old.IsCurrent);
            Assert.True(reopened.IsCurrent);
            release.SetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.False(published);
            Assert.False(reopened.IsBusy);
            await old.RunAsync(_ => throw new InvalidOperationException("A closed visit must not restart."));
        }

        [Fact]
        public async Task A_failed_refresh_releases_the_gate_so_it_can_be_retried()
        {
            using var session = new SeriesRefreshSession();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RunAsync(_ => throw new InvalidOperationException("offline")));
            Assert.False(session.IsBusy);

            var retried = false;
            await session.RunAsync(_ => { retried = true; return Task.CompletedTask; });

            Assert.True(retried);
        }

        [Fact]
        public async Task Closing_the_application_cancels_the_visit()
        {
            using var lifetime = new CancellationTokenSource();
            using var session = new SeriesRefreshSession(lifetime.Token);
            lifetime.Cancel();

            Assert.False(session.IsCurrent);
            await session.RunAsync(_ => throw new InvalidOperationException("The application is closed."));
        }
    }
}
