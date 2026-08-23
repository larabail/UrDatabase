using System;
using System.Globalization;

namespace UrDatabase.Services
{
    /// <summary>
    /// What one Jellyfin sync brought back, and the sentence the status line puts under the
    /// library because of it.
    /// </summary>
    /// <remarks>
    /// A value rather than a bare film count, which is what the sync used to return. The count was
    /// turned into a sentence in the window's code-behind, where the wording could not be
    /// asserted on — and "412 films from the server" is now wrong in two directions: it is wrong
    /// about a server that also holds television, and it is wrong about one that holds only
    /// television, where it reported zero films and mentioned nothing else.
    /// </remarks>
    public readonly record struct JellyfinSyncResult(int Films, int Series)
    {
        public int Total => Films + Series;

        /// <summary>
        /// What the status line says. Each half is named only when there is one, so a film-only
        /// server reads exactly as it did before television existed and never grows a permanent
        /// "and 0 series".
        /// </summary>
        public string Describe()
        {
            if (Total == 0) return "Jellyfin: the server reported nothing this app can read.";

            var parts = new System.Collections.Generic.List<string>();

            if (Films > 0)
                parts.Add($"{Films.ToString(CultureInfo.InvariantCulture)} {(Films == 1 ? "film" : "films")}");

            // "series" is its own plural, which is the sort of thing that reaches a screenshot as
            // "1 seriess" if it is left to a format string.
            if (Series > 0)
                parts.Add($"{Series.ToString(CultureInfo.InvariantCulture)} series");

            return $"Jellyfin: {string.Join(" and ", parts)} from the server.";
        }
    }
}
