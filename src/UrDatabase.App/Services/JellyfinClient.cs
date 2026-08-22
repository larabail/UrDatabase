using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// A failure the user can do something about, phrased for a dialog rather than a log. Every
    /// path out of <see cref="JellyfinClient"/> that is not success raises one of these, so the
    /// window never has to interpret an HTTP status or a socket error itself.
    /// </summary>
    public sealed class JellyfinException : Exception
    {
        public JellyfinException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Reads a Jellyfin server's movie library. Nothing it fetches is stored on this machine
    /// except metadata: films are streamed, never downloaded.
    ///
    /// Deliberately narrow. It authenticates, finds the movie library, lists it, and builds the
    /// two URLs the rest of the app needs. It does not transcode, does not touch series, and
    /// never asks TMDB for anything — a Jellyfin item arrives complete, so a library from a
    /// server works fully on a build with no TMDB key at all.
    /// </summary>
    public sealed class JellyfinClient : IDisposable
    {
        /// <summary>How this app identifies itself in the Jellyfin authorization header.</summary>
        public const string ClientName = "UrDatabase";

        /// <summary>Items per request. Jellyfin caps a page well above this; 100 keeps each response small.</summary>
        public const int PageSize = 100;

        /// <summary>
        /// Everything the list needs in one pass. Without <c>Fields</c> Jellyfin returns a stub
        /// with no genres, overview or provider ids, and the app would be back to guessing.
        /// </summary>
        public const string ItemFields = "Genres,Overview,ProviderIds,ProductionYear,RunTimeTicks,CommunityRating";

        private readonly HttpClient _http;
        private readonly JellyfinSettings _settings;
        private readonly string _deviceId;
        private readonly string _deviceName;
        private readonly string _version;

        private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

        private string? _token;
        private string? _userId;

        public JellyfinClient(
            JellyfinSettings settings,
            string? deviceId = null,
            string? deviceName = null,
            string? version = null,
            HttpMessageHandler? handler = null,
            TimeSpan? timeout = null)
        {
            _settings = settings ?? new JellyfinSettings();
            _deviceId = SanitizeHeaderValue(deviceId, "unknown-device");
            _deviceName = SanitizeHeaderValue(deviceName ?? SafeMachineName(), "desktop");
            _version = SanitizeHeaderValue(version, "0.0.0");

            _http = handler is null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = timeout ?? TimeSpan.FromSeconds(15);
        }

        public bool IsConfigured => _settings.IsConfigured;

        /// <summary>The server as configured, safe to show a user: it is their own address.</summary>
        public string ServerUrl => _settings.ServerUrl;

        /// <summary>Set once <see cref="ConnectAsync"/> has run. Never hardcoded, always resolved by name.</summary>
        public string? UserId => _userId;

        // ---------- headers and URLs ----------

        /// <summary>
        /// The <c>Authorization</c> value Jellyfin expects. The token is folded into the same
        /// header whether it came from a password sign-in or from an API key, because the server
        /// treats both identically once issued.
        /// </summary>
        public string BuildAuthorizationHeader(string? token)
        {
            var builder = new StringBuilder("MediaBrowser ");
            builder.Append(CultureInfo.InvariantCulture, $"Client=\"{ClientName}\", ");
            builder.Append(CultureInfo.InvariantCulture, $"Device=\"{_deviceName}\", ");
            builder.Append(CultureInfo.InvariantCulture, $"DeviceId=\"{_deviceId}\", ");
            builder.Append(CultureInfo.InvariantCulture, $"Version=\"{_version}\"");

            if (!string.IsNullOrWhiteSpace(token))
                builder.Append(CultureInfo.InvariantCulture, $", Token=\"{token.Trim()}\"");

            return builder.ToString();
        }

        public Uri BuildUri(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(_settings.ServerUrl))
                throw new JellyfinException("No Jellyfin server address is configured.");

            return new Uri($"{_settings.ServerUrl}/{relativePath.TrimStart('/')}");
        }

        /// <summary>
        /// The direct play URL. <c>static=true</c> asks Jellyfin for the original file rather than
        /// a transcode, which it serves with range support so the player can seek.
        ///
        /// The token rides in the query string because a media player is handed a bare URL and has
        /// nowhere to put a header. That makes this string a credential: it must never be logged,
        /// shown in a dialog or written to disk.
        /// </summary>
        public string BuildStreamUrl(string itemId)
        {
            var url = $"{_settings.ServerUrl}/Videos/{Uri.EscapeDataString(itemId ?? "")}/stream?static=true";
            return string.IsNullOrWhiteSpace(_token)
                ? url
                : $"{url}&api_key={Uri.EscapeDataString(_token)}";
        }

        /// <summary>
        /// Poster URL. Jellyfin serves images without authentication, so this one is safe to hand
        /// to the image cache and safe to log.
        /// </summary>
        public string BuildPrimaryImageUrl(string itemId, string? imageTag, int maxWidth = 342)
        {
            var url = $"{_settings.ServerUrl}/Items/{Uri.EscapeDataString(itemId ?? "")}/Images/Primary" +
                      $"?maxWidth={maxWidth.ToString(CultureInfo.InvariantCulture)}";

            return string.IsNullOrWhiteSpace(imageTag) ? url : $"{url}&tag={Uri.EscapeDataString(imageTag)}";
        }

        /// <summary>
        /// Backdrop URL for the details window. A film with no backdrop simply 404s and the image
        /// stays blank, which is the same thing that happens when TMDB has none.
        /// </summary>
        public string BuildBackdropUrl(string itemId, int maxWidth = 1280) =>
            $"{_settings.ServerUrl}/Items/{Uri.EscapeDataString(itemId ?? "")}/Images/Backdrop/0" +
            $"?maxWidth={maxWidth.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>
        /// Removes a token from anything about to be written to a log. Called on every message
        /// this class logs, because a stream URL carries one in plain sight.
        /// </summary>
        public static string Redact(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var result = text;
            foreach (var marker in new[] { "api_key=", "ApiKey=", "Token=\"" })
            {
                var index = 0;
                while ((index = result.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var start = index + marker.Length;
                    var end = start;
                    var terminator = marker.EndsWith('"') ? '"' : '&';
                    while (end < result.Length && result[end] != terminator && result[end] != ' ') end++;

                    result = result[..start] + "REDACTED" + result[end..];
                    index = start + "REDACTED".Length;
                }
            }

            return result;
        }

        // ---------- connection ----------

        /// <summary>
        /// Signs in and resolves the user id. Safe to call repeatedly; the work happens once.
        /// </summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (!IsConfigured)
                throw new JellyfinException("Jellyfin is not configured.");

            if (_token is not null && _userId is not null) return;

            if (_settings.UsesUserAccount)
            {
                await AuthenticateByNameAsync(ct);
                return;
            }

            _token = _settings.ApiKey.Trim();
            _userId = await ResolveUserIdAsync(_settings.Username, ct);
        }

        /// <summary>
        /// The preferred sign-in: a user token is scoped to one account, whereas every Jellyfin
        /// API key is administrative. The owner's account is not an administrator, so this is also
        /// the only route that does not require borrowing someone else's authority.
        /// </summary>
        private async Task AuthenticateByNameAsync(CancellationToken ct)
        {
            var payload = JsonSerializer.Serialize(new { Username = _settings.Username, Pw = _settings.Password ?? "" });

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("Users/AuthenticateByName"))
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", BuildAuthorizationHeader(null));

            using var response = await SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new JellyfinException("Jellyfin rejected that username and password.");

            // Jellyfin always has this endpoint, so a 404 means the address is answering but is
            // not Jellyfin — most often a reverse proxy reached by an address it does not route.
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new JellyfinException(
                    JellyfinDiagnostics.Describe(JellyfinConnectionState.NotJellyfin, _settings.ServerUrl, 404));

            if (!response.IsSuccessStatusCode)
                throw new JellyfinException($"Jellyfin refused the sign-in (HTTP {(int)response.StatusCode}).");

            var result = await ReadAsync<JellyfinAuthResult>(response, ct);

            if (string.IsNullOrWhiteSpace(result?.AccessToken) || string.IsNullOrWhiteSpace(result?.User?.Id))
                throw new JellyfinException("Jellyfin accepted the sign-in but returned no session.");

            _token = result.AccessToken.Trim();
            _userId = result.User.Id.Trim();
        }

        /// <summary>
        /// Finds a user by name. A GUID is never configured or assumed: it differs per server and
        /// nobody can be expected to know theirs.
        /// </summary>
        public async Task<string> ResolveUserIdAsync(string? username, CancellationToken ct = default)
        {
            var users = await GetAsync<List<JellyfinUserDto>>("Users", ct) ?? new List<JellyfinUserDto>();

            if (users.Count == 0)
                throw new JellyfinException("That Jellyfin server reports no users.");

            if (string.IsNullOrWhiteSpace(username))
                return users[0].Id;

            var match = users.FirstOrDefault(u => string.Equals(u.Name, username.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match is null)
                throw new JellyfinException($"No Jellyfin user called \"{username.Trim()}\" exists on that server.");

            return match.Id;
        }

        /// <summary>
        /// The id of the movie library, chosen by collection type rather than by name or id, so it
        /// keeps working when a library is renamed and needs nothing hardcoded. Series libraries
        /// are skipped: this app only understands films.
        /// </summary>
        public async Task<string> ResolveMovieLibraryIdAsync(string userId, CancellationToken ct = default)
        {
            var views = await GetAsync<JellyfinItemsDtoOfViews>($"Users/{Uri.EscapeDataString(userId)}/Views", ct);
            var libraries = views?.Items ?? new List<JellyfinViewDto>();

            var movieLibraries = libraries
                .Where(v => string.Equals(v.CollectionType, "movies", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (movieLibraries.Count == 0)
                throw new JellyfinException("That Jellyfin server has no movie library.");

            if (!string.IsNullOrWhiteSpace(_settings.LibraryName))
            {
                var named = movieLibraries.FirstOrDefault(v =>
                    string.Equals(v.Name, _settings.LibraryName.Trim(), StringComparison.OrdinalIgnoreCase));

                if (named is null)
                    throw new JellyfinException($"No movie library called \"{_settings.LibraryName.Trim()}\" exists on that server.");

                return named.Id;
            }

            return movieLibraries[0].Id;
        }

        // ---------- the library ----------

        /// <summary>
        /// Every film in the movie library, fetched a page at a time. Progress is reported per
        /// page rather than per film so a slow server still says something without flooding the
        /// status line.
        /// </summary>
        public async Task<IReadOnlyList<JellyfinMovie>> GetMoviesAsync(
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            await ConnectAsync(ct);

            var userId = _userId!;
            var libraryId = await ResolveMovieLibraryIdAsync(userId, ct);

            var movies = new List<JellyfinMovie>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var startIndex = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var path =
                    $"Users/{Uri.EscapeDataString(userId)}/Items" +
                    $"?ParentId={Uri.EscapeDataString(libraryId)}" +
                    "&IncludeItemTypes=Movie&Recursive=true" +
                    "&SortBy=SortName&SortOrder=Ascending" +
                    $"&StartIndex={startIndex.ToString(CultureInfo.InvariantCulture)}" +
                    $"&Limit={PageSize.ToString(CultureInfo.InvariantCulture)}" +
                    $"&Fields={ItemFields}";

                var page = await GetAsync<JellyfinItemsDto>(path, ct);
                var items = page?.Items ?? new List<JellyfinItemDto>();
                if (items.Count == 0) break;

                foreach (var item in items)
                {
                    var movie = item.ToMovie();
                    if (movie is not null && seen.Add(movie.ItemId)) movies.Add(movie);
                }

                startIndex += items.Count;

                var total = page?.TotalRecordCount ?? movies.Count;
                progress?.Report($"Jellyfin: {Math.Min(startIndex, total)} of {total} films…");

                if (startIndex >= total) break;
            }

            return movies;
        }

        // ---------- plumbing ----------

        private async Task<T?> GetAsync<T>(string relativePath, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(relativePath));
            request.Headers.TryAddWithoutValidation("Authorization", BuildAuthorizationHeader(_token));

            using var response = await SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Every path this client asks for exists on every Jellyfin server, so a 404 here
                // is not a missing item — it is an address that answers without being Jellyfin.
                var state = JellyfinDiagnostics.FromStatusCode(response.StatusCode);
                throw new JellyfinException(
                    JellyfinDiagnostics.Describe(state, _settings.ServerUrl, (int)response.StatusCode));
            }

            return await ReadAsync<T>(response, ct);
        }

        /// <summary>
        /// Asks the server to identify itself, and reports which of the failure modes it found.
        /// Needs no credentials — <c>/System/Info/Public</c> is the one endpoint Jellyfin answers
        /// to anybody — so it can be run before a sign-in has ever succeeded, which is exactly
        /// when the answer is wanted.
        ///
        /// Never throws for a connection problem: reporting one is its whole job.
        /// </summary>
        public async Task<JellyfinConnectionReport> TestConnectionAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_settings.ServerUrl))
            {
                return new JellyfinConnectionReport
                {
                    State = JellyfinConnectionState.NotConfigured,
                    Message = JellyfinDiagnostics.Describe(JellyfinConnectionState.NotConfigured, null)
                };
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(JellyfinDiagnostics.PublicInfoPath));
                request.Headers.TryAddWithoutValidation("Authorization", BuildAuthorizationHeader(_token));

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                var status = (int)response.StatusCode;
                var state = JellyfinDiagnostics.FromStatusCode(response.StatusCode);

                if (state != JellyfinConnectionState.Reachable)
                    return Report(state, JellyfinDiagnostics.Describe(state, _settings.ServerUrl, status), status);

                var info = await TryReadPublicInfoAsync(response, ct);

                // A 200 from something that cannot name itself is a proxy, a router page or a
                // captive portal, not a server. Same remedy as a 404, so say the same thing.
                if (info is null || (string.IsNullOrWhiteSpace(info.ServerName) && string.IsNullOrWhiteSpace(info.Version)))
                {
                    return Report(
                        JellyfinConnectionState.NotJellyfin,
                        JellyfinDiagnostics.Describe(JellyfinConnectionState.NotJellyfin, _settings.ServerUrl, status),
                        status);
                }

                var name = string.IsNullOrWhiteSpace(info.ServerName) ? null : info.ServerName.Trim();
                var version = string.IsNullOrWhiteSpace(info.Version) ? null : info.Version.Trim();

                var described = $"Reached Jellyfin at {JellyfinDiagnostics.Display(_settings.ServerUrl)}";
                if (name is not null) described += $" — {name}";
                if (version is not null) described += $" (version {version})";

                return new JellyfinConnectionReport
                {
                    State = JellyfinConnectionState.Reachable,
                    Message = described + ".",
                    StatusCode = status,
                    ServerName = name,
                    Version = version
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Report(
                    JellyfinConnectionState.TimedOut,
                    JellyfinDiagnostics.Describe(JellyfinConnectionState.TimedOut, _settings.ServerUrl));
            }
            catch (Exception ex)
            {
                var state = JellyfinDiagnostics.Classify(ex);
                return Report(state, JellyfinDiagnostics.Describe(state, _settings.ServerUrl));
            }

            static JellyfinConnectionReport Report(JellyfinConnectionState state, string message, int? status = null)
                => new() { State = state, Message = message, StatusCode = status };
        }

        private async Task<JellyfinPublicInfoDto?> TryReadPublicInfoAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await JsonSerializer.DeserializeAsync<JellyfinPublicInfoDto>(stream, _json, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // An HTML error page deserialises to nothing, which is itself the answer.
                return null;
            }
        }

        /// <summary>
        /// The single place a transport failure is turned into something a person can read. Which
        /// failure it was matters: a name that will not resolve, a refused connection and a
        /// dropped one send a user to three different places, and saying only that the server
        /// could not be reached sends them nowhere.
        /// </summary>
        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            try
            {
                return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // Not the caller cancelling: HttpClient reports its own timeout this way.
                AppLog.Write("jellyfin.log", Redact($"request timed out: {ex.Message}"));
                throw new JellyfinException(
                    JellyfinDiagnostics.Describe(JellyfinConnectionState.TimedOut, _settings.ServerUrl), ex);
            }
            catch (HttpRequestException ex)
            {
                var state = JellyfinDiagnostics.Classify(ex);
                AppLog.Write("jellyfin.log", Redact($"request failed ({state}): {ex.Message}"));
                throw new JellyfinException(JellyfinDiagnostics.Describe(state, _settings.ServerUrl), ex);
            }
        }

        private async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await JsonSerializer.DeserializeAsync<T>(stream, _json, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new JellyfinException("Jellyfin sent a response this app could not read.", ex);
            }
        }

        /// <summary>        /// Jellyfin parses the authorization header by splitting on quotes and commas, so a value
        /// containing either would corrupt it. Machine names routinely contain both, along with
        /// non-ASCII characters an HTTP header cannot carry.
        /// </summary>
        internal static string SanitizeHeaderValue(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;

            var builder = new StringBuilder(value.Length);
            foreach (var ch in value.Trim())
            {
                if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.') builder.Append(ch);
                else if (ch == ' ') builder.Append('-');
            }

            var cleaned = builder.ToString().Trim('-');
            return cleaned.Length == 0 ? fallback : cleaned;
        }

        private static string SafeMachineName()
        {
            try { return Environment.MachineName; }
            catch { return "desktop"; }
        }

        public void Dispose() => _http.Dispose();

        /// <summary>
        /// <c>/Users/{id}/Views</c> returns the same envelope as <c>/Items</c> but with library
        /// entries inside, which needs its own type because the item shape differs.
        /// </summary>
        private sealed class JellyfinItemsDtoOfViews
        {
            public List<JellyfinViewDto> Items { get; set; } = new();
        }
    }
}
