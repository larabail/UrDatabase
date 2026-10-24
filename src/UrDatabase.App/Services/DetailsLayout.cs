namespace UrDatabase.Services
{
    /// <summary>
    /// How the details column shares its height between the plot, the strip of stills and the
    /// shelf of related films, given which of the last two a film has.
    /// </summary>
    /// <remarks>
    /// Exactly one of the three takes the space that is left. With a shelf it is the shelf, and
    /// the plot is capped so the posters have somewhere to be — lower still when the stills need
    /// room too, because a plot that scrolls loses nothing and a shelf pushed through the credits
    /// loses everything. With stills alone the strip takes it and sits directly under the plot,
    /// which is where it was asked for, rather than at the foot of an empty band. With neither,
    /// the plot has the space, exactly as the screen was before either existed.
    /// </remarks>
    public readonly record struct DetailsLayout(
        bool PlotFills, bool StillsFill, bool RelatedFills, double PlotMaxHeight)
    {
        public const double PlotCapWithShelf = 168;
        public const double PlotCapWithShelfAndStills = 120;
        public const double PlotCapWithStills = 240;

        public static DetailsLayout For(bool hasRelated, bool hasStills) => (hasRelated, hasStills) switch
        {
            (true, true) => new(false, false, true, PlotCapWithShelfAndStills),
            (true, false) => new(false, false, true, PlotCapWithShelf),
            (false, true) => new(false, true, false, PlotCapWithStills),
            _ => new(true, false, false, double.PositiveInfinity),
        };
    }
}
