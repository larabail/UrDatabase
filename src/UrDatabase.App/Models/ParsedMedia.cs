namespace UrDatabase.Models
{
    /// <summary>
    /// What a filename claims to be: a display title and, when the name carries one, the year of
    /// release. Deliberately dumb — it records what was read off disk, not what TMDB later says.
    /// </summary>
    public sealed record ParsedMedia(string Title, int? Year);
}
