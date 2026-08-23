namespace UrDatabase.Models
{
    /// <summary>
    /// What a card in the library actually is.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="MovieSource"/>, and deliberately: where a thing is and what a
    /// thing is are two different questions, and the library already learned once that folding
    /// two questions into one control buries whichever population is smaller.
    ///
    /// <see cref="Film"/> is the default everywhere, so every row the <c>movies</c> table
    /// materialises and every existing test is right without saying so. Only a Jellyfin
    /// <c>Series</c> is anything else — a scanned file is always catalogued as a film, because
    /// nothing on this machine parses an episode out of a filename yet.
    /// </remarks>
    public enum MediaKind
    {
        Film = 0,

        /// <summary>A television series: a shelf of seasons, not something that plays on its own.</summary>
        Series = 1
    }
}
