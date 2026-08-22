namespace UrDatabase.Models
{
    /// <summary>
    /// One item in the row of facts under the title on the details screen: a small tracked label
    /// over a value, separated from its neighbour by a hairline.
    /// </summary>
    public class DetailFact
    {
        /// <summary>The source or the kind of the number — <c>"IMDB"</c>, <c>"RUNTIME"</c>.</summary>
        public string Label { get; set; } = "";

        public string Value { get; set; } = "";

        /// <summary>
        /// How the value is inked. The two ratings on this screen come from different services
        /// and measure different populations, so they are never inked the same way.
        /// </summary>
        public DetailFactKind Kind { get; set; } = DetailFactKind.Plain;

        /// <summary>
        /// Whether a hairline is drawn after this fact. Decided when the row is built, because
        /// which fact is last depends on which ones a given film actually has, and a trailing
        /// hairline hanging off the end of the row is the giveaway that nobody checked.
        /// </summary>
        public bool ShowSeparator { get; set; }

        /// <summary>
        /// Exposed as flags so the view can switch a style class on them directly, rather than
        /// going through a converter to turn an enum into a brush.
        /// </summary>
        public bool IsImdb => Kind == DetailFactKind.Imdb;

        public bool IsServer => Kind == DetailFactKind.Server;
    }

    public enum DetailFactKind
    {
        Plain,

        /// <summary>The IMDb rating, from OMDb. Inked in the accent.</summary>
        Imdb,

        /// <summary>Jellyfin's own community rating, and the "on the server" marker.</summary>
        Server
    }
}
