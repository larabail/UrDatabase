namespace UrDatabase.Models
{
    /// <summary>
    /// What a path claims an episode is: which programme it belongs to, where it sits in the run,
    /// and what the episode itself is called.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="ParsedMedia"/> and just as dumb: it records what was read off
    /// disk, not what TMDB later says. The difference is that an episode is only identifiable in
    /// the context of a programme, so <see cref="SeriesTitle"/> is the load-bearing field — an
    /// episode that cannot name its programme cannot be filed, and the parser refuses it rather
    /// than inventing one.
    ///
    /// <see cref="SeasonNumber"/> is nullable although nothing in the first version produces a
    /// null one: every shape recognised today carries a season, either in the filename or in the
    /// directory above it. Absolute numbering, which is ordinary for anime and is deliberately not
    /// recognised yet, has no season at all, and a field that has to widen later is worse than one
    /// that was honest from the start. <see cref="Services.SeriesGrouping"/> already files a
    /// season-less episode under "Episodes", so nothing downstream has to change when it does.
    /// </remarks>
    public sealed record ParsedEpisode(
        string SeriesTitle,
        int? SeriesYear,
        int? SeasonNumber,
        int EpisodeNumber,
        string EpisodeTitle);
}
