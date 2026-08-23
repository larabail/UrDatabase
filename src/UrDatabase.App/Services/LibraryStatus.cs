using System.Globalization;

namespace UrDatabase.Services
{
    /// <summary>
    /// The one line of text under the library, which is the only thing that tells a user whether
    /// what they are looking at is the whole story.
    ///
    /// Pure and out of the window because it has to get one case right that is easy to get wrong:
    /// "no library yet" must not appear when a server has supplied several hundred films, and
    /// must still appear when nothing has been scanned and there is no server either.
    /// </summary>
    public static class LibraryStatus
    {
        /// <summary>
        /// Describes what is on screen.
        /// </summary>
        /// <param name="localCount">Films from the local catalogue.</param>
        /// <param name="localWithPosters">How many of those have artwork.</param>
        /// <param name="remoteCount">Films from a Jellyfin server, cached or freshly synced.</param>
        /// <param name="hasLocalDatabase">False when no catalogue file exists at all.</param>
        /// <param name="databasePath">Where one would be, named only when there is none.</param>
        /// <param name="remoteSeriesCount">
        /// Television series from the same server. Counted separately because they are not films
        /// and saying so is the whole job of this line — a server holding four hundred episodes of
        /// television and no films used to be summarised as "0 films", which is true of films and
        /// false about the library.
        /// </param>
        public static string Describe(
            int localCount,
            int localWithPosters,
            int remoteCount,
            bool hasLocalDatabase,
            string databasePath,
            int remoteSeriesCount = 0)
        {
            if (!hasLocalDatabase && remoteCount == 0 && remoteSeriesCount == 0)
                return $"No library yet. Expected a database at {databasePath}.";

            var posters = $"Posters present: {localWithPosters.ToString(CultureInfo.InvariantCulture)}/{localCount.ToString(CultureInfo.InvariantCulture)}";

            if (remoteCount == 0 && remoteSeriesCount == 0) return posters;

            var parts = new System.Collections.Generic.List<string>();

            if (remoteCount > 0)
                parts.Add($"{remoteCount.ToString(CultureInfo.InvariantCulture)} {(remoteCount == 1 ? "film" : "films")}");

            if (remoteSeriesCount > 0)
                parts.Add($"{remoteSeriesCount.ToString(CultureInfo.InvariantCulture)} series");

            return $"{posters} · {string.Join(" and ", parts)} on the Jellyfin server";
        }
    }
}
