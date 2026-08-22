using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public sealed record PlayerCandidate(string Name, string ExecutablePath);

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
        public static ProcessStartInfo BuildStartInfo(PlayerCandidate player, string url)
        {
            if (player is null) throw new ArgumentNullException(nameof(player));
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("A stream URL is required.", nameof(url));

            var psi = new ProcessStartInfo(player.ExecutablePath) { UseShellExecute = false };
            psi.ArgumentList.Add(url);
            return psi;
        }

        /// <summary>
        /// Streams <paramref name="url"/> in whichever player is installed.
        /// </summary>
        /// <exception cref="MediaPlayerNotFoundException">Neither player is installed.</exception>
        public static void Play(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("A stream URL is required.", nameof(url));

            var player = Find();
            if (player is null) throw new MediaPlayerNotFoundException(NotInstalledMessage);

            // Only the player's name is logged. The URL is a credential.
            AppLog.Write("jellyfin.log", $"streaming through {player.Name}");
            Process.Start(BuildStartInfo(player, url));
        }
    }
}
