using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace UrDatabase.Services
{
    /// <summary>
    /// Turns a poster reference into an Avalonia <see cref="Bitmap"/>. WPF converted a string
    /// straight to an ImageSource and fetched remote URLs by itself; Avalonia does neither, so
    /// the app has to load and cache bitmaps explicitly.
    /// </summary>
    public static class ImageLoader
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly ConcurrentDictionary<string, Bitmap> Cache = new();
        private static readonly SemaphoreSlim DownloadGate = new(6);

        public static async Task<Bitmap?> LoadAsync(string? source, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            if (Cache.TryGetValue(source, out var cached)) return cached;

            try
            {
                var bitmap = await Task.Run(() => LoadCoreAsync(source, ct), ct);
                if (bitmap is null) return null;

                // Another load may have won the race; keep a single instance per key.
                return Cache.GetOrAdd(source, bitmap);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch
            {
                // A missing or corrupt poster must never take the window down.
                return null;
            }
        }

        private static async Task<Bitmap?> LoadCoreAsync(string source, CancellationToken ct)
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                await DownloadGate.WaitAsync(ct);
                try
                {
                    var bytes = await Http.GetByteArrayAsync(uri, ct);
                    using var stream = new MemoryStream(bytes);
                    return new Bitmap(stream);
                }
                finally
                {
                    DownloadGate.Release();
                }
            }

            var path = PlatformPaths.Expand(source);
            if (!File.Exists(path)) return null;

            await using var file = File.OpenRead(path);
            return new Bitmap(file);
        }
    }
}
