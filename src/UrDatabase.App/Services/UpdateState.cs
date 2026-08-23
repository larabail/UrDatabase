using System;
using System.IO;
using System.Text.Json;

namespace UrDatabase.Services
{
    /// <summary>
    /// The answer the last check produced, written down so the next launch does not have to ask
    /// for it again.
    ///
    /// Stored flat, as the few strings the banner needs, rather than as the release payload it came
    /// from. That payload is thirty releases of JSON to answer one question, and keeping it would
    /// grow this file by two orders of magnitude to say the same thing.
    /// </summary>
    public sealed class CachedUpdate
    {
        public string? Version { get; set; }
        public string? Tag { get; set; }
        public string? Page { get; set; }
        public string? AssetName { get; set; }
        public string? AssetUrl { get; set; }
        public long AssetBytes { get; set; }
    }

    /// <summary>
    /// What the app remembers about update checks between launches: the version somebody pressed
    /// <b>Later</b> on, and everything needed to avoid asking GitHub the same question twice.
    ///
    /// Its own small file rather than a field in <c>appsettings.json</c>. That file is a
    /// hand-editable list of the user's answers, it is round-tripped whole by the setup screen, and
    /// every setting in it has to be named in <see cref="ConfigStore.Serialize"/> or it is deleted
    /// on the next save. A value the app writes to itself, that no user would ever set and that
    /// changes on its own, does not belong in there.
    ///
    /// Best effort in both directions. A read-only home directory means the banner reappears next
    /// launch and the check is made again, which is a mild annoyance and a wasted request; it must
    /// never be a failed start or a lost click.
    /// </summary>
    public sealed class UpdateState
    {
        public const string FileName = "update-state.json";

        /// <summary>
        /// The version the user has said they do not want to be told about, normalised. Null when
        /// they have never dismissed one, which is every install until they do.
        /// </summary>
        public string? SkippedVersion { get; set; }

        /// <summary>
        /// The validator GitHub gave for the release list, replayed as <c>If-None-Match</c> on the
        /// next check. A conditional request answered <c>304</c> does not count against the
        /// anonymous rate limit at all, which is the whole reason this is kept.
        /// </summary>
        public string? ETag { get; set; }

        /// <summary>
        /// When the last check <em>finished</em>, whatever the outcome. A failure is recorded as
        /// carefully as a success: a check that failed because the rate limit was already spent is
        /// exactly the one that must not be retried on the next launch.
        /// </summary>
        public DateTimeOffset? LastCheckedUtc { get; set; }

        /// <summary>The version that was running when the cache was filled.</summary>
        public string? RunningVersion { get; set; }

        /// <summary>The build the cache was resolved for, as a .NET runtime identifier.</summary>
        public string? RuntimeIdentifier { get; set; }

        /// <summary>
        /// The last answer, or null when the last check found nothing to offer. Null is a real
        /// answer here rather than a missing one: an up-to-date install caches "there is nothing"
        /// and stops asking, which is the common case and the one worth making free.
        /// </summary>
        public CachedUpdate? Cached { get; set; }

        public static string DefaultPath => Path.Combine(PlatformPaths.AppDataRoot, FileName);

        public static UpdateState Load(string? path = null)
        {
            try
            {
                var target = path ?? DefaultPath;
                if (!File.Exists(target)) return new UpdateState();

                var state = JsonSerializer.Deserialize<UpdateState>(
                    File.ReadAllText(target),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (state is null) return new UpdateState();

                // Normalised on the way in, so a file edited by hand to `v0.11.0` still silences
                // the release it names rather than silencing nothing.
                state.SkippedVersion = AppVersion.Text(state.SkippedVersion);
                return state;
            }
            catch
            {
                // Malformed, unreadable, written by a newer version: the banner shows and the check
                // runs, which is the safe way to be wrong.
                return new UpdateState();
            }
        }

        /// <summary>Writes this state, and returns whether it stuck.</summary>
        public bool Save(string? path = null)
        {
            try
            {
                var target = path ?? DefaultPath;

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

                File.WriteAllText(target, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("update.log", $"could not write the update state: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Records that this version has been dismissed, and returns whether it stuck. A version
        /// that does not parse clears the record instead of storing a value nothing will match.
        ///
        /// Read before written, rather than written over. Dismissing a banner must not discard the
        /// ETag and the timestamp that share this file, or pressing <b>Later</b> would silently
        /// cost a full request on the next launch — and on an install that never updates, pressing
        /// it would put back exactly the per-launch request this file exists to stop.
        /// </summary>
        public static bool SaveSkipped(string? version, string? path = null)
        {
            var state = Load(path);
            state.SkippedVersion = AppVersion.Text(version);
            return state.Save(path);
        }

        /// <summary>
        /// Whether a check made <paramref name="interval"/> ago or less has already answered this
        /// question, so no request should be made at all.
        ///
        /// Deliberately independent of whether the cached answer is usable. Those are two
        /// questions — "may I ask again" and "do I have an answer" — and tying them together would
        /// mean an app that had just been updated, or one whose last check failed, went back to
        /// asking on every single launch, which is the fault being fixed.
        ///
        /// A timestamp in the future is treated as stale rather than as a very long silence. It
        /// means the clock moved, and the alternative is an install that never checks again.
        /// </summary>
        public bool CheckedRecently(DateTimeOffset now, TimeSpan interval) =>
            LastCheckedUtc is DateTimeOffset last && last <= now && now - last < interval;

        /// <summary>
        /// Whether the cache was filled by this build, on this kind of machine, and so can be
        /// believed without asking again.
        ///
        /// Both halves matter. The answer names one asset chosen for one runtime identifier, so an
        /// Intel build that has since been replaced by the native one must not be handed the file
        /// the old one was offered. And an app that has just been updated has to re-ask rather than
        /// re-read, because the previous answer was computed against the version it replaced.
        /// </summary>
        public bool WasFilledBy(string? runningVersion, string? runtimeIdentifier) =>
            LastCheckedUtc is not null &&
            string.Equals(RunningVersion, runningVersion, StringComparison.Ordinal) &&
            string.Equals(RuntimeIdentifier, runtimeIdentifier, StringComparison.Ordinal);

        /// <summary>
        /// The cached answer as the banner wants it, or null when there is none to give.
        ///
        /// Re-validated rather than trusted. This is a file on somebody's disk: it can be edited,
        /// it can be restored from a backup taken before an update, and it outlives the release it
        /// describes. So the rules <see cref="UpdateFeed"/> applies to the API are applied again
        /// here — https only, github.com only — and the version is compared against the one
        /// actually running, which is what makes the banner disappear after an update rather than
        /// announcing the release the user has just installed.
        /// </summary>
        public AvailableUpdate? CachedFor(string? runningVersion, string? runtimeIdentifier)
        {
            if (!WasFilledBy(runningVersion, runtimeIdentifier)) return null;
            if (Cached is not CachedUpdate cached) return null;

            var version = AppVersion.Text(cached.Version);
            if (version is null || !AppVersion.IsNewer(version, runningVersion)) return null;

            var tag = string.IsNullOrWhiteSpace(cached.Tag) ? $"v{version}" : cached.Tag.Trim();

            // Only ever open a github.com page, for the reason UpdateFeed gives: the string reaches
            // the operating system's URL opener, which honours whatever scheme it is handed.
            var page = cached.Page is not null &&
                       cached.Page.StartsWith("https://github.com/", StringComparison.Ordinal)
                ? cached.Page
                : $"{UpdateFeed.ReleasesPageUrl}/tag/{Uri.EscapeDataString(tag)}";

            UpdateAsset? asset = null;
            if (!string.IsNullOrWhiteSpace(cached.AssetName) &&
                cached.AssetUrl is not null &&
                cached.AssetUrl.StartsWith("https://", StringComparison.Ordinal))
            {
                asset = new UpdateAsset(
                    cached.AssetName.Trim(),
                    cached.AssetUrl,
                    cached.AssetBytes > 0 ? cached.AssetBytes : 0);
            }

            return new AvailableUpdate(version, tag, page, asset);
        }

        /// <summary>
        /// Records what GitHub answered. <paramref name="update"/> is null when the install is up
        /// to date, and that is stored as an answer in its own right.
        /// </summary>
        public void RememberAnswer(
            DateTimeOffset checkedAt,
            string? runningVersion,
            string? runtimeIdentifier,
            string? etag,
            AvailableUpdate? update)
        {
            LastCheckedUtc = checkedAt;
            RunningVersion = runningVersion;
            RuntimeIdentifier = runtimeIdentifier;
            ETag = etag;
            Cached = update is null ? null : new CachedUpdate
            {
                Version = update.Version,
                Tag = update.Tag,
                Page = update.Page,
                AssetName = update.Asset?.Name,
                AssetUrl = update.Asset?.Url,
                AssetBytes = update.Asset?.Bytes ?? 0,
            };
        }

        /// <summary>
        /// Records that a check happened and changed nothing — either GitHub answered <c>304</c>,
        /// or the request never got an answer at all.
        ///
        /// Only the timestamp moves. On a <c>304</c> that is the literal truth: the list is
        /// unchanged, so the answer derived from it still stands. On a failure it is the
        /// conservative reading: the previous answer is the last thing known to be true, and
        /// discarding it would turn an unreachable network into a silently missing update notice.
        /// Either way the clock restarts, which is what stops the next launch asking again.
        /// </summary>
        public void RememberNothingChanged(DateTimeOffset checkedAt) => LastCheckedUtc = checkedAt;
    }
}
