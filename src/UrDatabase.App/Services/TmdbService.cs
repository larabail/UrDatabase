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

        /// <summary>What a download is called before it is a poster.</summary>
        internal const string StagingSuffix = ".part";

        /// <summary>
        /// How long a staging file has to have sat untouched before it is treated as wreckage.
        /// Comfortably longer than any request can take, so a live download — including one in
        /// another copy of the app sharing this cache — is never swept away.
        /// </summary>
        internal static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(1);

        private int _swept;

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

        /// <summary>
        /// Films TMDB thinks somebody who watched this one would want next.
        /// </summary>
        /// <remarks>
        /// <c>/recommendations</c> rather than <c>/similar</c>. The two are easy to confuse and
        /// answer different questions: <c>similar</c> is computed from shared genres and keywords,
        /// so it returns a list of things in the same bucket, while <c>recommendations</c> is
        /// derived from what people who rated this film also rated. For "what should I put on
        /// next" the second is the question being asked.
        /// </remarks>
        public string BuildRecommendationsUrl(int tmdbId) =>
            $"{ApiBaseUrl}/movie/{tmdbId}/recommendations?api_key={Uri.EscapeDataString(_apiKey)}&language=en-US&page=1";

        public string BuildImageUrl(string posterPath) =>
            $"{ImageBaseUrl}/{_imageSize}/{(posterPath ?? "").TrimStart('/')}";

        // ---------- API calls ----------

        /// <summary>
        /// Every TMDB result for a title, in TMDB's own order and with nothing filtered out.
        ///
        /// This is what the "Wrong film?" picker shows, and it deliberately does not go through
        /// <see cref="TmdbMatch"/>: the rules there exist to stop the app choosing on its own, and
        /// applying them to a list a person is reading would hide the very result they opened the
        /// picker to choose. A human looking at ten posters is better evidence than any rule here.
        /// </summary>
        public async Task<IReadOnlyList<TmdbMatch.Candidate>> SearchAsync(string title, int? year, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(title))
                return Array.Empty<TmdbMatch.Candidate>();

            using var resp = await GetWithRetryAsync(BuildSearchUrl(title, year), ct);
            if (resp is null || !resp.IsSuccessStatusCode) return Array.Empty<TmdbMatch.Candidate>();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var doc = await JsonSerializer.DeserializeAsync<TmdbSearchResult>(stream, _json, ct);
            return doc?.Results ?? new List<TmdbMatch.Candidate>();
        }

        /// <summary>
        /// TMDB's recommendations for a film, in its own order — which is a relevance ranking and
        /// is preserved rather than re-sorted. Empty for any failure at all: this fills a shelf
        /// that is hidden when it has nothing in it, so there is nothing here worth reporting.
        /// </summary>
        public async Task<IReadOnlyList<TmdbMatch.Candidate>> GetRecommendationsAsync(int tmdbId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_apiKey) || tmdbId <= 0)
                return Array.Empty<TmdbMatch.Candidate>();

            try
            {
                using var resp = await GetWithRetryAsync(BuildRecommendationsUrl(tmdbId), ct);
                if (resp is null || !resp.IsSuccessStatusCode) return Array.Empty<TmdbMatch.Candidate>();

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var doc = await JsonSerializer.DeserializeAsync<TmdbSearchResult>(stream, _json, ct);
                return doc?.Results ?? new List<TmdbMatch.Candidate>();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Write("posters.log", $"recommendations for {tmdbId} failed: {ex.Message}");
                return Array.Empty<TmdbMatch.Candidate>();
            }
        }

        /// <summary>
        /// Search a movie by title and optional year. Returns the TMDB id and poster path of the
        /// result that is actually this film, or nulls when none of them is — see
        /// <see cref="TmdbMatch"/> for why refusing beats returning TMDB's first guess.
        /// </summary>
        public async Task<(int? TmdbId, string? PosterPath)> SearchPosterAsync(string title, int? year, CancellationToken ct)
        {
            var results = await SearchAsync(title, year, ct);
            var hit = TmdbMatch.ChooseBest(results, title, year);
            return hit is null ? (null, null) : (hit.Id, hit.PosterPath);
        }

        /// <summary>
        /// One GET, retried once after a pause when TMDB rate limits it. Null when even the retry
        /// failed, which callers treat as "TMDB had nothing" rather than as an error: a missing
        /// poster is not worth interrupting somebody over.
        /// </summary>
        private async Task<HttpResponseMessage?> GetWithRetryAsync(string url, CancellationToken ct)
        {
            var resp = await _http.GetAsync(url, ct);
            if (resp.StatusCode != (HttpStatusCode)429) return resp;

            resp.Dispose();
            await Task.Delay(2000, ct);
            return await _http.GetAsync(url, ct);
        }

        /// <summary>
        /// Fetches a poster into the cache and returns where it landed, or <c>null</c> when the
        /// response was not artwork worth keeping.
        ///
        /// The download is staged and only then moved into place, because the file being present
        /// is the entire cache lookup: <c>File.Exists</c> below is what every later call asks,
        /// and it cannot tell a finished poster from an abandoned one. Writing straight to the
        /// destination meant a closed window, a cancelled fetch, a full disk or a dropped
        /// connection left a fragment that answered that question with "yes" forever, and the
        /// only way back was deleting the cache by hand.
        /// </summary>
        private async Task<string?> DownloadAsync(string url, string fileName, CancellationToken ct)
        {
            Directory.CreateDirectory(_posterCacheDir);
            SweepOnce();

            var dst = Path.Combine(_posterCacheDir, fileName);
            if (File.Exists(dst)) return dst;

            // Headers first, then the body straight to disk. Buffering the whole poster into
            // memory before writing it — which is what GetAsync does by default — both wastes
            // the memory and hides the failure this method exists to survive: a connection that
            // dies mid-image would surface from the request rather than from the copy, and the
            // fragment it left behind would still be sitting at the destination.
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            if (!PosterContent.IsPlausibleContentType(resp.Content.Headers.ContentType?.MediaType)) return null;

            // Staged beside the destination, not in the system temp directory: File.Move is only
            // atomic within one volume, and a cache configured onto another disk would quietly
            // turn the move back into the copy this exists to avoid. The GUID keeps two fetches
            // of the same poster — or a second copy of the app — off each other's staging file.
            var staging = Path.Combine(_posterCacheDir, $"{fileName}.{Guid.NewGuid():N}{StagingSuffix}");

            try
            {
                var head = new byte[PosterContent.SignatureLength];
                int headLength;
                long written;

                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                await using (var staged = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Read past the signature first so it can be judged without reading the file
                    // back afterwards. CopyToAsync then continues from wherever that left off.
                    headLength = await ReadSignatureAsync(src, head, ct);
                    await staged.WriteAsync(head.AsMemory(0, headLength), ct);
                    await src.CopyToAsync(staged, ct);

                    written = staged.Position;
                }

                if (written == 0 || !PosterContent.LooksLikeImage(head.AsSpan(0, headLength)))
                {
                    TryDelete(staging);
                    return null;
                }

                File.Move(staging, dst, overwrite: true);
                return dst;
            }
            catch
            {
                // Including cancellation. Leaving the fragment would be the original bug with an
                // extra step: nothing reads a .part file, but nothing would clear it up either.
                TryDelete(staging);
                throw;
            }
        }

        /// <summary>
        /// Fills <paramref name="buffer"/> from <paramref name="src"/>, returning how much
        /// arrived. A single read is not enough: a stream is free to hand back one byte at a
        /// time, and a signature judged on a short read would reject a real poster.
        /// </summary>
        private static async Task<int> ReadSignatureAsync(Stream src, byte[] buffer, CancellationToken ct)
        {
            var total = 0;

            while (total < buffer.Length)
            {
                var read = await src.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
                if (read == 0) break;
                total += read;
            }

            return total;
        }

        /// <summary>
        /// Best effort, and deliberately silent. This runs while another failure is already on
        /// its way up, and a staging file that cannot be removed is worth strictly less than the
        /// exception explaining why the poster never arrived.
        /// </summary>
        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>
        /// Clears out staging files nothing is going to finish, once per service. Every failure
        /// this process can see is already cleaned up where it happens; this is for the ones it
        /// cannot — a force quit, a lost power supply, a delete refused by a virus scanner that
        /// happened to be reading the file. Nothing ever reads a staging file, so what is left
        /// is invisible rather than harmful, but a cache that only ever grows is still a cache
        /// somebody eventually finds and wonders about.
        /// </summary>
        private void SweepOnce()
        {
            if (Interlocked.Exchange(ref _swept, 1) != 0) return;

            SweepStaleStaging(_posterCacheDir, StaleStagingAge);
        }

        /// <summary>
        /// Deletes <c>*.part</c> files in <paramref name="directory"/> last written more than
        /// <paramref name="olderThan"/> ago, and reports how many went.
        /// </summary>
        /// <remarks>
        /// The age is what makes this safe to run while another copy of the app is downloading
        /// into the same cache. A request is abandoned after fifteen seconds, so a staging file
        /// untouched for an hour cannot belong to a download anybody is still waiting on;
        /// sweeping on name alone would delete a live one out from under it.
        /// </remarks>
        internal static int SweepStaleStaging(string directory, TimeSpan olderThan)
        {
            var removed = 0;

            try
            {
                var cutoff = DateTime.UtcNow - olderThan;

                foreach (var file in Directory.EnumerateFiles(directory, $"*{StagingSuffix}"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                        File.Delete(file);
                        removed++;
                    }
                    catch
                    {
                        // Locked, vanished, or not ours to delete. The next run tries again.
                    }
                }
            }
            catch
            {
                // No cache directory, or one that cannot be listed. Not worth a word.
            }

            return removed;
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

                if (tmdbId is not null && !string.IsNullOrWhiteSpace(posterPath))
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
                        "UPDATE movies SET poster_path = @poster, tmdb_id = @tmdb WHERE id = @id",
                        new { poster = storedPath, tmdb = tmdbId.Value, id = m.Id });

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
            [JsonPropertyName("results")] public List<TmdbMatch.Candidate> Results { get; set; } = new();
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

        /// <summary>Search for the title, then fetch the full record for the film that matched.</summary>
        public async Task<TmdbDetails?> GetDetailsByTitleAsync(string title, int? year, CancellationToken ct)
        {
            var (id, _) = await SearchPosterAsync(title, year, ct);
            return id is null ? null : await GetDetailsByIdAsync(id.Value, ct);
        }

        /// <summary>
        /// The full record for a film TMDB has already been identified as. This is what a
        /// corrected match reads: once somebody has said which film this is, asking by title again
        /// would throw their answer away and re-derive the wrong one.
        /// </summary>
        public async Task<TmdbDetails?> GetDetailsByIdAsync(int tmdbId, CancellationToken ct)
        {
            using var resp = await _http.GetAsync(BuildDetailsUrl(tmdbId), ct);
            if (!resp.IsSuccessStatusCode) return null;

            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            var details = await JsonSerializer.DeserializeAsync<TmdbDetails>(s, _json, ct);

            // TMDB's details response does carry its own id, but a malformed or partial body would
            // leave it zero, and every caller uses it to ask for credits next.
            if (details != null) details.Id = tmdbId;
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
