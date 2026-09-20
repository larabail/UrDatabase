using System;
using UrDatabase.Models;
using Xunit;

namespace UrDatabase.Tests;

public sealed class MetadataProgressTests
{
    [Fact]
    public void Poster_count_is_not_presented_as_metadata_completion()
    {
        var progress = new MetadataProgress(Pending: 2000, Matched: 648, Unmatched: 3200, Failed: 152);

        Assert.Equal(4000, progress.Processed);
        Assert.Equal(6000, progress.Total);
        Assert.Equal("Metadata: 4000/6000 checked · 2000 pending · 3200 unmatched · 152 failed", progress.Describe());
        Assert.Equal("Posters present: 648/6000" + Environment.NewLine + progress.Describe(),
            progress.WithStatus("Posters present: 648/6000"));
    }

    [Fact]
    public void A_completed_pass_with_unmatched_films_still_says_it_finished_every_lookup()
    {
        var progress = new MetadataProgress(Matched: 648, Unmatched: 5352);

        Assert.Equal("Metadata: 6000/6000 checked · 0 pending · 5352 unmatched · 0 failed", progress.Describe());
    }

    [Fact]
    public void Background_progress_does_not_erase_an_actionable_error()
    {
        const string error = "TMDB search failed (HTTP 401). Press Refresh to retry.";
        var progress = new MetadataProgress(Pending: 99, Failed: 1);

        Assert.StartsWith(error + Environment.NewLine, progress.WithStatus(error));
        Assert.Contains("1/100 checked", progress.WithStatus(error));
    }

    [Fact]
    public void A_rate_limit_explains_why_pending_movies_are_waiting()
    {
        var progress = new MetadataProgress(Pending: 5352, Matched: 648, RetryAfterSeconds: 30);

        Assert.Equal(648, progress.Processed);
        Assert.Equal(5352, progress.Pending);
        Assert.Equal(0, progress.Failed);
        Assert.Contains("648/6000 checked", progress.Describe());
        Assert.Contains("TMDB rate-limited: retrying in 30s", progress.Describe());
        Assert.True(progress.Describe().Length <= (progress with { RetryAfterSeconds = 0 }).Describe().Length);
        Assert.DoesNotContain("rate-limited", (progress with { RetryAfterSeconds = 0 }).Describe());
        Assert.DoesNotContain("rate-limited", (progress with { Pending = 0 }).Describe());
    }

    [Fact]
    public void An_idle_loader_adds_nothing_to_the_status()
    {
        Assert.Equal("", new MetadataProgress().Describe());
        Assert.Equal("Ready", new MetadataProgress().WithStatus("Ready"));
    }
}
