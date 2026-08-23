using System.Collections.Generic;

namespace UrDatabase.Models
{
    /// <summary>
    /// One television series, as the details screen shows it.
    /// </summary>
    /// <remarks>
    /// Its own type rather than a flag on <see cref="MovieDetailsVm"/>. That one carries a file
    /// path, a play target, a TMDB match to correct and a download — six things that describe one
    /// video, none of which a series has, because a series is a folder and it is the episodes that
    /// play. Folding the two together would mean a screen full of properties that are meaningless
    /// for half of what it is asked to show, and a Play button whose meaning depended on a
    /// boolean.
    ///
    /// Everything on it comes from the cache, so the screen opens with the server switched off.
    /// The seasons arrive separately, and later — see <see cref="Services.SeriesLoader"/>.
    /// </remarks>
    public class SeriesDetailsVm
    {
        /// <summary>The Jellyfin item id of the series itself. What its episodes are asked for by.</summary>
        public string RemoteId { get; set; } = "";

        public string Title { get; set; } = "";

        /// <summary>The year it began. Jellyfin reports one year, not a range.</summary>
        public int? Year { get; set; }

        public string Genres { get; set; } = "";
        public string Overview { get; set; } = "";

        /// <inheritdoc cref="MovieDetailsVm.CommunityRating"/>
        public double? CommunityRating { get; set; }

        public string? ImdbId { get; set; }
        public double? ImdbRating { get; set; }

        public string? PosterPath { get; set; }
        public string? BackdropUrl { get; set; }

        public List<string> TopCast { get; set; } = new();
        public List<string> KeyCrew { get; set; } = new();

        /// <summary>
        /// What the last sync recorded, which is what the facts row shows until the seasons
        /// themselves arrive. Null when the server never said, in which case the row says nothing
        /// rather than claiming zero.
        /// </summary>
        public int? SeasonCount { get; set; }

        /// <inheritdoc cref="SeasonCount"/>
        public int? EpisodeCount { get; set; }
    }
}
