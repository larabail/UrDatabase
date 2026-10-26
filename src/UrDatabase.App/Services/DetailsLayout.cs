namespace UrDatabase.Services
{
    /// <summary>
    /// How tall the plot on a film's details page may grow, given what sits under it.
    /// </summary>
    /// <remarks>
    /// The column is a stack: title, facts, plot, stills, shelf, credits, one after the other at
    /// their own heights, and the whole column scrolls when the window is shorter than that. No
    /// row stretches to fill the window. One that did — the shelf, the strip or the plot taking
    /// "the space that is left" — opened a band of empty backdrop above and below itself on a tall
    /// window and pinned the credits to its foot, which read as a page that had failed to load.
    ///
    /// The plot is still capped when there is something under it, and scrolls inside the cap, so a
    /// long synopsis does not push the stills and the shelf below the fold. With neither it has no
    /// cap: there is nothing for it to keep in view, and the column scrolls if it has to.
    /// </remarks>
    public static class DetailsLayout
    {
        public const double PlotCapWithShelf = 168;
        public const double PlotCapWithShelfAndStills = 120;
        public const double PlotCapWithStills = 240;

        public static double PlotMaxHeight(bool hasRelated, bool hasStills) => (hasRelated, hasStills) switch
        {
            (true, true) => PlotCapWithShelfAndStills,
            (true, false) => PlotCapWithShelf,
            (false, true) => PlotCapWithStills,
            _ => double.PositiveInfinity,
        };
    }
}
