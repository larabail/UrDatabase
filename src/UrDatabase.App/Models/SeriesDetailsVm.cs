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

        /// <summary>
        /// The <c>series</c> row, for a programme a scan found on this disk. Zero for a server
        /// programme. A series is one or the other: a show held in both places is two cards.
        /// </summary>
        public long LocalId { get; set; }

        /// <summary>True when the episodes are files on this disk rather than items on a server.</summary>
        public bool IsLocal => LocalId > 0;

        /// <summary>
        /// The TMDB programme a local series has been identified as. Null for a server programme,
        /// and for a local one TMDB has not been able to confirm — which is the series layer 1
        /// shipped, named only by its files.
        /// </summary>
        public int? TmdbId { get; set; }

        /// <summary>True for a programme on this disk that TMDB has described.</summary>
        public bool IsIdentified => IsLocal && TmdbId is int id && id > 0;

        /// <summary>
        /// Whether a TMDB key is configured, which decides what the screen says about a local
        /// programme with nothing known about it yet: one that will be looked up, or one that
        /// cannot be.
        /// </summary>
        public bool TmdbConfigured { get; set; }

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
