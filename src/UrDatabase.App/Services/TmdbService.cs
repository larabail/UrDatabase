using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    public sealed class TmdbService : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly string _posterCacheDir;
        private readonly string _imageSize;
        private readonly bool _downloadPosters;
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public sealed class TmdbCredits
        {
            public List<TmdbCast> Cast { get; set; } = new();
            public List<TmdbCrew> Crew { get; set; } = new();
        }

        public sealed class TmdbCast { public string Name { get; set; } = ""; public string? Character { get; set; } }
        public sealed class TmdbCrew { public string Name { get; set; } = ""; public string? Job { get; set; } }

        public TmdbService(string apiKey, string posterCacheDir, string imageSize, bool downloadPosters, HttpMessageHandler? handler = null)
        {
            _apiKey = apiKey ?? "";
            _posterCacheDir = Environment.ExpandEnvironmentVariables(posterCacheDir ?? "");
            _imageSize = string.IsNullOrWhiteSpace(imageSize) ? "w342" : imageSize.Trim();
            _downloadPosters = downloadPosters;

            _http = handler is null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(15);
            Directory.CreateDirectory(_posterCacheDir);
        }

        // Search movie by title + (optional) year. Returns poster path like "/abc123.jpg" or null.
        public async Task<(int? TmdbId, string? PosterPath)> SearchPosterAsync(string title, int? year, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(title))
                return (null, null);

            // TMDb search API (v3)
            // https://api.themoviedb.org/3/search/movie?api_key=...&query=...&year=...
            var url = $"https://api.themoviedb.org/3/search/movie?api_key={Uri.EscapeDataString(_apiKey)}&query={Uri.EscapeDataString(title)}";
            if (year.HasValue && year.Value > 1800) url += $"&year={year.Value}";

            using var resp = await _http.GetAsync(url, ct);
            if (resp.StatusCode == (HttpStatusCode)429) // rate limited
            {
                await Task.Delay(2000, ct);
                using var retry = await _http.GetAsync(url, ct);
                if (!retry.IsSuccessStatusCode) return (null, null);
                return await ExtractPosterAsync(retry, ct);
            }

            if (!resp.IsSuccessStatusCode) return (null, null);
            return await ExtractPosterAsync(resp, ct);

            async Task<(int?, string?)> ExtractPosterAsync(HttpResponseMessage r, CancellationToken c)
            {
                using var s = await r.Content.ReadAsStreamAsync(c);
                var doc = await JsonSerializer.DeserializeAsync<TmdbSearchResult>(s, _json, c);
                var hit = doc?.Results is { Count: > 0 } ? doc.Results[0] : null;
                return hit is null ? (null, null) : (hit.Id, hit.PosterPath);
            }
        }

        private string BuildImageUrl(string posterPath) =>
            $"https://image.tmdb.org/t/p/{_imageSize}/{posterPath.TrimStart('/')}";

        private async Task<string?> DownloadAsync(string url, string fileName, CancellationToken ct)
        {
            var dst = Path.Combine(_posterCacheDir, fileName);
            if (File.Exists(dst)) return dst;

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dstStream = File.Create(dst);
            await src.CopyToAsync(dstStream, ct);
            return dst;
        }

        // Bulk updater: fill movies.poster_path where missing.
        public async Task<int> UpdateMissingPostersAsync(SqliteConnection conn, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var movies = await conn.QueryAsync<(long Id, string Title, int? Year)>(
                "SELECT id, title, year FROM movies WHERE poster_path IS NULL OR poster_path = ''");

            int updated = 0;
            foreach (var m in movies)
            {
                ct.ThrowIfCancellationRequested();

                progress?.Report($"TMDb: {m.Title} ({m.Year?.ToString() ?? "n/a"})");
                var (tmdbId, posterPath) = await SearchPosterAsync(m.Title, m.Year, ct);

                if (!string.IsNullOrWhiteSpace(posterPath))
                {
                    string storedPath;
                    var url = BuildImageUrl(posterPath);

                    if (_downloadPosters)
                    {
                        var safe = $"{m.Id}.jpg"; // stable per movie
                        var local = await DownloadAsync(url, safe, ct);
                        storedPath = local ?? url; // fall back to URL if download fails
                    }
                    else
                    {
                        storedPath = url;
                    }

                    await conn.ExecuteAsync(
                        "UPDATE movies SET poster_path = @poster WHERE id = @id",
                        new { poster = storedPath, id = m.Id });

                    updated++;
                }

                // Gentle throttle to respect TMDb rate limits
                await Task.Delay(250, ct);
            }

            progress?.Report($"TMDb update complete. Posters added: {updated}");
            return updated;
        }

        public void Dispose() => _http.Dispose();

        // --- DTOs ---
        private sealed class TmdbSearchResult
        {
            [JsonPropertyName("results")] public List<TmdbMovie> Results { get; set; } = new();
        }

        private sealed class TmdbMovie
        {
            [JsonPropertyName("id")] public int Id { get; set; }
            [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
        }
        public sealed class TmdbDetails
        {
            public int Id { get; set; }
            public string? Title { get; set; }
            public string? Overview { get; set; }
            public string? BackdropPath { get; set; }
            public int? Runtime { get; set; }
            public double? VoteAverage { get; set; }
            public List<TmdbGenre> Genres { get; set; } = new();
        }

        public sealed class TmdbGenre
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
        }

        // Public helpers we already expose
        internal string BuildImageUrlPublic(string posterPath) => BuildImageUrl(posterPath);
        internal async Task<string?> DownloadForPublic(string url, string fileName, CancellationToken ct) => await DownloadAsync(url, fileName, ct);

        // New: fetch full details (we’ll first Search to get tmdbId, then call details)
        public async Task<TmdbDetails?> GetDetailsByTitleAsync(string title, int? year, CancellationToken ct)
        {
            var (id, _) = await SearchPosterAsync(title, year, ct);
            if (id is null) return null;

            var url = $"https://api.themoviedb.org/3/movie/{id}?api_key={Uri.EscapeDataString(_apiKey)}&language=en-US";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            var details = await JsonSerializer.DeserializeAsync<TmdbDetails>(s, _json, ct);
            if (details != null) details.Id = id.Value; // make sure Id is set from search
            return details;
        }


        public async Task<TmdbCredits?> GetCreditsByIdAsync(int tmdbId, CancellationToken ct)
        {
            var url = $"https://api.themoviedb.org/3/movie/{tmdbId}/credits?api_key={Uri.EscapeDataString(_apiKey)}&language=en-US";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            var credits = await JsonSerializer.DeserializeAsync<TmdbCredits>(s, _json, ct);
            return credits;
        }

    }
}
