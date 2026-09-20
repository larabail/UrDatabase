using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public sealed class TmdbRequestSchedulerTests
{
    [Fact]
    public async Task Concurrent_requests_share_a_ten_per_second_pace()
    {
        var scheduler = new TmdbRequestScheduler();
        var elapsed = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => scheduler.WaitAsync(default)))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(290));
    }

    [Fact]
    public void Both_retry_after_formats_extend_but_never_shorten_the_shared_pause()
    {
        var clock = new Clock();
        var scheduler = new TmdbRequestScheduler(TimeSpan.Zero, clock);

        Assert.Equal(90, scheduler.Pause(new RetryConditionHeaderValue(clock.Now.AddSeconds(90)), 1));
        Assert.Equal(90, scheduler.Pause(new RetryConditionHeaderValue(TimeSpan.FromSeconds(10)), 1));
        clock.Now += TimeSpan.FromSeconds(20);
        Assert.Equal(70, scheduler.RetryAfterSeconds);
        Assert.Equal(100, scheduler.Pause(new RetryConditionHeaderValue(TimeSpan.FromSeconds(100)), 1));
        clock.Now += TimeSpan.FromSeconds(100);
        Assert.Equal(0, scheduler.RetryAfterSeconds);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(6, 60)]
    [InlineData(100, 60)]
    public void Missing_retry_after_uses_increasing_bounded_backoff(int limits, int seconds)
    {
        var scheduler = new TmdbRequestScheduler(TimeSpan.Zero, new Clock());

        Assert.Equal(seconds, scheduler.Pause(null, limits));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Zero_or_past_retry_after_cannot_cause_a_busy_retry_loop(bool past)
    {
        var clock = new Clock();
        var scheduler = new TmdbRequestScheduler(TimeSpan.Zero, clock);
        var header = past
            ? new RetryConditionHeaderValue(clock.Now.AddHours(-1))
            : new RetryConditionHeaderValue(TimeSpan.Zero);

        scheduler.Pause(header, 1);
        clock.Now += TimeSpan.FromMilliseconds(99);
        Assert.Equal(1, scheduler.RetryAfterSeconds);
        clock.Now += TimeSpan.FromMilliseconds(1);
        Assert.Equal(0, scheduler.RetryAfterSeconds);
    }

    [Fact]
    public async Task Both_the_cooldown_waiter_and_queued_callers_can_be_cancelled()
    {
        var scheduler = new TmdbRequestScheduler();
        scheduler.Pause(new RetryConditionHeaderValue(TimeSpan.FromHours(1)), 1);
        using var cancellation = new CancellationTokenSource();
        var first = scheduler.WaitAsync(cancellation.Token);
        var queued = scheduler.WaitAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.True(scheduler.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task A_response_can_extend_a_cooldown_that_another_request_is_already_waiting_out()
    {
        var scheduler = new TmdbRequestScheduler(TimeSpan.Zero);
        scheduler.Pause(new RetryConditionHeaderValue(TimeSpan.Zero), 1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = scheduler.WaitAsync(cancellation.Token);
        var elapsed = Stopwatch.StartNew();

        scheduler.Pause(new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(250)), 1);
        await waiting;

        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(240));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
