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
    /// <summary>
    /// The app's only external data source: TMDB (api.themoviedb.org for metadata,
    /// image.tmdb.org for artwork). No other network calls are made anywhere in the app.
    /// </summary>
    public sealed class TmdbService : IDisposable
    {
        public const string ApiBaseUrl = "https://api.themoviedb.org/3";
        public const string ImageBaseUrl = "https://image.tmdb.org/t/p";

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
            _posterCacheDir = ResolveCacheDir(posterCacheDir);
            _imageSize = string.IsNullOrWhiteSpace(imageSize) ? "w342" : imageSize.Trim();
            _downloadPosters = downloadPosters;

            _http = handler is null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        /// <summary>
        /// Posters are only cached to disk on request, so the directory is created lazily —
        /// creating it eagerly made construction fail whenever the setting was blank.
        /// </summary>
        private static string ResolveCacheDir(string? configured)
        {
            var expanded = PlatformPaths.Expand(configured);
            return string.IsNullOrWhiteSpace(expanded) ? PlatformPaths.DefaultPosterCacheDir : expanded;
        }

        // ---------- URL construction ----------

        public string BuildSearchUrl(string title, int? year)
        {
            var url = $"{ApiBaseUrl}/search/movie?api_key={Uri.EscapeDataString(_apiKey)}&query={Uri.EscapeDataString(title ?? "")}";
            if (year.HasValue && year.Value > 1800) url += $"&year={year.Value}";
            return url;
        }

        public string BuildDetailsUrl(int tmdbId) =>
            $"{ApiBaseUrl}/movie/{tmdbId}?api_key={Uri.EscapeDataString(_apiKey)}&language=en-US";

        public string BuildCreditsUrl(int tmdbId) =>
            $"{ApiBaseUrl}/movie/{tmdbId}/credits?api_key={Uri.EscapeDataString(_apiKey)}&language=en-US";

        public string BuildImageUrl(string posterPath) =>
            $"{ImageBaseUrl}/{_imageSize}/{(posterPath ?? "").TrimStart('/')}";

        // ---------- API calls ----------

        /// <summary>Search a movie by title and optional year. Returns the TMDB id and poster path.</summary>
        public async Task<(int? TmdbId, string? PosterPath)> SearchPosterAsync(string title, int? year, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(title))
                return (null, null);

            var url = BuildSearchUrl(title, year);

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

        private async Task<string?> DownloadAsync(string url, string fileName, CancellationToken ct)
        {
            Directory.CreateDirectory(_posterCacheDir);
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
                var (_, posterPath) = await SearchPosterAsync(m.Title, m.Year, ct);

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
            [JsonPropertyName("id")] public int Id { get; set; }
            [JsonPropertyName("title")] public string? Title { get; set; }
            [JsonPropertyName("overview")] public string? Overview { get; set; }
            // TMDB returns snake_case; case-insensitive matching alone never bound these.
            [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; set; }
            [JsonPropertyName("imdb_id")] public string? ImdbId { get; set; }
            [JsonPropertyName("runtime")] public int? Runtime { get; set; }
            [JsonPropertyName("vote_average")] public double? VoteAverage { get; set; }
            [JsonPropertyName("genres")] public List<TmdbGenre> Genres { get; set; } = new();
        }

        public sealed class TmdbGenre
        {
            [JsonPropertyName("id")] public int Id { get; set; }
            [JsonPropertyName("name")] public string Name { get; set; } = "";
        }

        internal string BuildImageUrlPublic(string posterPath) => BuildImageUrl(posterPath);
        internal async Task<string?> DownloadForPublic(string url, string fileName, CancellationToken ct) => await DownloadAsync(url, fileName, ct);

        /// <summary>Search for the title, then fetch the full record for the first hit.</summary>
        public async Task<TmdbDetails?> GetDetailsByTitleAsync(string title, int? year, CancellationToken ct)
        {
            var (id, _) = await SearchPosterAsync(title, year, ct);
            if (id is null) return null;

            using var resp = await _http.GetAsync(BuildDetailsUrl(id.Value), ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            var details = await JsonSerializer.DeserializeAsync<TmdbDetails>(s, _json, ct);
            if (details != null) details.Id = id.Value; // make sure Id is set from search
            return details;
        }

        public async Task<TmdbCredits?> GetCreditsByIdAsync(int tmdbId, CancellationToken ct)
        {
            using var resp = await _http.GetAsync(BuildCreditsUrl(tmdbId), ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<TmdbCredits>(s, _json, ct);
        }
    }
}
