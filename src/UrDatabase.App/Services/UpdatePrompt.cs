using System;

namespace UrDatabase.Services
{
    /// <summary>
    /// Whether the update banner appears, and every word on it, decided outside the window.
    ///
    /// Here rather than in <c>MainWindow</c>'s code-behind for the reason the rest of this folder
    /// exists: nothing reachable only from a window can be tested without a UI thread, and "does a
    /// user who dismissed 0.11.0 get told about 0.12.0" is exactly the sort of rule that is wrong
    /// for a year because nobody can run it.
    ///
    /// The strings say what the button will actually do. An update prompt that says "Update now"
    /// and then opens a web page has lied, and the next one it shows will be believed less.
    /// </summary>
    public static class UpdatePrompt
    {
        /// <summary>What the button says when the app can fetch the build itself.</summary>
        public const string DownloadAction = "Update now";

        /// <summary>And when it cannot, so the honest offer is the website.</summary>
        public const string WebsiteAction = "Open downloads";

        public const string DismissAction = "Later";

        /// <summary>And once the build is on the disk, because the window it opened can be closed.</summary>
        public const string OpenAgainAction = "Open again";

        /// <summary>
        /// Whether to show the banner at all.
        ///
        /// A version the user has already dismissed stays dismissed until a newer one exists. Not
        /// "until this one is superseded": the comparison is against what they skipped rather than
        /// for equality with it, so somebody who skipped 0.12.0 is not shown 0.11.9 the day an old
        /// release is edited and reappears at the top of the feed.
        /// </summary>
        public static bool ShouldShow(AvailableUpdate? update, string? skippedVersion)
        {
            if (update is null) return false;
            if (skippedVersion is null) return true;

            return AppVersion.IsNewer(update.Version, skippedVersion);
        }

        /// <summary>The line that has to work on its own, because it is the one people read.</summary>
        public static string Headline(AvailableUpdate update)
        {
            if (update is null) throw new ArgumentNullException(nameof(update));

            return $"UrDatabase {update.Version} is available";
        }

        /// <summary>
        /// The line underneath. It says which version is running — an update notice that does not
        /// is asking somebody to take its word for it — and then exactly what pressing the button
        /// does, including the part the app cannot do for them.
        /// </summary>
        public static string Detail(AvailableUpdate update, string? runningVersion)
        {
            if (update is null) throw new ArgumentNullException(nameof(update));

            var running = AppVersion.Text(runningVersion) is string text ? $"You have {text}. " : "";

            if (update.Asset is not UpdateAsset asset)
                return running + "There is no build for this machine on that release, so this opens the downloads page.";

            var size = asset.Bytes > 0 ? $" ({ByteSize.Describe(asset.Bytes)})" : "";
            return running +
                   $"Downloads {asset.Name}{size} and opens it — installing it is still yours to do.";
        }

        /// <summary>What the action button says, which follows from whether there is a file to fetch.</summary>
        public static string ActionText(AvailableUpdate update)
        {
            if (update is null) throw new ArgumentNullException(nameof(update));

            return update.Asset is null ? WebsiteAction : DownloadAction;
        }

        /// <summary>The detail line while bytes are moving.</summary>
        public static string Downloading(UpdateProgress progress) => $"Downloading… {progress.Describe()}";

        /// <summary>
        /// The detail line once it has landed. It names the file's own folder, because the app has
        /// opened the archive for them but the thing they have to do next — drag it into
        /// Applications, or unpack it over the old copy — happens in a window this app does not
        /// own, and being told where the file is beats hunting for it.
        /// </summary>
        public static string Downloaded(string? path)
        {
            var where = string.IsNullOrWhiteSpace(path) ? "your downloads folder" : path;
            return $"Downloaded to {where}. Quit UrDatabase before installing it over this copy.";
        }

        /// <summary>
        /// What is said when the fetch failed. Always ends somewhere the user can still get the
        /// build: a failed download that leaves them with nothing to press is a dead end, and the
        /// website is the same one they would have used had the app never offered.
        /// </summary>
        public static string DownloadFailed(string? reason)
        {
            var because = string.IsNullOrWhiteSpace(reason) ? "That download did not finish." : reason.Trim();
            return $"{because} Open downloads goes to the website instead.";
        }

        /// <summary>The detail line after the user stops a download themselves.</summary>
        public const string DownloadStopped = "Download stopped. Nothing was kept.";
    }
}
