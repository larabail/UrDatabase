namespace UrDatabase.Models
{
    /// <summary>
    /// What one TMDB lookup filled in for a film.
    /// </summary>
    /// <remarks>
    /// A record rather than the bare poster path this used to be, because the lookup now answers
    /// two questions and they arrive together. The search TMDB is asked in order to find artwork
    /// already carries the film's genres, so reporting only the path threw half the answer away —
    /// and it was the half that decided whether the library could be grouped at all.
    ///
    /// Either half may be null: TMDB has films it holds no poster for, and films it files under
    /// nothing. Null means "nothing to record" rather than "record nothing", and a caller writing
    /// these to the catalogue leaves the corresponding column alone.
    /// </remarks>
    public readonly record struct Enrichment(string? PosterPath, string? Genres)
    {
        /// <summary>Nothing found, which is what a refused match reports.</summary>
        public static Enrichment None => new(null, null);

        /// <summary>Whether there is anything here worth putting on a card.</summary>
        public bool HasPoster => !string.IsNullOrWhiteSpace(PosterPath);

        /// <summary>The file was restored at the same path, so string bindings will not reload it.</summary>
        public bool ArtworkRepaired { get; init; }
    }
}
