using System;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>
    /// Streaming one thing from the server: hand it to a player, at the position it was left at,
    /// and follow it for as long as it plays.
    /// </summary>
    /// <remarks>
    /// One method rather than the same four lines at each screen that can start a stream, and that
    /// is the whole point of it. Playing and following are two calls, and a caller that makes the
    /// first and forgets the second gets a film that plays perfectly and reports nothing — which
    /// is exactly what the series screen did until #77, silently, for as long as television has
    /// been in this app. A second entry point into playback is a second chance to make that
    /// mistake, so there is one door and it does both.
    ///
    /// The launch itself is a delegate so that the whole of this is testable. A test can assert
    /// that a stream is asked for with progress reporting, at the right position, and that
    /// following it actually starts — without a player, a socket or a server.
    /// </remarks>
    public static class StreamPlayback
    {
        /// <summary>
        /// Hands a URL to a player. <see cref="MediaPlayerLauncher.Play"/> in the app; a recorder
        /// in the tests.
        /// </summary>
        public delegate MediaPlayerLauncher.LaunchedPlayer Launcher(string url, bool withProgressReporting, long startTicks);

        /// <summary>
        /// Plays <paramref name="itemId"/> from <paramref name="startTicks"/>, and returns the task
        /// following it — or null when there is nothing to follow, which is an ordinary outcome
        /// and not a failure.
        /// </summary>
        /// <param name="appLifetime">
        /// The window's lifetime rather than a screen's. A film outlives the screen it was started
        /// from, and reporting has to outlive it too; closing the app ends it with a final stop.
        /// </param>
        /// <exception cref="MediaPlayerNotFoundException">
        /// Neither player is installed. Raised by the launcher and deliberately not caught here:
        /// the message is written to be shown to somebody, and only the caller has a window.
        /// </exception>
        public static Task? Start(
            JellyfinClient client,
            string itemId,
            long startTicks = 0,
            CancellationToken appLifetime = default,
            Launcher? launcher = null)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));

            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("A Jellyfin item id is required.", nameof(itemId));

            var id = itemId.Trim();

            // The interface is only asked for when there is somewhere for it to report to. A port
            // and a password for something with no server behind it would be a socket opened for
            // nothing.
            var canReport = PlaybackTracking.CanReport(client, id);

            var launch = (launcher ?? MediaPlayerLauncher.Play)(client.BuildStreamUrl(id), canReport, startTicks);

            return PlaybackTracking.Follow(launch, client, id, appLifetime);
        }
    }
}
