using System;
using System.Globalization;
using System.Xml.Linq;

namespace UrDatabase.Services
{
    /// <summary>What VLC says it is doing.</summary>
    public enum VlcPlaybackState
    {
        /// <summary>Something was said that this app has no meaning for.</summary>
        Unknown = 0,

        /// <summary>Opening, buffering — started, but not yet playing anything.</summary>
        Starting = 1,

        Playing = 2,
        Paused = 3,

        /// <summary>Nothing is loaded. Reached when the film ends and when the user stops it.</summary>
        Stopped = 4
    }

    /// <summary>
    /// One reading from VLC's HTTP control interface.
    /// </summary>
    /// <remarks>
    /// VLC 3.x answers <c>/requests/status.xml</c> with a document whose direct children include
    /// <c>time</c> and <c>length</c>, both in whole seconds, and <c>state</c>. That is all this app
    /// needs, and parsing it is pure, so the awkward readings are asserted rather than hoped for: a
    /// paused film, a film VLC has not opened yet and reports <c>-1</c> for, and a body that is not
    /// XML at all — which is what a wrong password produces, since the interface answers 401 with
    /// an HTML page.
    /// </remarks>
    public sealed class VlcStatus
    {
        /// <summary>Where playback is, in seconds. Never negative.</summary>
        public double PositionSeconds { get; init; }

        /// <summary>The film's length in seconds, or null when VLC has not worked it out yet.</summary>
        public double? LengthSeconds { get; init; }

        public VlcPlaybackState State { get; init; } = VlcPlaybackState.Unknown;

        /// <summary>True while a film is actually running: the only state that starts a report.</summary>
        public bool IsPlaying => State == VlcPlaybackState.Playing;

        public bool IsPaused => State == VlcPlaybackState.Paused;

        /// <summary>
        /// True when the player is holding a film — playing it or paused in it. A paused film is
        /// emphatically not an abandoned one; the difference is the whole reason
        /// <c>Sessions/Playing/Progress</c> carries an <c>IsPaused</c> flag.
        /// </summary>
        public bool HasFilm => State is VlcPlaybackState.Playing or VlcPlaybackState.Paused;

        public long PositionTicks => PlaybackPosition.SecondsToTicks(PositionSeconds);

        public long? RuntimeTicks =>
            LengthSeconds is double length && length > 0 ? PlaybackPosition.SecondsToTicks(length) : null;

        /// <summary>
        /// Reads one status document, or returns null when it cannot be read at all.
        /// </summary>
        /// <remarks>
        /// Null rather than an exception, because every caller of this treats an unreadable answer
        /// the same way as no answer: progress reporting is a bonus and must never be a reason the
        /// film stops playing or a dialog appears. A document that parses but names no state is
        /// still returned, as <see cref="VlcPlaybackState.Unknown"/> — that is a VLC that answered,
        /// which is a different fact from one that did not.
        /// </remarks>
        public static VlcStatus? Parse(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;

            XDocument document;
            try
            {
                document = XDocument.Parse(xml, LoadOptions.None);
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }

            var root = document.Root;
            if (root is null) return null;

            var length = ReadSeconds(root, "length");

            return new VlcStatus
            {
                // Only direct children are read. The document also carries an <information> tree of
                // stream metadata with names of its own, and a descendant search would eventually
                // pick up something from a file whose tags happened to be called the right thing.
                PositionSeconds = Math.Max(0, ReadSeconds(root, "time") ?? 0),
                LengthSeconds = length is > 0 ? length : null,
                State = ReadState(root.Element("state")?.Value)
            };
        }

        private static double? ReadSeconds(XElement root, string name)
        {
            var text = root.Element(name)?.Value;
            if (string.IsNullOrWhiteSpace(text)) return null;

            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        /// <summary>
        /// VLC's own vocabulary. Matched case-insensitively and by name rather than by position,
        /// because it has never promised these are stable and an unrecognised one has a safe
        /// meaning here anyway.
        /// </summary>
        internal static VlcPlaybackState ReadState(string? state) => (state ?? "").Trim().ToLowerInvariant() switch
        {
            "playing" => VlcPlaybackState.Playing,
            "paused" => VlcPlaybackState.Paused,
            "stopped" or "ended" => VlcPlaybackState.Stopped,
            "opening" or "buffering" => VlcPlaybackState.Starting,
            _ => VlcPlaybackState.Unknown
        };
    }
}
