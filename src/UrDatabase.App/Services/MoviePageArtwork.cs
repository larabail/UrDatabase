using System;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services;

public sealed record MoviePageImage<T>(T? Image, bool Failed) where T : class;

public static class MoviePageArtwork
{
    /// <summary>Always asks for the source again; a failed or cancelled request never clears working artwork.</summary>
    public static async Task<MoviePageImage<T>> RetryAsync<T>(
        string? source,
        T? current,
        Func<string?, CancellationToken, Task<T?>> load,
        CancellationToken ct = default) where T : class
    {
        if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(source)) return new(current, false);
        try
        {
            var image = await load(source, ct);
            return ct.IsCancellationRequested ? new(current, false) : new(image ?? current, image is null);
        }
        catch
        {
            return new(current, !ct.IsCancellationRequested);
        }
    }
}
