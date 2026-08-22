using System;

namespace UrDatabase.Services
{
    /// <summary>
    /// What to say when the details screen has nothing to show.
    /// </summary>
    /// <remarks>
    /// There are three different reasons a film has no plot or no cast, and they are not
    /// interchangeable:
    ///
    /// <list type="bullet">
    /// <item>the film is on a Jellyfin server, which describes its own films — so anything absent
    /// is absent from the server's own metadata;</item>
    /// <item>the film is local and no TMDB key is configured, so nothing was ever asked;</item>
    /// <item>the film is local, TMDB was asked, and TMDB does not have it.</item>
    /// </list>
    ///
    /// The screen used to print "None found for this film" for all three. On an install with no
    /// TMDB key — which is the default, since both keys are optional — that sentence is simply
    /// untrue: it blames the film for a question nobody asked, and it hides the one thing the
    /// user could actually do about it.
    /// </remarks>
    public static class MissingMetadata
    {
        /// <summary>Why a cast or crew list is empty.</summary>
        public static string CreditsNotice(bool isRemote, bool tmdbConfigured)
        {
            if (isRemote) return "Not supplied by the server for this film.";

            return tmdbConfigured
                ? "None found for this film."
                : "Add a TMDB key under Settings to fill in cast and crew.";
        }

        /// <summary>Why there is no plot summary.</summary>
        public static string OverviewNotice(bool isRemote, bool tmdbConfigured)
        {
            if (isRemote) return "The server has no plot summary for this film.";

            return tmdbConfigured
                ? "No plot summary was available for this film."
                : "No plot summary yet. Add a TMDB key under Settings and UrDatabase will fill in the plot, runtime, genres, cast and crew.";
        }
    }
}
