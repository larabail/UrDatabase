using System;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public sealed class MoviePageArtworkTests : IDisposable
{
    private readonly TempLog _log = new();

    [Fact]
    public async Task FailedArtworkIsRetriedAtTheSamePathAndPreservedUntilSuccess()
    {
        var current = new object();
        var replacement = new object();
        var calls = 0;
        Task<object?> Load(string? source, CancellationToken _)
        {
            Assert.Equal("same-poster-path", source);
            return Task.FromResult(++calls == 1 ? null : replacement);
        }

        var failed = await MoviePageArtwork.RetryAsync("same-poster-path", current, Load);
        Assert.Same(current, failed.Image);
        Assert.True(failed.Failed);

        var retried = await MoviePageArtwork.RetryAsync("same-poster-path", failed.Image, Load);
        Assert.Same(replacement, retried.Image);
        Assert.False(retried.Failed);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancelledArtworkKeepsItsPreviousImageEvenWhenTheLoaderIgnoresCancellation()
    {
        var current = new object();
        using var cts = new CancellationTokenSource();
        var result = await MoviePageArtwork.RetryAsync("poster-path", current, (_, _) =>
        {
            cts.Cancel();
            return Task.FromResult<object?>(new object());
        }, cts.Token);

        Assert.Same(current, result.Image);
    }

    [Fact]
    public async Task ArtworkExceptionIsVisibleAndNeverClearsTheWorkingImage()
    {
        var current = new object();
        var result = await MoviePageArtwork.RetryAsync<object>("poster-path", current,
            (_, _) => throw new InvalidOperationException("Broken image"));

        Assert.Same(current, result.Image);
        Assert.True(result.Failed);
    }

    public void Dispose() => _log.Dispose();
}
