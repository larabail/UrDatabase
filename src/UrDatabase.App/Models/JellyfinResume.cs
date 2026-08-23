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
    /// One entry of the Continue watching row: a film the server says is part-watched, and how far
    /// through it is.
    /// </summary>
    /// <remarks>
    /// Deliberately not a second copy of <see cref="JellyfinMovie"/>. The title, year and artwork
    /// are already cached with the library, and duplicating them here would give the row its own
    /// idea of what a film is called — so this is the position and nothing else, matched onto the
    /// card the library already built. A resume entry for something the movie library does not
    /// hold, such as a television episode, therefore has nowhere to land and is dropped, which is
    /// the behaviour an app that only understands films should have.
    /// </remarks>
    public sealed class JellyfinResumeItem
    {
        public string ItemId { get; set; } = "";

        /// <summary>Where playback stopped, in 100-nanosecond ticks.</summary>
        public long PositionTicks { get; set; }

        /// <summary>The film's length in the same units, when the server reported one.</summary>
        public long? RuntimeTicks { get; set; }

        /// <summary>The server's own percentage, when it reported one. Null is ordinary.</summary>
        public double? PlayedPercentage { get; set; }

        /// <summary>
        /// Where the server put this film in the row. Kept because the order is a real answer —
        /// most recently watched first — and re-sorting by title or year here would throw it away.
        /// </summary>
        public int SortOrder { get; set; }
    }
}
