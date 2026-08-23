using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Reads Academy Award nominations from the UrActor API.
    /// </summary>
    /// <remarks>
    /// Two things about this API are unusual enough to be worth stating at the call site.
    ///
    /// The key travels as the last segment of the path rather than in a header or a query string.
    /// That means it lands in server logs and in any URL this app might print, so it is never
    /// logged here: <see cref="Redact"/> takes it out of anything that reaches the log file, the
    /// same way <c>JellyfinClient</c> does for its access token.
    ///
    /// And there is no endpoint for "this film's awards". The archive is searched by name through
    /// the person endpoint, which matches against both the nominee and the context fields — so a
    /// film title finds its Best Picture nomination, where it is the nominee, and its craft
    /// nominations, where it is the context. The documented film endpoint needs the ceremony year
    /// as well, and the whole difficulty is that the app does not reliably know it. Which of the
    /// results belong to the film in hand is then <see cref="OscarMatch"/>'s problem.
    ///
    /// Matching upstream is exact and case-sensitive, which is why nothing here tries to be
    /// clever about a near miss: a title the Academy spells differently simply has no awards as
    /// far as this app is concerned, and that is a quieter failure than attaching another film's.
    /// </remarks>
    public sealed class UrActorService : IOscarsLookup, IDisposable
    {
        public const string ApiBaseUrl = "https://api.uractor.com";

        /// <summary>
        /// Upstream allows sixty requests a minute per key. Nothing here paces itself, because
        /// <see cref="OscarsService"/>'s cache means one film is asked about once, ever — but a
        /// 429 is still handled rather than treated as a failure, since a first run over a large
        /// library can reach it.
        /// </summary>
        private const int TooManyRequests = 429;

        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

        public UrActorService(string? apiKey, HttpMessageHandler? handler = null)
        {
            _apiKey = (apiKey ?? "").Trim();
            _http = handler is null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

        /// <summary>
        /// The URL for a title search. Both segments are escaped: a film called "Face/Off" would
        /// otherwise write a path segment of its own and ask for something that does not exist.
        /// </summary>
        public string BuildLookupUrl(string title) =>
            $"{ApiBaseUrl}/person/name={Uri.EscapeDataString(title ?? "")}/apikey={Uri.EscapeDataString(_apiKey)}";

        /// <summary>
        /// Everything the archive holds under this title, or null when the question could not be
        /// answered at all. The difference matters to the cache above this: an empty list is
        /// "the Academy never nominated it", which is true forever, and null is "ask again".
        /// Awards are an ornament on this screen, so no failure here is worth stopping a film
        /// opening for.
        /// </summary>
        public async Task<IReadOnlyList<OscarNomination>?> LookupAsync(string title, CancellationToken ct = default)
        {
            if (!IsAvailable || string.IsNullOrWhiteSpace(title)) return null;

            try
            {
                using var resp = await _http.GetAsync(BuildLookupUrl(title), ct);

                // A film the Academy never nominated is the commonest answer there is, and the API
                // says so with a 404. That is an answer, not a failure, and it is cached as one.
                if (resp.StatusCode == HttpStatusCode.NotFound) return Array.Empty<OscarNomination>();

                if ((int)resp.StatusCode == TooManyRequests)
                {
                    // Emphatically not an empty answer. Caching this would record "no awards"
                    // against whatever the user happened to be browsing when the minute ran out.
                    AppLog.Write("oscars.log", $"{title}: rate limited, will ask again later");
                    return null;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    AppLog.Write("oscars.log", $"{title}: HTTP {(int)resp.StatusCode}");
                    return null;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var payload = await JsonSerializer.DeserializeAsync<List<UrActorMatch>>(stream, _json, ct);
                return Convert(payload);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Write("oscars.log", Redact($"{title} lookup failed: {ex.Message}"));
                return null;
            }
        }

        /// <summary>
        /// Turns the wire shape into the app's own, dropping anything with no category or no
        /// nominee — a row that names neither cannot be printed.
        /// </summary>
        internal static IReadOnlyList<OscarNomination> Convert(IEnumerable<UrActorMatch>? payload)
        {
            var results = new List<OscarNomination>();
            if (payload is null) return results;

            foreach (var match in payload)
            {
                if (match?.Nomination is null) continue;
                if (string.IsNullOrWhiteSpace(match.Category)) continue;

                // The year arrives as a string, and is parsed with the invariant culture so a
                // comma-decimal locale cannot break it.
                if (!int.TryParse(match.Year, NumberStyles.None, CultureInfo.InvariantCulture, out var ceremony))
                    continue;

                var nominee = Join(match.Nomination.Primary);
                var detail = Join(match.Nomination.Secondary);
                if (nominee.Length == 0 && detail.Length == 0) continue;

                results.Add(new OscarNomination
                {
                    Ceremony = ceremony,
                    Category = match.Category.Trim(),
                    Nominee = nominee,
                    Detail = detail,
                    Won = match.Nomination.Won
                });
            }

            return results;
        }

        /// <summary>
        /// Both fields are arrays: a Best Picture nomination lists every producer, and an actor
        /// nominated for two films in one year lists both.
        /// </summary>
        private static string Join(IEnumerable<string>? values)
        {
            if (values is null) return "";

            var parts = new List<string>();
            foreach (var value in values)
                if (!string.IsNullOrWhiteSpace(value))
                    parts.Add(value.Trim());

            return string.Join(", ", parts);
        }

        /// <summary>
        /// Takes the key out of text bound for the log. It is a low-value credential — read-only
        /// access to public awards data — but it is still a credential, and a URL in an exception
        /// message carries the whole of it.
        /// </summary>
        internal string Redact(string text)
        {
            if (string.IsNullOrEmpty(text) || _apiKey.Length == 0) return text ?? "";

            return text
                .Replace(_apiKey, "***", StringComparison.Ordinal)
                .Replace(Uri.EscapeDataString(_apiKey), "***", StringComparison.Ordinal);
        }

        public void Dispose() => _http.Dispose();

        /// <summary>One result from the title search: a ceremony, a category, and the nomination.</summary>
        internal sealed class UrActorMatch
        {
            [JsonPropertyName("year")] public string? Year { get; set; }
            [JsonPropertyName("category")] public string? Category { get; set; }
            [JsonPropertyName("nomination")] public UrActorNomination? Nomination { get; set; }
        }

        internal sealed class UrActorNomination
        {
            [JsonPropertyName("primary")] public List<string>? Primary { get; set; }
            [JsonPropertyName("secondary")] public List<string>? Secondary { get; set; }
            [JsonPropertyName("won")] public bool Won { get; set; }
        }
    }
}
