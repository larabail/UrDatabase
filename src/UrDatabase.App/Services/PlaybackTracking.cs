using System;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>
    /// Starts following a film that has just been handed to a player, and tells Jellyfin where it
    /// gets to.
    /// </summary>
    /// <remarks>
    /// The join between the two halves, and deliberately the only part of this that knows about
    /// both: the launcher does not know there is a server, and the reporter does not know a
    /// process was started. Everything it does is optional — a launch with no control interface, a
    /// film with no id, an install with no server — and each of those simply means there is
    /// nothing to follow, which is a null rather than a failure.
    ///
    /// Nothing here is awaited by the caller. Playing a film returns as soon as the player is
    /// running; following it takes as long as the film does.
    /// </remarks>
    public static class PlaybackTracking
    {
        /// <summary>
        /// Follows <paramref name="launch"/> until the film ends, or returns null when there is
        /// nothing to follow.
        /// </summary>
        /// <param name="ct">
        /// The app's lifetime. Cancelling it — closing the window mid-film — sends a final stop at
        /// the last position seen rather than abandoning the session silently.
        /// </param>
        public static Task? Follow(
            MediaPlayerLauncher.LaunchedPlayer? launch,
            JellyfinClient? client,
            string? itemId,
            CancellationToken ct = default)
        {
            if (launch?.Control is null) return null;
            if (client is null || !client.IsConfigured) return null;
            if (string.IsNullOrWhiteSpace(itemId)) return null;

            return RunAsync(launch.Control, new JellyfinPlaybackSink(client, itemId), ct);
        }

        private static async Task RunAsync(
            VlcControlEndpoint control,
            IPlaybackReportSink sink,
            CancellationToken ct)
        {
            using var reader = new HttpVlcStatusReader(control);

            var reporter = new PlaybackReporter(
                reader,
                sink,
                log: message => AppLog.Write("jellyfin.log", JellyfinClient.Redact(message)));

            try
            {
                await reporter.RunAsync(ct);
            }
            catch (Exception ex)
            {
                // The reporter already swallows everything it can name. This is the last guard:
                // an unobserved exception on a task nobody awaits would otherwise be a crash at
                // whatever moment the finalizer ran, for a feature the viewer never asked for.
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"progress reporting failed: {ex.Message}"));
            }
        }
    }
}
