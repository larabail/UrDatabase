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
    /// materialises and every existing test is right without saying so. A <c>Series</c> comes
    /// from a Jellyfin server or from the <c>series</c> table a scan fills when it reads a file as
    /// an episode (see <see cref="Services.EpisodeParser"/>).
    /// </remarks>
    public enum MediaKind
    {
        Film = 0,

        /// <summary>A television series: a shelf of seasons, not something that plays on its own.</summary>
        Series = 1,

        /// <summary>
        /// One episode of a programme.
        /// </summary>
        /// <remarks>
        /// Only ever built for the Continue watching row, and never part of the library wall: the
        /// library lists programmes, and a shelf holding a thousand episodes beside four hundred
        /// films would be a different app. An episode card exists because the server says somebody
        /// is part way through that particular episode, which a series card cannot say.
        /// </remarks>
        Episode = 2
    }
}
