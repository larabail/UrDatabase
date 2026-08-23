using System;
using System.Text.Json.Serialization;

namespace UrDatabase.Models
{
    /// <summary>
    /// What a Jellyfin user has done with one item: how far into it they are, and whether they
    /// have finished it.
    /// </summary>
    /// <remarks>
    /// Requested with every item this app fetches, not only the resume list, so a film's position
    /// is known wherever it is read from. Jellyfin omits the whole object for an item nobody has
    /// touched, which is why every field here is nullable rather than defaulted to zero.
    /// </remarks>
    public sealed class JellyfinUserDataDto
    {
        /// <summary>Where playback stopped, in 100-nanosecond ticks. Zero for a film never started.</summary>
        [JsonPropertyName("PlaybackPositionTicks")] public long? PlaybackPositionTicks { get; set; }

        /// <summary>
        /// How far through, as a percentage the server worked out itself. Preferred to dividing
        /// the position by the runtime because a server knows the real duration of the file it
        /// holds, while <c>RunTimeTicks</c> can be absent or describe a different cut.
        /// </summary>
        [JsonPropertyName("PlayedPercentage")] public double? PlayedPercentage { get; set; }

        /// <summary>True once the server considers the film watched.</summary>
        [JsonPropertyName("Played")] public bool? Played { get; set; }
    }

    /// <summary>
    /// One entry of the Continue watching row: something the server says is part-watched, and how
    /// far through it is.
    /// </summary>
    /// <remarks>
    /// A film carries its position and nothing else. The title, year and artwork are already
    /// cached in <c>jellyfin_movies</c>, and a second copy of them here would give the row its own
    /// idea of what a film is called.
    ///
    /// An episode is the exception, and has to be: nothing caches episodes until a series is
    /// opened, so an episode in this row has no first copy of its own name to be a second copy of.
    /// What it carries is therefore what a card has to say — the programme, the season, the number
    /// and the episode's own title — and nothing that could go stale independently, because the
    /// whole table is replaced by each sync and so is always exactly what the server last said.
    ///
    /// Its artwork is not among them. That comes from the series card the library already built,
    /// found by <see cref="SeriesId"/>, so an episode card is a poster like the film cards beside
    /// it rather than a 16:9 still in a row of 2:3 plates.
    /// </remarks>
    public sealed class JellyfinResumeItem
    {
        /// <summary>Jellyfin's own word for a film.</summary>
        public const string MovieType = "Movie";

        /// <summary>Jellyfin's own word for an episode.</summary>
        public const string EpisodeType = "Episode";

        public string ItemId { get; set; } = "";

        /// <summary>
        /// What the id refers to: <see cref="MovieType"/> or <see cref="EpisodeType"/>.
        /// </summary>
        /// <remarks>
        /// The row could not be mixed without it. A film id resolves through the cached movie
        /// library and an episode id never will, so a row that had to guess would either look up
        /// every id in both places or quietly render an episode as a film with an inexplicable
        /// name. Defaulted to <see cref="MovieType"/> so a row cached before television was in it
        /// reads back as what it was.
        /// </remarks>
        public string ItemType { get; set; } = MovieType;

        /// <summary>True when this entry is a television episode rather than a film.</summary>
        public bool IsEpisode =>
            string.Equals(ItemType?.Trim(), EpisodeType, StringComparison.OrdinalIgnoreCase);

        /// <summary>Where playback stopped, in 100-nanosecond ticks.</summary>
        public long PositionTicks { get; set; }

        /// <summary>The film's or episode's length in the same units, when the server reported one.</summary>
        public long? RuntimeTicks { get; set; }

        /// <summary>The server's own percentage, when it reported one. Null is ordinary.</summary>
        public double? PlayedPercentage { get; set; }

        /// <summary>
        /// Where the server put this entry in the row. Kept because the order is a real answer —
        /// most recently watched first — and re-sorting by title or year here would throw it away.
        /// </summary>
        public int SortOrder { get; set; }

        /// <summary>The programme an episode belongs to. Empty on a film.</summary>
        public string SeriesId { get; set; } = "";

        /// <summary>
        /// The programme's name, which is what an episode card is titled. Empty on a film.
        /// </summary>
        public string SeriesName { get; set; } = "";

        /// <summary>Which season an episode is in, when the server numbered it. Null on a film.</summary>
        public int? SeasonNumber { get; set; }

        /// <summary>Its number within that season, on the same terms.</summary>
        public int? EpisodeNumber { get; set; }

        /// <summary>
        /// The episode's own title, which is secondary on the card and often useless alone — a
        /// real one from this library is "In throes of increasing wonder … ". Empty on a film,
        /// whose title is cached with the library.
        /// </summary>
        public string Name { get; set; } = "";
    }
}
