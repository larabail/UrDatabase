namespace UrDatabase.Models
{
    /// <summary>
    /// One frame from a film, as the details screen shows it: a small image for the strip under
    /// the plot and a large one for when it is clicked.
    /// </summary>
    /// <param name="Thumbnail">A cached file or a URL. What the strip loads.</param>
    /// <param name="Full">
    /// A URL at a size worth looking at full screen. Never cached to disk — it is only fetched
    /// when somebody asks to see it — so the viewer falls back to <paramref name="Thumbnail"/>
    /// when it cannot be reached.
    /// </param>
    public sealed record FilmStill(string Thumbnail, string Full);
}
