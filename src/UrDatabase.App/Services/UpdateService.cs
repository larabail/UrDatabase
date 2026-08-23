using System;
using System.Collections.Generic;
using System.Net;
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
    /// <see cref="AppConfig.CheckForUpdates"/> says so, it is answered or it is not, and every
    /// failure — no network, a rate limit, a mangled payload, a hostile one — comes back as the
    /// last known answer rather than as an exception on a background thread.
    ///
    /// It used to make one request per launch, which was affordable only while nothing else shared
    /// the budget. GitHub allows an anonymous caller 60 requests an hour <em>per IP address</em>,
    /// and the downloads site asks the very same URL from the browser, so an app started a dozen
    /// times during a day's work would quietly spend the allowance that the website needed to list
    /// its own downloads. Worse, requests that keep arriving after the allowance is gone are
    /// escalated to GitHub's edge, which answers without CORS headers — so the page could not even
    /// report the rate limit honestly, and showed a browser security error instead.
    ///
    /// So the request is now both rationed and conditional. It happens at most once every
    /// <see cref="CheckInterval"/>, and when it does happen it carries the previous
    /// <c>If-None-Match</c>: an unchanged release list comes back <c>304</c>, which GitHub does not
    /// count against the rate limit at all. A machine that is up to date therefore costs nothing on
    /// almost every check, and nothing whatsoever on most launches.
    ///
    /// Asked of the API rather than baked into the app at build time for the reason the downloads
    /// page gives: a build knows only what was true when it was made, and this question is asked
    /// precisely by the builds that are out of date.
    /// </summary>
    public sealed class UpdateService : IDisposable
    {
        /// <summary>
        /// How long an answer stands before it is worth asking again.
        ///
        /// A day, because that is the resolution of the thing being watched: releases arrive on
        /// merges to <c>main</c>, and nobody needs to hear about one within the hour. It is also
        /// the number that makes the arithmetic safe — even an install started every few minutes
        /// spends one request a day, so a household or an office behind one address cannot exhaust
        /// the hourly allowance between them however many copies are running.
        /// </summary>
        public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

        /// <summary>
        /// Long enough for a slow connection to answer, short enough that a captive portal
        /// swallowing the request costs a background task and nothing a user can see.
        /// </summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private readonly HttpClient _http;
        private readonly string _runningVersion;
        private readonly string? _runtimeIdentifier;
        private readonly string? _statePath;
        private readonly Func<DateTimeOffset> _now;

        private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// <paramref name="runningVersion"/> and <paramref name="runtimeIdentifier"/> default to
        /// this build and this machine; both are arguments so a test can ask what a Mac on 0.1.0
        /// would be told without being one.
        ///
        /// <paramref name="statePath"/> and <paramref name="now"/> are the other two seams, and
        /// they exist for the same reason. The rationing is a rule about a file and a clock, so it
        /// cannot be tested without being able to say where the file is and what time it is — and a
        /// test that used the real ones would be both slow to the tune of a day and, far worse,
        /// writing into somebody's actual install.
        /// </summary>
        public UpdateService(
            string? runningVersion = null,
            string? runtimeIdentifier = null,
            HttpMessageHandler? handler = null,
            string? statePath = null,
            Func<DateTimeOffset>? now = null)
        {
            _runningVersion = string.IsNullOrWhiteSpace(runningVersion) ? AppVersion.Current : runningVersion.Trim();
            _runtimeIdentifier = string.IsNullOrWhiteSpace(runtimeIdentifier)
                ? UpdateFeed.CurrentRuntimeIdentifier
                : runtimeIdentifier.Trim();
            _statePath = statePath;
            _now = now ?? (() => DateTimeOffset.UtcNow);

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
        /// The newest release worth offering, or null when there is none. Never throws except on
        /// cancellation, which is the window closing and is the caller's own doing.
        ///
        /// Most calls make no request. One inside <see cref="CheckInterval"/> of the last is
        /// answered from the file it wrote, which is the whole point: the answer to "is there a
        /// newer version" does not change between two launches an hour apart, and asking again is
        /// spending a shared allowance to be told the same thing.
        /// </summary>
        public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct = default)
        {
            var state = UpdateState.Load(_statePath);
            var now = _now();

            if (state.CheckedRecently(now, CheckInterval))
                return state.CachedFor(_runningVersion, _runtimeIdentifier);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, UpdateFeed.ReleasesApiUrl);

                // Replayed only when the answer it validates belongs to this build on this machine.
                // Sent otherwise, a 304 would confirm a cache that was computed for a different
                // runtime identifier, and the banner would offer the wrong file — a validator says
                // the release list has not changed, not that the conclusion drawn from it still
                // applies.
                if (!string.IsNullOrWhiteSpace(state.ETag) &&
                    state.WasFilledBy(_runningVersion, _runtimeIdentifier))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", state.ETag);
                }

                using var response = await _http.SendAsync(request, ct);

                // The cheap answer, and on an install that is up to date it is nearly every answer.
                // GitHub does not charge a conditional request that changes nothing.
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    Remember(state, s => s.RememberNothingChanged(now));
                    return state.CachedFor(_runningVersion, _runtimeIdentifier);
                }

                if (!response.IsSuccessStatusCode)
                {
                    // 403 here is almost always the anonymous rate limit, which is per IP address
                    // and shared with everything else on the network — including the downloads
                    // page. Recorded like any other finished check, so the next launch waits its
                    // turn instead of adding to the queue that caused it.
                    AppLog.Write("update.log", $"release check: HTTP {(int)response.StatusCode}");
                    Remember(state, s => s.RememberNothingChanged(now));
                    return state.CachedFor(_runningVersion, _runtimeIdentifier);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var payload = await JsonSerializer.DeserializeAsync<List<GithubRelease?>>(stream, _json, ct);

                var update = UpdateFeed.Newest(payload, _runningVersion, _runtimeIdentifier);
                var etag = response.Headers.ETag?.ToString();

                Remember(state, s => s.RememberAnswer(now, _runningVersion, _runtimeIdentifier, etag, update));
                return update;
            }
            catch (OperationCanceledException)
            {
                // The window closed while the check was in flight. Nothing is written: this is not
                // a check that finished, and recording it would cost the user a day of checking
                // because they quit the app quickly.
                throw;
            }
            catch (Exception ex)
            {
                // Offline, a DNS failure, a proxy returning HTML, a truncated body. An update check
                // that took the app down with it would be a far worse bug than never running.
                AppLog.Write("update.log", $"release check failed: {ex.Message}");
                Remember(state, s => s.RememberNothingChanged(now));
                return state.CachedFor(_runningVersion, _runtimeIdentifier);
            }
        }

        /// <summary>
        /// Applies a change and writes it, swallowing whatever the write did.
        ///
        /// A state file that cannot be written costs a request on the next launch, and that is all
        /// it may cost. The check has already succeeded by this point and the caller is owed its
        /// answer.
        /// </summary>
        private void Remember(UpdateState state, Action<UpdateState> change)
        {
            change(state);
            state.Save(_statePath);
        }

        public void Dispose() => _http.Dispose();
    }
}
