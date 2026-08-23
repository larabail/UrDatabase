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
        /// <summary>The primary button on a film nobody is part way through.</summary>
        public const string PlayLabel = "▶  Play";

        /// <summary>The primary button on a film that will actually pick up where it was left.</summary>
        public const string ContinueLabel = "▶  Continue watching";

        /// <summary>The way back to the beginning, beside the button above.</summary>
        public const string StartAgainLabel = "Start again";

        /// <summary>
        /// Whether this film can be resumed right now, which is the only thing that entitles the
        /// screen to say so.
        /// </summary>
        /// <remarks>
        /// Four things have to hold at once, and each of them fails in ordinary use:
        ///
        /// The server has to have a position for it. It has to be a film that is actually being
        /// streamed — a downloaded copy is opened with the system's own opener, which is handed a
        /// path and nothing else and cannot be told where to start. There has to be a stream at
        /// all, which there is not when the server could not be reached. And the installed player
        /// has to be one that takes an offset, which VLC does and IINA does not.
        ///
        /// The alternative was to label the button "Continue watching" whenever a position exists
        /// and let it start from the beginning when it cannot honour that. That is worse than
        /// never offering it: a button that names what it will do and then does something else
        /// teaches somebody not to trust the rest of the screen.
        /// </remarks>
        public static bool CanResume(MovieDetailsVm vm, bool playerCanSeek)
        {
            if (vm is null) throw new ArgumentNullException(nameof(vm));

            return playerCanSeek &&
                   vm.IsRemote &&
                   vm.HasResumePosition &&
                   string.IsNullOrWhiteSpace(vm.DownloadedPath) &&
                   !string.IsNullOrWhiteSpace(vm.StreamUrl);
        }

        /// <summary>What the primary button says.</summary>
        public static string PlayButtonLabel(MovieDetailsVm vm, bool playerCanSeek) =>
            CanResume(vm, playerCanSeek) ? ContinueLabel : PlayLabel;

        /// <summary>
        /// Where the primary button should open the film: the saved position, or the beginning.
        /// </summary>
        /// <remarks>
        /// Zero whenever the label does not promise otherwise, so the two can never disagree —
        /// the button's words and its behaviour are read off the same answer.
        /// </remarks>
        public static long ResumeFrom(MovieDetailsVm vm, bool playerCanSeek) =>
            CanResume(vm, playerCanSeek) ? vm.ResumePositionTicks : 0;

        /// <summary>
        /// The line under the buttons. Its job is to be honest about how much the app knows — the
        /// old one said "No local file linked. Play will open nothing." while Play cheerfully
        /// opened whichever file's name happened to contain the title.
        /// </summary>
        public static string FileNote(MovieDetailsVm vm, bool playerCanSeek = false)
        {
            if (vm is null) throw new ArgumentNullException(nameof(vm));

            if (vm.IsRemote)
            {
                // A copy on this disk is the answer to every question the server raises, so it is
                // said first: it plays whether or not the server is reachable.
                if (!string.IsNullOrWhiteSpace(vm.DownloadedPath))
                    return $"Downloaded to {vm.DownloadedPath}. Plays with the server switched off.";

                // Never the URL itself: it carries an access token.
                if (string.IsNullOrWhiteSpace(vm.StreamUrl))
                    return "On the Jellyfin server, which could not be reached. Download or Play will not work until it is back.";

                if (CanResume(vm, playerCanSeek))
                    return $"{Describe(vm)}. Continue watching resumes there, and your progress goes back to the server.";

                // A position the server has and this machine cannot act on. Saying so is the
                // difference between a feature that looks broken and one that is explained: an
                // IINA user would otherwise see a Continue watching row and a Play button that
                // starts from the beginning, with nothing anywhere to connect the two.
                if (vm.HasResumePosition && !playerCanSeek)
                    return $"{Describe(vm)}. Only VLC can open a film part way through, so Play starts at the beginning.";

                return "Streams from your Jellyfin server. Play opens it in VLC or IINA. Download keeps a copy for offline.";
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
        /// How far through, in the sentence above, with its first letter left as the card sets it:
        /// <c>"42 MIN LEFT"</c> becomes <c>"42 min left"</c>, which is running text rather than the
        /// mono capitals the card prints.
        /// </summary>
        private static string Describe(MovieDetailsVm vm) =>
            string.IsNullOrWhiteSpace(vm.ResumeNote)
                ? "You are part way through this film"
                : vm.ResumeNote!.ToLowerInvariant();

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
