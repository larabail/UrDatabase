using System.Collections.Generic;

namespace UrDatabase.Models
{
    /// <summary>
    /// One episode as the series screen lists it: already numbered, already described, ready to
    /// bind. Nothing here is computed in the view.
    /// </summary>
    public sealed class EpisodeRow
    {
        /// <summary>The Jellyfin item id. What Play asks the server to stream. Empty for a local episode.</summary>
        public string ItemId { get; set; } = "";

        /// <summary>
        /// The file Play opens, for an episode on this disk: the best copy the last scan still
        /// found. Null for a server episode, and for a local one whose every copy has gone.
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// False for a local episode the last scan could not find. It stays listed — a gap in a
        /// season is worth seeing — but dimmed, and Play says why rather than failing.
        /// </summary>
        public bool IsAvailable { get; set; } = true;

        /// <summary>Bound to the row, so an episode that will not play looks like one.</summary>
        public double RowOpacity => IsAvailable ? 1.0 : 0.45;

        /// <summary>
        /// <c>S01E02</c>, or <c>E02</c> when the season is unnumbered, or empty when the server
        /// numbered nothing at all. Built by <see cref="Services.SeriesGrouping"/>.
        /// </summary>
        public string Label { get; set; } = "";

        public bool HasLabel => Label.Length > 0;

        /// <summary>
        /// The episode's own name, or a stand-in built from its number. Never empty: a blank row
        /// in a list of twenty-four is unclickable in the sense that matters — nobody can tell
        /// what they would be clicking.
        /// </summary>
        public string Title { get; set; } = "";

        /// <summary><c>"48 min"</c>, or empty when the server did not say.</summary>
        public string Runtime { get; set; } = "";

        public bool HasRuntime => Runtime.Length > 0;

        public string Overview { get; set; } = "";

        public bool HasOverview => Overview.Length > 0;

        /// <summary>
        /// The whole plot, for the tooltip, or null when there is none. Null rather than an empty
        /// string because Avalonia shows a tooltip for one and an empty box for the other.
        /// </summary>
        public string? Tip => HasOverview ? Overview : null;
    }

    /// <summary>
    /// One season, and the episodes in it.
    /// </summary>
    /// <remarks>
    /// Built from the server's season list where there is one and synthesised from the episodes
    /// themselves where there is not, so a show whose seasons the server declined to enumerate
    /// still lists its episodes under headings rather than as one undifferentiated run.
    /// </remarks>
    public sealed class SeasonGroup
    {
        /// <summary>The server's name for it, or "Season 2" when it had none.</summary>
        public string Name { get; set; } = "";

        /// <summary>Its number, when it has one. Null for a season nobody numbered.</summary>
        public int? Number { get; set; }

        public IReadOnlyList<EpisodeRow> Episodes { get; set; } = new List<EpisodeRow>();

        /// <summary>How many episodes, as it is printed beside the heading: <c>"12 EPISODES"</c>.</summary>
        public string CountLabel => Services.SeriesGrouping.CountLabel(Episodes.Count);

        /// <summary>Whether this is the season being shown. Bound, as <see cref="GenreChip"/> is.</summary>
        public bool IsSelected { get; set; }
    }
}
