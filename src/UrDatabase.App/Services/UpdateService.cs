using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>
    /// Asks GitHub whether there is a newer release than the one running.
    ///
    /// This is the app's only call to a service that is neither a metadata provider nor the user's
    /// own server, and it is arranged so that it can never cost anything: it is off entirely when
    /// <see cref="AppConfig.CheckForUpdates"/> says so, it happens once per launch, it is answered
    /// or it is not, and every failure — no network, a rate limit, a mangled payload, a hostile
    /// one — comes back as "no update" rather than as an exception on a background thread.
    ///
    /// Asked of the API at launch rather than baked into the app at build time for the reason the
    /// downloads page gives: a build knows only what was true when it was made, and this question
    /// is asked precisely by the builds that are out of date.
    /// </summary>
    public sealed class UpdateService : IDisposable
    {
        /// <summary>
        /// Long enough for a slow connection to answer, short enough that a captive portal
        /// swallowing the request costs a background task and nothing a user can see.
        /// </summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private readonly HttpClient _http;
        private readonly string _runningVersion;
        private readonly string? _runtimeIdentifier;

        private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// <paramref name="runningVersion"/> and <paramref name="runtimeIdentifier"/> default to
        /// this build and this machine; both are arguments so a test can ask what a Mac on 0.1.0
        /// would be told without being one.
        /// </summary>
        public UpdateService(
            string? runningVersion = null,
            string? runtimeIdentifier = null,
            HttpMessageHandler? handler = null)
        {
            _runningVersion = string.IsNullOrWhiteSpace(runningVersion) ? AppVersion.Current : runningVersion.Trim();
            _runtimeIdentifier = string.IsNullOrWhiteSpace(runtimeIdentifier)
                ? UpdateFeed.CurrentRuntimeIdentifier
                : runtimeIdentifier.Trim();

            _http = handler is null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = RequestTimeout;

            // GitHub answers an anonymous request with no User-Agent with a 403, so this is not
            // decoration. It names the app and its version and nothing about the machine: there is
            // no identifier here, and two people on the same release send identical requests.
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("UrDatabase", _runningVersion));
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }

        /// <summary>The version this service is comparing against, for the line the banner shows.</summary>
        public string RunningVersion => _runningVersion;

        /// <summary>
        /// The newest release worth offering, or null when there is none, when the check failed,
        /// or when the answer made no sense. Never throws except on cancellation, which is the
        /// window closing and is the caller's own doing.
        /// </summary>
        public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct = default)
        {
            try
            {
                using var response = await _http.GetAsync(UpdateFeed.ReleasesApiUrl, ct);

                if (!response.IsSuccessStatusCode)
                {
                    // 403 here is almost always the anonymous rate limit, which is per IP address
                    // and shared with everything else on the network. Logged and dropped: there is
                    // nothing a user could do about it and nothing worth interrupting them for.
                    AppLog.Write("update.log", $"release check: HTTP {(int)response.StatusCode}");
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var payload = await JsonSerializer.DeserializeAsync<List<GithubRelease?>>(stream, _json, ct);

                return UpdateFeed.Newest(payload, _runningVersion, _runtimeIdentifier);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Offline, a DNS failure, a proxy returning HTML, a truncated body. An update check
                // that took the app down with it would be a far worse bug than never running.
                AppLog.Write("update.log", $"release check failed: {ex.Message}");
                return null;
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
