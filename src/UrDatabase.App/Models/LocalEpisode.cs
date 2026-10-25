namespace UrDatabase.Models
{
    /// <summary>
    /// One episode of a programme on this disk, as the catalogue holds it: where it sits in the
    /// run, what the filename called it, and which file plays it.
    /// </summary>
    /// <remarks>
    /// The local counterpart of <see cref="JellyfinEpisode"/>, and deliberately not the same type.
    /// That one is identified by a server's item id and played by streaming it; this one is
    /// identified by a row in <c>episodes</c> and played by opening a file, and a single type
    /// would carry a field that is meaningless for each half.
    /// </remarks>
    public sealed class LocalEpisode
    {
        public long Id { get; set; }
        public long SeriesId { get; set; }

        /// <summary>The season, when the path named one. Null for a numbering that has none.</summary>
        public int? SeasonNumber { get; set; }

        public int EpisodeNumber { get; set; }

        /// <summary>The episode's own title as the filename gave it. Often empty.</summary>
        public string? Title { get; set; }

        /// <summary>TMDB's name for the episode, once its season has been asked about. Preferred to <see cref="Title"/>.</summary>
        public string? TmdbTitle { get; set; }

        /// <summary>TMDB's plot for the episode. Null until its season has been asked about, and when TMDB has none.</summary>
        public string? Overview { get; set; }

        /// <summary>TMDB's length for the episode, in minutes.</summary>
        public int? RuntimeMinutes { get; set; }

        /// <summary>The day TMDB says it first aired, <c>yyyy-MM-dd</c>.</summary>
        public string? AirDate { get; set; }

        /// <summary>
        /// The best copy the last scan still found: largest first, as a film's prints are chosen.
        /// Null when every copy is one a scan could not find.
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>Any path the catalogue has for it, found or not. Named on a missing row.</summary>
        public string? AnyPath { get; set; }

        /// <summary>True when the episode has a file the last scan could still find.</summary>
        public bool IsAvailable => !string.IsNullOrWhiteSpace(FilePath);
    }
}
