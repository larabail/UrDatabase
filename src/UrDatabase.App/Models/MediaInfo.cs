using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace UrDatabase.Models
{
    /// <summary>
    /// What is known about the actual copy of a film — its picture size, its codecs, and which
    /// languages it can be heard and read in.
    /// </summary>
    /// <remarks>
    /// One shape for two very different sources, which is the point of it existing at all. A
    /// Jellyfin film comes with measured streams: real pixel dimensions, real language tags, a
    /// real channel count. A scanned file comes with nothing but its own name, and a name is a
    /// claim rather than a measurement — "1080p" in a filename is whatever the person who encoded
    /// it typed. Both end up here so the screen has one thing to render and one thing to test,
    /// and every field is optional because the honest answer for most of them, most of the time,
    /// is that nobody knows.
    ///
    /// Deliberately not a record: it is deserialised from the Jellyfin cache, and a mutable class
    /// with property initialisers is what <c>System.Text.Json</c> handles without ceremony.
    /// </remarks>
    public sealed class MediaInfo
    {
        /// <summary>Picture width in pixels, when it was measured rather than claimed.</summary>
        public int? Width { get; set; }

        public int? Height { get; set; }

        /// <summary>
        /// A resolution named rather than measured — read out of a filename, where it is the only
        /// thing on offer. Ignored whenever <see cref="Width"/> is known, because a measurement
        /// beats a claim.
        /// </summary>
        public string? ClaimedQuality { get; set; }

        /// <summary><c>hevc</c>, <c>h264</c>, <c>av1</c>… as the source spells it, not as it is shown.</summary>
        public string? VideoCodec { get; set; }

        /// <summary>
        /// The dynamic range, when the source says: <c>HDR10</c>, <c>DOVI</c>, <c>HLG</c>, <c>SDR</c>.
        /// Jellyfin reports this on the video stream; a filename claims it in a token.
        /// </summary>
        public string? VideoRange { get; set; }

        public string? AudioCodec { get; set; }

        /// <summary>Channel count of the default audio track — 2, 6, 8 — never a layout string.</summary>
        public int? AudioChannels { get; set; }

        /// <summary>True when the default track carries Dolby Atmos.</summary>
        public bool HasAtmos { get; set; }

        /// <summary>Where the copy came from: <c>BluRay</c>, <c>WEB-DL</c>, <c>Remux</c>. Filenames only.</summary>
        public string? Source { get; set; }

        /// <summary>Size of the file on disk, in bytes. Local films only; a server never reports one.</summary>
        public long? SizeBytes { get; set; }

        /// <summary>Container extension, lower case and without the dot — <c>mkv</c>, <c>mp4</c>.</summary>
        public string? Container { get; set; }

        /// <summary>Every language the film can be heard in, in the order the source listed them.</summary>
        public List<string> AudioLanguages { get; set; } = new();

        /// <summary>Every language it can be read in.</summary>
        public List<string> SubtitleLanguages { get; set; } = new();

        /// <summary>
        /// True when there is at least one thing here worth printing. An empty instance is what a
        /// film nobody has measured produces, and the screen shows nothing rather than a row of
        /// blanks.
        /// </summary>
        [JsonIgnore]
        public bool HasAnything =>
            Width > 0 ||
            Height > 0 ||
            !string.IsNullOrWhiteSpace(ClaimedQuality) ||
            !string.IsNullOrWhiteSpace(VideoCodec) ||
            !string.IsNullOrWhiteSpace(VideoRange) ||
            !string.IsNullOrWhiteSpace(AudioCodec) ||
            !string.IsNullOrWhiteSpace(Source) ||
            !string.IsNullOrWhiteSpace(Container) ||
            SizeBytes > 0 ||
            AudioChannels > 0 ||
            HasAtmos ||
            AudioLanguages.Any() ||
            SubtitleLanguages.Any();
    }
}
