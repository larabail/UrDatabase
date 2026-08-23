using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace UrDatabase.Services
{
    /// <summary>
    /// Raised when a film could be streamed but there is nothing installed to stream it with.
    /// Its message is written to be shown to a user unchanged.
    /// </summary>
    public sealed class MediaPlayerNotFoundException : Exception
    {
        public MediaPlayerNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Hands a stream URL to a real video player.
    ///
    /// The OS opener is not an option here. Jellyfin direct-plays these files as Matroska, and
    /// both macOS and Windows answer an <c>http://…</c> URL by opening a browser, which downloads
    /// the film or shows a black frame with no sound. VLC and IINA both take a URL as an argument
    /// and play it, so the app looks for one of those and says so plainly when there is neither.
    /// </summary>
    public static class MediaPlayerLauncher
    {
        /// <summary>A player the app knows how to drive, and where its executable lives.</summary>
        public sealed record PlayerCandidate(string Name, string ExecutablePath)
        {
            /// <summary>
            /// The name VLC is listed under, and the only player this app can follow while it
            /// plays. Compared rather than hardcoded at the two places that care.
            /// </summary>
            public const string Vlc = "VLC";

            /// <summary>
            /// True when this player has the HTTP control interface progress reporting needs.
            /// </summary>
            /// <remarks>
            /// IINA is deliberately not included, and cannot be by adding a name here. It is mpv
            /// underneath and exposes a JSON IPC socket rather than an HTTP interface, which is a
            /// different protocol over a different transport — so it plays films exactly as it
            /// always has and reports nothing.
            /// </remarks>
            public bool CanReportProgress =>
                string.Equals(Name, Vlc, StringComparison.OrdinalIgnoreCase);

            /// <summary>
            /// True when this player can be told to open a film part way through.
            /// </summary>
            /// <remarks>
            /// A separate question from <see cref="CanReportProgress"/>, though both currently
            /// answer "is it VLC". Reporting needs the HTTP control interface; starting at an
            /// offset needs <c>--start-time</c>, which is an ordinary command line argument a
            /// player could perfectly well have without the other. Kept apart so that whichever
            /// arrives first for some future player does not silently imply the other.
            /// </remarks>
            public bool CanStartAtAnOffset =>
                string.Equals(Name, Vlc, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// What a launch produced: the player that was started, and the control interface it was
        /// given, when it was given one.
        /// </summary>
        /// <remarks>
        /// <see cref="Control"/> is null for IINA, for a VLC that could not be offered a port, and
        /// whenever the caller asked for no interface. Null means "this film plays and nothing
        /// will be reported about it", which is an ordinary outcome rather than a failure.
        /// </remarks>
        public sealed record LaunchedPlayer(PlayerCandidate Player, VlcControlEndpoint? Control);

        /// <summary>Shown when nothing is installed. Names both players, because either will do.</summary>
        public const string NotInstalledMessage =
            "No video player was found. Films on a Jellyfin server are streamed rather than " +
            "downloaded, and need VLC or IINA to play — the system default opens them in a " +
            "browser, which cannot play them. Install either one and try again.";


        /// <summary>
        /// Where the two players install, most likely first. VLC leads on every platform simply
        /// because it exists on all three; IINA is macOS only.
        /// </summary>
        public static IReadOnlyList<PlayerCandidate> KnownPlayers()
        {
            if (OperatingSystem.IsMacOS())
            {
                var home = PlatformPaths.HomeDirectory;
                return new[]
                {
                    // The binary inside the bundle, not `open -a`: `open` treats an http URL as a
                    // web address and hands it to the browser even when an application is named.
                    new PlayerCandidate("VLC", "/Applications/VLC.app/Contents/MacOS/VLC"),
                    new PlayerCandidate("VLC", Path.Combine(home, "Applications/VLC.app/Contents/MacOS/VLC")),
                    new PlayerCandidate("IINA", "/Applications/IINA.app/Contents/MacOS/IINA"),
                    new PlayerCandidate("IINA", Path.Combine(home, "Applications/IINA.app/Contents/MacOS/IINA"))
                };
            }

            if (OperatingSystem.IsWindows())
            {
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                return new[]
                {
                    new PlayerCandidate("VLC", Path.Combine(programFiles, "VideoLAN", "VLC", "vlc.exe")),
                    new PlayerCandidate("VLC", Path.Combine(programFilesX86, "VideoLAN", "VLC", "vlc.exe")),
                    new PlayerCandidate("VLC", Path.Combine(localAppData, "Programs", "VideoLAN", "VLC", "vlc.exe"))
                };
            }

            return new[]
            {
                new PlayerCandidate("VLC", "/usr/bin/vlc"),
                new PlayerCandidate("VLC", "/usr/local/bin/vlc"),
                new PlayerCandidate("VLC", "/snap/bin/vlc"),
                new PlayerCandidate("IINA", "/usr/bin/iina")
            };
        }

        /// <summary>
        /// The first candidate that is actually installed. The <paramref name="exists"/> probe is
        /// a parameter so the choice can be asserted without installing anything.
        /// </summary>
        public static PlayerCandidate? Find(IEnumerable<PlayerCandidate> candidates, Func<string, bool> exists)
        {
            if (candidates is null || exists is null) return null;

            return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.ExecutablePath) && exists(c.ExecutablePath));
        }

        /// <summary>The installed player on this machine, or null when there is none.</summary>
        public static PlayerCandidate? Find() => Find(KnownPlayers(), File.Exists);

        /// <summary>
        /// Builds the launch, without starting it, so it can be asserted in a test. The URL goes
        /// in <c>ArgumentList</c> rather than a command string: it carries an access token and
        /// query separators, and letting a shell see either would break or leak it.
        /// </summary>
        /// <param name="control">
        /// The loopback control interface to add, or null for a plain launch. Only ever supplied
        /// for VLC; see <see cref="PlayerCandidate.CanReportProgress"/>.
        /// </param>
        /// <param name="startAtTicks">
        /// Where to open the film, for one being resumed. Ignored by a player that cannot seek
        /// from the command line, which is why the button offering it is only shown for one that
        /// can — a label promising to continue and a film starting again is worse than no label.
        /// </param>
        public static ProcessStartInfo BuildStartInfo(
            PlayerCandidate player,
            string url,
            VlcControlEndpoint? control = null,
            long startAtTicks = 0)
        {
            if (player is null) throw new ArgumentNullException(nameof(player));
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("A stream URL is required.", nameof(url));

            var psi = new ProcessStartInfo(player.ExecutablePath) { UseShellExecute = false };
            psi.ArgumentList.Add(url);

            if (control is not null && player.CanReportProgress)
            {
                foreach (var argument in VlcControl.BuildArguments(control))
                    psi.ArgumentList.Add(argument);
            }

            if (startAtTicks > 0 && player.CanStartAtAnOffset)
            {
                // Seconds, and invariant: VLC parses this as a float and a machine set to a locale
                // that writes decimals with a comma would otherwise hand it something it reads as
                // a different number, or as nothing.
                psi.ArgumentList.Add("--start-time");
                psi.ArgumentList.Add(
                    PlaybackPosition.TicksToSeconds(startAtTicks).ToString("0.###", CultureInfo.InvariantCulture));
            }

            return psi;
        }

        /// <summary>
        /// Streams <paramref name="url"/> in whichever player is installed, and returns what was
        /// started.
        /// </summary>
        /// <param name="withProgressReporting">
        /// Whether to ask VLC for the control interface that lets the app follow the film. False
        /// when there is nowhere to report to — no server, or a film with no id on it — so a
        /// player is not given an interface nothing is going to read.
        /// </param>
        /// <param name="startAtTicks">
        /// Where to open the film. Zero starts at the beginning, which is every film nobody has
        /// half-watched and every one somebody asked to start again.
        /// </param>
        /// <exception cref="MediaPlayerNotFoundException">Neither player is installed.</exception>
        public static LaunchedPlayer Play(string url, bool withProgressReporting = false, long startAtTicks = 0)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("A stream URL is required.", nameof(url));

            var player = Find();
            if (player is null) throw new MediaPlayerNotFoundException(NotInstalledMessage);

            // Failing to get a port must not stop the film. It costs the resume position for this
            // viewing and nothing else, which is why this is a null rather than an exception.
            var control = withProgressReporting && player.CanReportProgress ? VlcControl.TryCreate() : null;

            // Only the player's name and the port are logged. The URL is a credential and so is
            // the interface password; neither ever reaches a log, a dialog or the status line.
            AppLog.Write("jellyfin.log", VlcControl.Describe(player.Name, control));

            try
            {
                Process.Start(BuildStartInfo(player, url, control, startAtTicks));
            }
            catch when (control is not null)
            {
                // Vanishingly unlikely, and worth one retry: a VLC too old to understand these
                // arguments would refuse to start at all, and the film matters more than the
                // position. The offset is kept — it is a much older argument than the interface
                // is, and losing the place somebody asked to return to is the visible failure.
                // Anything that fails without an interface too is a real failure and is left to
                // the caller.
                AppLog.Write("jellyfin.log", "the control interface was refused; playing without it");
                Process.Start(BuildStartInfo(player, url, control: null, startAtTicks));
                return new LaunchedPlayer(player, null);
            }

            return new LaunchedPlayer(player, control);
        }

        /// <summary>
        /// True when the player installed on this machine can open a film part way through, so the
        /// screen knows whether it may offer to. False with nothing installed at all, which is the
        /// same answer for this purpose: there is nothing to promise.
        /// </summary>
        public static bool CanResumeHere() => Find()?.CanStartAtAnOffset ?? false;

    }
}
