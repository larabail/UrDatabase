using System;
using System.IO;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// What the Play button does, and what the window says about it, decided outside the window.
    ///
    /// It lived in <c>MovieDetailsWindow</c>'s code-behind, which is a large part of why it went
    /// wrong unnoticed: nothing reachable only from a window can be tested without a UI thread, so
    /// the rule "play whatever file we found" was never once asserted on. The window is left with
    /// rendering a string and reacting to an answer, both of which are hard to get wrong.
    /// </summary>
    public static class PlayPrompts
    {
        /// <summary>
        /// The line under the buttons. Its job is to be honest about how much the app knows — the
        /// old one said "No local file linked. Play will open nothing." while Play cheerfully
        /// opened whichever file's name happened to contain the title.
        /// </summary>
        public static string FileNote(MovieDetailsVm vm)
        {
            if (vm is null) throw new ArgumentNullException(nameof(vm));

            if (vm.IsRemote)
            {
                // A copy on this disk is the answer to every question the server raises, so it is
                // said first: it plays whether or not the server is reachable.
                if (!string.IsNullOrWhiteSpace(vm.DownloadedPath))
                    return $"Downloaded to {vm.DownloadedPath}. Plays with the server switched off.";

                // Never the URL itself: it carries an access token.
                return string.IsNullOrWhiteSpace(vm.StreamUrl)
                    ? "On the Jellyfin server, which could not be reached. Download or Play will not work until it is back."
                    : "Streams from your Jellyfin server. Play opens it in VLC or IINA. Download keeps a copy for offline.";
            }

            return vm.FileMatch switch
            {
                PlayTargetKind.Linked when vm.HasFile => $"File: {FileName(vm.FilePath)}",
                PlayTargetKind.Suggested when vm.HasFile =>
                    $"No file is linked to this film. {FileName(vm.FilePath)} looks like it, so Play " +
                    "will ask before opening it. Link File… settles it for good.",
                _ => "No file is linked to this film. Use Link File… to choose one."
            };
        }

        /// <summary>
        /// Why this film cannot be opened right now, or null when it can. Checked immediately
        /// before handing the path to the operating system, and not only when the link was made.
        ///
        /// Both halves matter. <see cref="PlayTargetResolver.LinkFile"/> refuses to record a path
        /// that is not a video file, but the row it guards is ordinary local state: a catalogue
        /// copied from another machine, restored from a backup, or written by a build that
        /// predates that rule can all name something else, and the app opens whatever the path
        /// says with the operating system's own launcher. Checking on the way in and again on the
        /// way out is the point — the row can change between the two.
        /// </summary>
        public static string? DescribeRefusal(MovieDetailsVm vm, Func<string, bool>? fileExists = null)
        {
            if (vm is null) throw new ArgumentNullException(nameof(vm));

            if (vm.IsRemote)
            {
                // A downloaded copy is an ordinary file and answers for itself, so the server
                // being unreachable stops mattering — which is the entire reason to download one.
                if (!string.IsNullOrWhiteSpace(vm.DownloadedPath))
                    return PlayTargetResolver.DescribeLinkRefusal(vm.DownloadedPath, fileExists);

                return string.IsNullOrWhiteSpace(vm.StreamUrl)
                    ? "This film is on your Jellyfin server, which could not be reached. " +
                      "It will play again once you are back on the same network as the server."
                    : null;
            }

            if (!vm.HasFile) return NothingToPlay;

            return PlayTargetResolver.DescribeLinkRefusal(vm.FilePath, fileExists);
        }

        /// <summary>
        /// True when opening this file would be acting on a guess, so the user is asked first.
        /// The distinction is the fix: a link the scan recorded plays, a filename that merely
        /// resembles the title does not.
        /// </summary>
        public static bool NeedsConfirmation(MovieDetailsVm vm)
        {
            if (vm is null) throw new ArgumentNullException(nameof(vm));

            return !vm.IsRemote && vm.HasFile && vm.FileMatch == PlayTargetKind.Suggested;
        }

        /// <summary>
        /// The question asked before playing a guess. It names the file and says why it might be
        /// wrong, because "are you sure?" gives somebody nothing to be sure with.
        /// </summary>
        public static string ConfirmationQuestion(MovieDetailsVm vm)
        {
            if (vm is null) throw new ArgumentNullException(nameof(vm));

            var film = string.IsNullOrWhiteSpace(vm.Title) ? "this film" : vm.Title;

            return $"No file is linked to {film}. Play {FileName(vm.FilePath)}?" +
                   $"{Environment.NewLine}{Environment.NewLine}" +
                   "It was matched on its name alone, so it may be a different film.";
        }

        /// <summary>
        /// What to say when there is nothing to open. Names the way out rather than only the
        /// problem.
        /// </summary>
        public const string NothingToPlay = "No file is linked to this film. Use Link File… to choose one.";

        private static string FileName(string? path) =>
            string.IsNullOrWhiteSpace(path) ? "" : Path.GetFileName(path);
    }
}
