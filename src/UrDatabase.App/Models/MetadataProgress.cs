using System;

namespace UrDatabase.Models;

/// <summary>Unique films in the current metadata pass, independent of how many have artwork.</summary>
public sealed record MetadataProgress(int Pending = 0, int Matched = 0, int Unmatched = 0, int Failed = 0,
    int RetryAfterSeconds = 0)
{
    public int Processed => Matched + Unmatched + Failed;
    public int Total => Pending + Processed;

    public string Describe() => Total == 0 ? "" :
        $"Metadata: {Processed}/{Total} checked · " +
        (Pending > 0 && RetryAfterSeconds > 0
            ? $"TMDB rate-limited: retrying in {RetryAfterSeconds}s"
            : $"{Pending} pending · {Unmatched} unmatched · {Failed} failed");

    public string WithStatus(string status) => Total == 0 ? status : status + Environment.NewLine + Describe();
}
