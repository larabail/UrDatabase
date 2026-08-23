using System;
using System.IO;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// What the Upload button offers, refuses and reports, decided outside the window.
    ///
    /// The same bargain <see cref="PlayPrompts"/> makes: nothing reachable only from a window can
    /// be tested without a UI thread, so the sentences live here where they can be asserted on and
    /// the screen is left rendering strings.
    ///
    /// The wording carries one fact that is easy to get wrong and expensive to get wrong. An
    /// upload finishing does not mean the film has appeared in Jellyfin — the bytes are on the
    /// server's disk, and Jellyfin only knows about them once a scan has reached them. Telling
    /// somebody "uploaded" and leaving them to refresh a library that stays empty for a minute is
    /// how a working feature gets reported as broken.
    /// </summary>
    public static class UploadPrompts
    {
        /// <summary>The button, when it is not in the middle of anything.</summary>
        public const string ButtonLabel = "Upload to Jellyfin";

        /// <summary>The same button, while a transfer is running.</summary>
        public const string CancelLabel = "Cancel";

        /// <summary>
        /// What is said when the user stops a transfer. It is specific about the library being
        /// left alone, because the reason to stop one is usually a suspicion that it has made a
        /// mess — and it promises that rather than promising the server is spotless, which cannot
        /// be guaranteed when the thing that stopped the transfer was the connection itself.
        /// </summary>
        public const string Cancelled =
            "Upload stopped. Nothing was added to your Jellyfin library, " +
            "and starting again sends the film from the beginning.";

        /// <summary>
        /// Why this film cannot be sent to the server right now, or null when it can.
        ///
        /// Checked immediately before the transfer as well as when the button is drawn: the linked
        /// path is ordinary local state and the file behind it can be moved, renamed or deleted
        /// between the screen opening and the button being pressed.
        /// </summary>
        public static string? DescribeRefusal(MovieDetailsVm vm, Func<string, bool>? fileExists = null)
        {
            if (vm is null) throw new ArgumentNullException(nameof(vm));

            if (vm.IsRemote)
                return "This film is already on your Jellyfin server.";

            if (vm.IsOnServer)
                return "Your Jellyfin server already has this film.";

            if (!vm.HasFile)
                return "No file is linked to this film, so there is nothing to upload. Use Link File… to choose one.";

            return JellyfinUpload.DescribeRefusal(vm.FilePath, fileExists);
        }

        /// <summary>
        /// True when uploading would be acting on a guess. The catalogue distinguishes a file it
        /// recorded from one that merely looks like the title, and the second is not good enough
        /// to put in somebody's server library under this film's name — where, unlike a mistaken
        /// Play, it is a mess for other people on other devices to find.
        /// </summary>
        public static bool NeedsConfirmation(MovieDetailsVm vm)
        {
            if (vm is null) throw new ArgumentNullException(nameof(vm));

            return !vm.IsRemote && vm.HasFile && vm.FileMatch == PlayTargetKind.Suggested;
        }

        /// <summary>
        /// The question asked before uploading a guess. It names the file and says why it might be
        /// wrong, because "are you sure?" gives somebody nothing to be sure with.
        /// </summary>
        public static string ConfirmationQuestion(MovieDetailsVm vm)
        {
            if (vm is null) throw new ArgumentNullException(nameof(vm));

            var film = string.IsNullOrWhiteSpace(vm.Title) ? "this film" : vm.Title;

            return $"No file is linked to {film}. Upload {FileName(vm.FilePath)} to your Jellyfin server?" +
                   $"{Environment.NewLine}{Environment.NewLine}" +
                   "It was matched on its name alone, so it may be a different film.";
        }

        /// <summary>The line under the buttons while bytes are moving.</summary>
        public static string Progress(JellyfinUploadProgress report) => $"Uploading… {report.Describe()}";

        /// <summary>
        /// What is said when it is done. Three different things, because the three outcomes are
        /// genuinely different and one sentence covering all of them would be true of none.
        /// </summary>
        public static string Describe(JellyfinUploadResult result)
        {
            if (result.AlreadyExisted)
                return $"Your Jellyfin server already has this film, at {result.RemotePath}. Nothing was uploaded.";

            var sent = $"Uploaded {JellyfinDownload.DescribeSize(result.Bytes)} to {result.RemotePath}.";

            return result.LibraryRefreshed
                ? sent + " Jellyfin is scanning for it now, so it will appear on the server shortly."
                : sent + " Jellyfin has not been told to scan — the film will appear at its next scan of the library.";
        }

        private static string FileName(string? path) =>
            string.IsNullOrWhiteSpace(path) ? "" : Path.GetFileName(path);
    }
}
