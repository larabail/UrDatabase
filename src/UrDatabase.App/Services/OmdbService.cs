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
        public async Task<double?> LookupAsync(string imdbId, CancellationToken ct = default)
        {
            if (!IsAvailable || string.IsNullOrWhiteSpace(imdbId)) return null;

            try
            {
                using var resp = await _http.GetAsync(BuildLookupUrl(imdbId), ct);
                if (!resp.IsSuccessStatusCode)
                {
                    AppLog.Write("omdb.log", $"{imdbId}: HTTP {(int)resp.StatusCode}");
                    return null;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var payload = await JsonSerializer.DeserializeAsync<OmdbResponse>(stream, _json, ct);
                return ParseRating(payload);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Write("omdb.log", $"{imdbId} lookup failed: {ex.Message}");
                return null;
            }
        }

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
