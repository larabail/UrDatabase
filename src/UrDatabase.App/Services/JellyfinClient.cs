using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// What one sync found on the server: its films and its television series, in one value.
    /// </summary>
    /// <remarks>
    /// Together rather than fetched separately, because they are written to the cache in one
    /// transaction and a half-replaced cache is the failure this app already went out of its way
    /// to avoid once. Either list may be empty — a server with only films and a server with only
    /// television are both ordinary — and an empty list is not the same as a failure.
    /// </remarks>
    public sealed record JellyfinLibraryContents(
        IReadOnlyList<JellyfinMovie> Movies,
        IReadOnlyList<JellyfinSeries> Series)
    {
        public static JellyfinLibraryContents Empty { get; } =
            new(Array.Empty<JellyfinMovie>(), Array.Empty<JellyfinSeries>());

        /// <summary>How many things the sync brought back, films and series together.</summary>
        public int Count => Movies.Count + Series.Count;
    }

    /// <summary>
    /// Reads a Jellyfin server's film and television libraries, and fetches an item from one when
    /// asked.
    ///
    /// Deliberately narrow. It authenticates, finds the libraries, lists them, builds the URLs the
    /// rest of the app needs and opens the response for a download. It does not transcode and
    /// never asks TMDB for anything — a Jellyfin item arrives complete, so a library from a server
    /// works fully on a build with no TMDB key at all.
    ///
    /// Seasons and episodes are the one thing it fetches lazily. A sync pulls films and series and
    /// stops there: two hundred shows is thousands of episodes, and a sync that walked them all
    /// would be a sync nobody waits for. They are asked for when a series is opened.
    ///
    /// Nothing it fetches is written to this machine: metadata goes to the cache through
    /// <see cref="JellyfinCache"/>, and a downloaded film is written by
    /// <see cref="JellyfinDownloader"/>, which owns every decision about where bytes land.
    /// </summary>
    public sealed class JellyfinClient : IDisposable
    {
        /// <summary>How this app identifies itself in the Jellyfin authorization header.</summary>
        public const string ClientName = "UrDatabase";

        /// <summary>Items per request. Jellyfin caps a page well above this; 100 keeps each response small.</summary>
        public const int PageSize = 100;

        /// <summary>
        /// How many part-watched films to ask for. A row is something you glance along, not a
        /// second library, and a server that has accumulated two hundred abandoned films should
        /// not put all of them above the genres.
        /// </summary>
        public const int ResumeLimit = 24;

        /// <summary>
        /// Everything the list needs in one pass. Without <c>Fields</c> Jellyfin returns a stub
        /// with no genres, overview or provider ids, and the app would be back to guessing.
        /// </summary>
        public const string ItemFields = "Genres,Overview,ProviderIds,ProductionYear,RunTimeTicks,CommunityRating,People";

        /// <summary>
        /// The same, plus the two counts that make a series card readable as a series. Both are
        /// optional on the wire — not every server version fills them in — so nothing depends on
        /// them arriving; a missing count is printed as nothing rather than as zero.
        /// </summary>
        public const string SeriesFields = ItemFields + ",ChildCount,RecursiveItemCount";

        /// <summary>A season needs its episode count and nothing else; its name and number are always sent.</summary>
        public const string SeasonFields = "ChildCount";

        /// <summary>
        /// An episode needs a plot and a length. Deliberately not <see cref="ItemFields"/>: a
        /// season of twenty-four episodes would drag twenty-four cast lists across the network to
        /// render a list of titles.
        /// </summary>
        public const string EpisodeFields = "Overview,RunTimeTicks";

        private readonly HttpClient _http;
        private readonly HttpClient _downloadHttp;
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

            // A second client purely for transfers. The timeout above is a request timeout and
            // covers reading the body too, so downloading a film through it would abort at fifteen
            // seconds however healthy the connection was. Here the cancellation token is the only
            // limit, which is what the user's Cancel button is for.
            //
            // The handler belongs to the client above, which disposes it; this one must not, or a
            // shared handler would be disposed twice.
            _downloadHttp = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            _downloadHttp.Timeout = Timeout.InfiniteTimeSpan;
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
        /// Where the original file is served from, for keeping a copy rather than streaming one.
        ///
        /// Unlike <see cref="BuildStreamUrl"/> this carries no token: the request is made by this
        /// app, which can put credentials in a header, rather than handed to an external player
        /// that can only be given a URL. That makes the address safe to log, and it is the reason
        /// downloading is preferred to pointing something else at a stream URL.
        /// </summary>
        public Uri BuildDownloadUri(string itemId) =>
            BuildUri($"Items/{Uri.EscapeDataString(itemId ?? "")}/Download");

        /// <summary>
        /// Opens the response for a film's original file, without reading the body. The caller owns
        /// the returned response and must dispose it.
        ///
        /// <paramref name="resumeFrom"/> asks the server to continue an interrupted transfer.
        /// Jellyfin supports ranges, but a proxy in front of it might not, so the caller has to
        /// check the status: <c>206</c> means the range was honoured and the bytes append to what
        /// is already on disk, while <c>200</c> means it was ignored and the file starts again.
        /// Appending a whole-file response onto a partial one would produce a corrupt film that
        /// still plays for the first few minutes, which is the worst way for this to fail.
        /// </summary>
        public async Task<HttpResponseMessage> OpenDownloadAsync(
            string itemId,
            long resumeFrom = 0,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new JellyfinException("This film has no id on the server, so it cannot be downloaded.");

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildDownloadUri(itemId));
            request.Headers.TryAddWithoutValidation("Authorization", BuildAuthorizationHeader(_token));

            if (resumeFrom > 0) request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

            HttpResponseMessage response;
            try
            {
                response = await SendAsync(request, ct, _downloadHttp);
            }
            catch (JellyfinException)
            {
                throw;
            }

            if (response.IsSuccessStatusCode) return response;

            var status = (int)response.StatusCode;
            response.Dispose();

            // A 404 here is genuinely a missing item, unlike everywhere else in this client: the
            // id came from a cache that may be older than the server's library.
            if (status == (int)HttpStatusCode.NotFound)
                throw new JellyfinException(
                    "The server no longer has this film. Sync Jellyfin to refresh the library.");

            var state = JellyfinDiagnostics.FromStatusCode((HttpStatusCode)status);
            throw new JellyfinException(JellyfinDiagnostics.Describe(state, _settings.ServerUrl, status));
        }

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
        /// The user's top level libraries, exactly as the server reports them.
        /// </summary>
        public async Task<IReadOnlyList<JellyfinViewDto>> GetLibrariesAsync(string userId, CancellationToken ct = default)
        {
            var views = await GetAsync<JellyfinItemsDtoOfViews>($"Users/{Uri.EscapeDataString(userId)}/Views", ct);
            return views?.Items ?? new List<JellyfinViewDto>();
        }

        /// <summary>
        /// The movie library, or null when the server has none.
        /// </summary>
        /// <remarks>
        /// Null rather than an exception, which is what this used to be. A server that holds only
        /// television is a perfectly ordinary server, and refusing to get past the absence of a
        /// movie library meant such a server showed nothing at all — the throw happened before
        /// anything else ran, so every one of its series was discarded to report a missing film
        /// library nobody had asked for.
        ///
        /// A <em>named</em> library that is not there is still an exception, and has to be: the
        /// name came from configuration, so its absence is a mistake somebody can correct, and
        /// silently reading a different library instead would be the wrong films with no
        /// explanation. It is only raised when the server has movie libraries to choose between —
        /// a name cannot be wrong about a kind of library the server does not have.
        /// </remarks>
        public async Task<JellyfinViewDto?> FindMovieLibraryAsync(string userId, CancellationToken ct = default)
            => SelectMovieLibrary(await GetLibrariesAsync(userId, ct));

        /// <summary>
        /// Every television library. All of them, unlike the single movie library above: a server
        /// routinely files television under more than one — "TV Shows" and "Anime" is the usual
        /// pair — and <see cref="JellyfinSettings.LibraryName"/> is documented as naming a movie
        /// library, so there is nothing to narrow these by and no reason to pick one arbitrarily.
        /// </summary>
        public async Task<IReadOnlyList<JellyfinViewDto>> FindSeriesLibrariesAsync(string userId, CancellationToken ct = default)
            => SelectSeriesLibraries(await GetLibrariesAsync(userId, ct));

        /// <summary>
        /// Picks the movie library out of a list of libraries. Pure, and separate from the request
        /// above, so the rule — collection type first, then the configured name — can be asserted
        /// without a server.
        /// </summary>
        internal JellyfinViewDto? SelectMovieLibrary(IEnumerable<JellyfinViewDto>? libraries)
        {
            var movieLibraries = (libraries ?? Array.Empty<JellyfinViewDto>())
                .Where(v => v.IsMovieLibrary)
                .ToList();

            if (movieLibraries.Count == 0) return null;

            if (!string.IsNullOrWhiteSpace(_settings.LibraryName))
            {
                var named = movieLibraries.FirstOrDefault(v =>
                    string.Equals(v.Name, _settings.LibraryName.Trim(), StringComparison.OrdinalIgnoreCase));

                if (named is null)
                    throw new JellyfinException($"No movie library called \"{_settings.LibraryName.Trim()}\" exists on that server.");

                return named;
            }

            return movieLibraries[0];
        }

        /// <inheritdoc cref="FindSeriesLibrariesAsync"/>
        internal static IReadOnlyList<JellyfinViewDto> SelectSeriesLibraries(IEnumerable<JellyfinViewDto>? libraries)
            => (libraries ?? Array.Empty<JellyfinViewDto>()).Where(v => v.IsSeriesLibrary).ToList();

        /// <summary>
        /// Signs in, finds the libraries and counts them, without fetching a single item. Written
        /// for the setup screen's test button: it answers the questions that actually go wrong —
        /// is the address right, are the credentials right, and is there anything on that server
        /// this app can read — and says which one failed rather than reporting "it didn't work".
        ///
        /// The counts come from Jellyfin's own totals on empty pages, so testing a library of ten
        /// thousand films costs the same as testing an empty one.
        /// </summary>
        public async Task<string> DescribeLibraryAsync(CancellationToken ct = default)
        {
            await ConnectAsync(ct);

            var libraries = await GetLibrariesAsync(_userId!, ct);
            var movieLibrary = SelectMovieLibrary(libraries);
            var seriesLibraries = SelectSeriesLibraries(libraries);

            if (movieLibrary is null && seriesLibraries.Count == 0)
                throw new JellyfinException("That Jellyfin server has no film or television library.");

            var parts = new List<string>();

            if (movieLibrary is not null)
            {
                var films = await CountAsync(_userId!, movieLibrary.Id, "Movie", ct);
                parts.Add($"{films.ToString(CultureInfo.InvariantCulture)} {(films == 1 ? "film" : "films")} in {Describe(movieLibrary, "the movie library")}");
            }

            if (seriesLibraries.Count > 0)
            {
                var shows = 0;
                foreach (var library in seriesLibraries)
                    shows += await CountAsync(_userId!, library.Id, "Series", ct);

                var name = seriesLibraries.Count == 1
                    ? Describe(seriesLibraries[0], "the television library")
                    : "television";

                parts.Add($"{shows.ToString(CultureInfo.InvariantCulture)} {(shows == 1 ? "series" : "series")} in {name}");
            }

            return $"Connected. {string.Join(", ", parts)}.";

            static string Describe(JellyfinViewDto library, string fallback)
                => string.IsNullOrWhiteSpace(library.Name) ? fallback : $"\"{library.Name}\"";
        }

        /// <summary>
        /// How many items of one type a library holds, asked for as a page of none.
        /// </summary>
        private async Task<int> CountAsync(string userId, string libraryId, string itemType, CancellationToken ct)
        {
            var path =
                $"Users/{Uri.EscapeDataString(userId)}/Items" +
                $"?ParentId={Uri.EscapeDataString(libraryId)}" +
                $"&IncludeItemTypes={itemType}&Recursive=true&Limit=0";

            var page = await GetAsync<JellyfinItemsDto>(path, ct);
            return page?.TotalRecordCount ?? 0;
        }

        // ---------- the library ----------

        /// <summary>
        /// Everything on the server this app understands: the films, and the television series.
        /// One call because it is one sync, and because the two share a sign-in, a
        /// <c>/Views</c> request and a progress line.
        /// </summary>
        /// <remarks>
        /// A server missing one half is not an error. Only a server with neither is, and it is
        /// reported once here rather than by each half separately — a library of television
        /// answering "that server has no movie library" is exactly the bug this replaced.
        /// </remarks>
        public async Task<JellyfinLibraryContents> GetLibraryAsync(
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            await ConnectAsync(ct);

            var userId = _userId!;
            var libraries = await GetLibrariesAsync(userId, ct);
            var movieLibrary = SelectMovieLibrary(libraries);
            var seriesLibraries = SelectSeriesLibraries(libraries);

            if (movieLibrary is null && seriesLibraries.Count == 0)
                throw new JellyfinException("That Jellyfin server has no film or television library.");

            var movies = movieLibrary is null
                ? Array.Empty<JellyfinMovie>()
                : await FetchMoviesAsync(userId, movieLibrary.Id, progress, ct);

            var series = await FetchSeriesAsync(userId, seriesLibraries, progress, ct);

            return new JellyfinLibraryContents(movies, series);
        }

        /// Asks the server to scan its libraries, which is the only way a file that appeared on
        /// its disk becomes a film it knows about.
        ///
        /// It exists here rather than in <see cref="JellyfinUploader"/> because this class already
        /// holds the token and the shape of an authenticated request; a second HTTP client for one
        /// POST would mean a second place for the authorization header to be got wrong.
        ///
        /// Two things about it are worth knowing at the call site. It is administrative — a
        /// perfectly ordinary Jellyfin account gets a 403 and cannot start a scan — and it is
        /// asynchronous even when it succeeds: the server answers 204 immediately and the film
        /// appears when the scan reaches it, which on a large library is not instant.
        /// </summary>
        public async Task RefreshLibraryAsync(CancellationToken ct = default)
        {
            await ConnectAsync(ct);

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("Library/Refresh"));
            request.Headers.TryAddWithoutValidation("Authorization", BuildAuthorizationHeader(_token));

            using var response = await SendAsync(request, ct);

            if (response.IsSuccessStatusCode) return;

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new JellyfinException(
                    "Jellyfin will not let this account start a library scan, because scanning is " +
                    "an administrator's job. The film is on the server and will appear at its next " +
                    "scheduled scan.");

            throw new JellyfinException($"Jellyfin refused to rescan its library (HTTP {(int)response.StatusCode}).");
        }

        /// <summary>
        /// Every film in the movie library, fetched a page at a time. Empty, rather than an
        /// exception, on a server with no movie library at all.
        /// </summary>
        public async Task<IReadOnlyList<JellyfinMovie>> GetMoviesAsync(
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            await ConnectAsync(ct);

            var userId = _userId!;
            var library = await FindMovieLibraryAsync(userId, ct);
            if (library is null) return Array.Empty<JellyfinMovie>();

            return await FetchMoviesAsync(userId, library.Id, progress, ct);
        }

        /// <summary>
        /// Every television series on the server, across all of its television libraries. The
        /// seasons and episodes underneath them are deliberately not fetched here.
        /// </summary>
        public async Task<IReadOnlyList<JellyfinSeries>> GetSeriesAsync(
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            await ConnectAsync(ct);

            var userId = _userId!;
            return await FetchSeriesAsync(userId, await FindSeriesLibrariesAsync(userId, ct), progress, ct);
        }

        /// <summary>
        /// Progress is reported per page rather than per film so a slow server still says
        /// something without flooding the status line.
        /// </summary>
        private async Task<IReadOnlyList<JellyfinMovie>> FetchMoviesAsync(
            string userId,
            string libraryId,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            var movies = new List<JellyfinMovie>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await PageAsync(
                startIndex => ItemsPath(userId, libraryId, "Movie", ItemFields, startIndex),
                (items, total) =>
                {
                    foreach (var item in items)
                    {
                        var movie = item.ToMovie();
                        if (movie is not null && seen.Add(movie.ItemId)) movies.Add(movie);
                    }

                    progress?.Report($"Jellyfin: {Math.Min(seen.Count, total)} of {total} films…");
                },
                ct);

            return movies;
        }

        private async Task<IReadOnlyList<JellyfinSeries>> FetchSeriesAsync(
            string userId,
            IReadOnlyList<JellyfinViewDto> libraries,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            var series = new List<JellyfinSeries>();
            if (libraries.Count == 0) return series;

            // Deduplicated across libraries, not merely within one. A server that files the same
            // show under both "TV Shows" and "Anime" would otherwise put two identical cards on
            // the shelf, and the second would overwrite the first in the cache anyway.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var library in libraries)
            {
                await PageAsync(
                    startIndex => ItemsPath(userId, library.Id, "Series", SeriesFields, startIndex),
                    (items, _) =>
                    {
                        foreach (var item in items)
                        {
                            var show = item.ToSeries();
                            if (show is not null && seen.Add(show.ItemId)) series.Add(show);
                        }

                        progress?.Report($"Jellyfin: {seen.Count} {(seen.Count == 1 ? "series" : "series")}…");
                    },
                    ct);
            }

            return series;
        }

        /// <summary>
        /// The seasons of one series, in the order the server lists them. Fetched when a series is
        /// opened rather than during a sync — see the remarks on this class.
        /// </summary>
        public async Task<IReadOnlyList<JellyfinSeason>> GetSeasonsAsync(string seriesId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(seriesId))
                throw new JellyfinException("This series has no id on the server, so its seasons cannot be listed.");

            await ConnectAsync(ct);

            var path =
                $"Shows/{Uri.EscapeDataString(seriesId)}/Seasons" +
                $"?userId={Uri.EscapeDataString(_userId!)}" +
                $"&Fields={SeasonFields}";

            var page = await GetAsync<JellyfinItemsDto>(path, ct);

            return (page?.Items ?? new List<JellyfinItemDto>())
                .Select(item => item.ToSeason(seriesId))
                .Where(season => season is not null)
                .Select(season => season!)
                .ToList();
        }

        /// <summary>
        /// Every episode of one series, across every season, a page at a time.
        /// </summary>
        /// <remarks>
        /// All seasons in one request rather than one request per season. Each episode carries its
        /// own season number, so the grouping costs nothing here and asking per season would turn
        /// opening a show with twelve of them into twelve round trips.
        /// </remarks>
        public async Task<IReadOnlyList<JellyfinEpisode>> GetEpisodesAsync(string seriesId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(seriesId))
                throw new JellyfinException("This series has no id on the server, so its episodes cannot be listed.");

            await ConnectAsync(ct);

            var episodes = new List<JellyfinEpisode>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await PageAsync(
                startIndex =>
                    $"Shows/{Uri.EscapeDataString(seriesId)}/Episodes" +
                    $"?userId={Uri.EscapeDataString(_userId!)}" +
                    $"&Fields={EpisodeFields}" +
                    $"&StartIndex={startIndex.ToString(CultureInfo.InvariantCulture)}" +
                    $"&Limit={PageSize.ToString(CultureInfo.InvariantCulture)}",
                (items, _) =>
                {
                    foreach (var item in items)
                    {
                        var episode = item.ToEpisode(seriesId);
                        if (episode is not null && seen.Add(episode.ItemId)) episodes.Add(episode);
                    }
                },
                ct);

            return episodes;
        }

        private static string ItemsPath(string userId, string libraryId, string itemType, string fields, int startIndex) =>
            $"Users/{Uri.EscapeDataString(userId)}/Items" +
            $"?ParentId={Uri.EscapeDataString(libraryId)}" +
            $"&IncludeItemTypes={itemType}&Recursive=true" +
            "&SortBy=SortName&SortOrder=Ascending" +
            $"&StartIndex={startIndex.ToString(CultureInfo.InvariantCulture)}" +
            $"&Limit={PageSize.ToString(CultureInfo.InvariantCulture)}" +
            $"&Fields={fields}";

        /// <summary>
        /// Walks a paged endpoint until it runs out, handing each page to <paramref name="accept"/>.
        /// </summary>
        /// <remarks>
        /// One loop for films, series and episodes, because the way it stops is the part that is
        /// easy to get wrong and expensive to get wrong twice: it advances by what the page
        /// actually contained rather than by the page size, so a server that returns a short page
        /// does not have the rest of its library skipped, and it stops on an empty page as well as
        /// on the total, so a server whose total is wrong cannot spin here forever.
        /// </remarks>
        private async Task PageAsync(
            Func<int, string> path,
            Action<IReadOnlyList<JellyfinItemDto>, int> accept,
            CancellationToken ct)
        {
            var startIndex = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var page = await GetAsync<JellyfinItemsDto>(path(startIndex), ct);
                var items = page?.Items ?? new List<JellyfinItemDto>();
                if (items.Count == 0) break;

                startIndex += items.Count;

                var total = page?.TotalRecordCount ?? startIndex;
                accept(items, total);

                if (startIndex >= total) break;
            }
        }

        // ---------- continue watching ----------

        /// <summary>
        /// Films and episodes the server says this user is part way through, newest first.
        /// </summary>
        /// <remarks>
        /// <c>/UserItems/Resume</c> is the server's own answer to "where was I", so the row is the
        /// same one every other Jellyfin client shows rather than something this app worked out.
        /// Television is asked for alongside film because a half-watched episode is the commonest
        /// thing to be part way through, and a row that silently left it out was this app
        /// disagreeing with every other client in the house.
        ///
        /// The position is all that is kept for a film — its title, year and artwork are already
        /// cached with the library. An episode also brings the programme, the season and its
        /// number, because nothing caches episodes until a series is opened and a card cannot be
        /// drawn from an id alone.
        /// </remarks>
        public async Task<IReadOnlyList<JellyfinResumeItem>> GetResumeAsync(CancellationToken ct = default)
        {
            await ConnectAsync(ct);

            var path =
                "UserItems/Resume" +
                $"?userId={Uri.EscapeDataString(_userId!)}" +
                $"&IncludeItemTypes={JellyfinResumeItem.MovieType},{JellyfinResumeItem.EpisodeType}&MediaTypes=Video" +
                $"&Limit={ResumeLimit.ToString(CultureInfo.InvariantCulture)}" +
                "&Fields=UserData,RunTimeTicks" +
                "&EnableTotalRecordCount=false";

            var page = await GetAsync<JellyfinItemsDto>(path, ct);
            var items = page?.Items ?? new List<JellyfinItemDto>();

            var resume = new List<JellyfinResumeItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var order = 0;

            foreach (var item in items)
            {
                var entry = item.ToResumeItem(order);
                if (entry is null || !seen.Add(entry.ItemId)) continue;

                resume.Add(entry);
                order++;
            }

            return resume;
        }

        // ---------- reporting playback ----------

        /// <summary>
        /// Tells the server a film has started, so it appears as a session and, once it stops,
        /// in Continue watching.
        /// </summary>
        /// <remarks>
        /// These three go through the client that already holds the token rather than through
        /// anything of their own: a report has to be signed in as the user whose row it will
        /// appear in, and a second sign-in would be a second place for the credential to live.
        ///
        /// None of them raises for a server that has gone away. A viewer mid-film is owed their
        /// film, not a dialog about a resume position, and the caller is a background loop with
        /// nowhere to show one.
        /// </remarks>
        public Task ReportPlaybackStartAsync(string itemId, long positionTicks, CancellationToken ct = default) =>
            ReportPlaybackAsync("Sessions/Playing", itemId, positionTicks, isPaused: false, ct);

        /// <inheritdoc cref="ReportPlaybackStartAsync"/>
        public Task ReportPlaybackProgressAsync(string itemId, long positionTicks, bool isPaused, CancellationToken ct = default) =>
            ReportPlaybackAsync("Sessions/Playing/Progress", itemId, positionTicks, isPaused, ct);

        /// <inheritdoc cref="ReportPlaybackStartAsync"/>
        public Task ReportPlaybackStoppedAsync(string itemId, long positionTicks, CancellationToken ct = default) =>
            ReportPlaybackAsync("Sessions/Playing/Stopped", itemId, positionTicks, isPaused: false, ct);

        /// <summary>
        /// The body all three reports share.
        /// </summary>
        /// <remarks>
        /// <c>MediaSourceId</c> is the item id, which is what a direct play of the original file
        /// uses, and <c>PlayMethod</c> says so — the stream URL asks for <c>static=true</c>, so
        /// nothing is being transcoded and claiming otherwise would put a wrong line in the
        /// server's own dashboard.
        /// </remarks>
        internal static string BuildPlaybackReportBody(string itemId, long positionTicks, bool isPaused) =>
            JsonSerializer.Serialize(new
            {
                ItemId = itemId,
                MediaSourceId = itemId,
                PositionTicks = Math.Max(0, positionTicks),
                IsPaused = isPaused,
                IsMuted = false,
                CanSeek = true,
                PlayMethod = "DirectStream"
            });

        private async Task ReportPlaybackAsync(
            string relativePath,
            string itemId,
            long positionTicks,
            bool isPaused,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return;

            await ConnectAsync(ct);

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(relativePath))
            {
                Content = new StringContent(
                    BuildPlaybackReportBody(itemId.Trim(), positionTicks, isPaused),
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", BuildAuthorizationHeader(_token));

            using var response = await SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
                throw new JellyfinException(
                    $"Jellyfin refused a playback report (HTTP {(int)response.StatusCode}).");
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
        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct, HttpClient? client = null)
        {
            try
            {
                return await (client ?? _http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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

        public void Dispose()
        {
            _http.Dispose();
            _downloadHttp.Dispose();
        }

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
