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
                // Never the URL itself: it carries an access token.
                return string.IsNullOrWhiteSpace(vm.StreamUrl)
                    ? "On the Jellyfin server, which could not be reached. Play will not work until it is back."
                    : "Streams from your Jellyfin server. Play opens it in VLC or IINA.";
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
