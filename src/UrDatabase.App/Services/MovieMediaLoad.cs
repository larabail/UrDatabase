using System;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;

namespace UrDatabase.Services;

/// <summary>A probe belongs to one page and one selected copy, never the next page or file.</summary>
internal sealed class MovieMediaLoad : IDisposable
{
    private readonly MovieDetailsVm _movie;
    private readonly LocalMediaReader _reader;
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationToken _token;
    private readonly bool _refresh;
    private bool _disposed;
    public string? Path { get; }
    public LocalMediaResult? Result { get; private set; }

    public MovieMediaLoad(MovieDetailsVm movie, LocalMediaReader reader, CancellationToken ct = default,
        bool refresh = false, LocalMediaResult? previous = null)
    {
        _movie = movie;
        _reader = reader;
        _refresh = refresh;
        Path = PathFor(movie);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _token = _cancellation.Token;
        Result = previous;
    }

    public static string? PathFor(MovieDetailsVm movie) => movie.IsRemote ? movie.DownloadedPath : movie.FilePath;

    public bool Matches(MovieDetailsVm? movie) => !_token.IsCancellationRequested &&
        ReferenceEquals(movie, _movie) && string.Equals(Path, PathFor(movie!), StringComparison.Ordinal);

    public async Task LoadAsync()
    {
        var result = await _reader.ReadAsync(Path, _refresh, _token).ConfigureAwait(false);
        _token.ThrowIfCancellationRequested();
        Result = result;
    }

    public bool TryApply(MovieDetailsVm? movie)
    {
        if (Result is null || !Matches(movie)) return false;
        movie!.Media = Result.Info;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
