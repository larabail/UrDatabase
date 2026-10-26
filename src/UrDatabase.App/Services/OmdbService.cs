using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>
    /// Reads IMDb ratings from the OMDb API, keyed by the IMDb id TMDB reports, so a title is
    /// matched exactly rather than by name and year.
    /// </summary>
    public sealed class OmdbService : IImdbRatingLookup, IDisposable
    {
        public const string ApiBaseUrl = "https://www.omdbapi.com/";

        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

        public OmdbService(string? apiKey, HttpMessageHandler? handler = null)
        {
            _apiKey = (apiKey ?? "").Trim();
            _http = handler is null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        /// <summary>False when no key is configured, in which case no request is ever made.</summary>
        public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

        public string BuildLookupUrl(string imdbId) =>
            $"{ApiBaseUrl}?i={Uri.EscapeDataString(imdbId ?? "")}&apikey={Uri.EscapeDataString(_apiKey)}";

        /// <summary>
        /// Returns the IMDb rating, or null when it is unavailable for any reason. The rating is an
        /// optional enhancement, so every failure path degrades quietly instead of throwing.
        /// </summary>
        public async Task<double?> LookupAsync(string imdbId, CancellationToken ct = default) =>
            (await LookupResultAsync(imdbId, ct).ConfigureAwait(false)).Rating;

        /// <summary>
        /// The rating, or why there is none: an answer about the title, the day's limit, a refused
        /// key, or a failure. Never throws for an upstream problem.
        /// </summary>
        /// <remarks>
        /// OMDb answers a spent quota with HTTP 401 and <c>"Request limit reached!"</c>, and a bad
        /// key with 401 and <c>"Invalid API key!"</c>. Both used to come back as "no rating" and be
        /// cached as such, so one afternoon over the limit took the rating away from every film
        /// opened during it, permanently.
        /// </remarks>
        public async Task<ImdbLookupResult> LookupResultAsync(string imdbId, CancellationToken ct = default)
        {
            if (!IsAvailable || string.IsNullOrWhiteSpace(imdbId)) return ImdbLookupResult.Refused;

            try
            {
                using var resp = await _http.GetAsync(BuildLookupUrl(imdbId), ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                OmdbResponse? payload = null;
                try { payload = JsonSerializer.Deserialize<OmdbResponse>(body, _json); }
                catch (JsonException) when (!resp.IsSuccessStatusCode) { }

                var refusal = Classify(payload?.Error);
                if (refusal is not null)
                {
                    AppLog.Write("omdb.log", $"{imdbId}: HTTP {(int)resp.StatusCode} {payload?.Error}");
                    return refusal.Value;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    AppLog.Write("omdb.log", $"{imdbId}: HTTP {(int)resp.StatusCode}");
                    return resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? ImdbLookupResult.Refused
                        : ImdbLookupResult.Failed;
                }

                if (payload is not null
                    && string.Equals(payload.Response?.Trim(), "False", StringComparison.OrdinalIgnoreCase)
                    && !IsNotFound(payload.Error))
                {
                    AppLog.Write("omdb.log", $"{imdbId}: OMDb error: {payload.Error}");
                    return ImdbLookupResult.Failed;
                }

                return ImdbLookupResult.Of(ParseRating(payload));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Write("omdb.log", $"{imdbId} lookup failed: {ex.Message}");
                return ImdbLookupResult.Failed;
            }
        }

        /// <summary>The two errors that are about the key or the quota rather than the title.</summary>
        internal static ImdbLookupResult? Classify(string? error)
        {
            if (string.IsNullOrWhiteSpace(error)) return null;
            if (error.Contains("limit", StringComparison.OrdinalIgnoreCase)) return ImdbLookupResult.LimitReached;
            if (error.Contains("API key", StringComparison.OrdinalIgnoreCase)) return ImdbLookupResult.Refused;
            return null;
        }

        /// <summary>
        /// OMDb's ways of saying it holds nothing for the id, which is an answer about the title.
        /// Any other error — "Error getting data." — is OMDb having a bad moment, and is not.
        /// </summary>
        private static bool IsNotFound(string? error) =>
            string.IsNullOrWhiteSpace(error)
            || error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Incorrect IMDb ID", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// OMDb reports the rating as a string and uses the literal "N/A" when it has none, so the
        /// value is parsed with the invariant culture — a comma-decimal locale must not break it.
        /// </summary>
        internal static double? ParseRating(OmdbResponse? payload)
        {
            if (payload is null) return null;

            if (!string.IsNullOrWhiteSpace(payload.Response) &&
                payload.Response.Equals("False", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(payload.Error))
                    AppLog.Write("omdb.log", $"OMDb error: {payload.Error}");
                return null;
            }

            var raw = payload.ImdbRating?.Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                return null;

            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating)
                ? rating
                : null;
        }

        public void Dispose() => _http.Dispose();

        internal sealed class OmdbResponse
        {
            [JsonPropertyName("imdbRating")] public string? ImdbRating { get; set; }
            [JsonPropertyName("imdbID")] public string? ImdbId { get; set; }
            [JsonPropertyName("Response")] public string? Response { get; set; }
            [JsonPropertyName("Error")] public string? Error { get; set; }
        }
    }
}
