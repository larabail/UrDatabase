using System;
using System.Globalization;

namespace UrDatabase.Services
{
    /// <summary>
    /// Playback positions, in the two units this app has to speak at once.
    /// </summary>
    /// <remarks>
    /// Jellyfin counts in ticks of 100 nanoseconds, and VLC's control interface answers in whole
    /// seconds. Every conversion between them lives here rather than being written out at each
    /// call site: a stray factor of a thousand in one of those would report a two hour film as
    /// seven seconds in, which the server would happily record and nothing would obviously look
    /// wrong until somebody tried to resume.
    ///
    /// Pure, and therefore asserted on at the boundaries — zero, a negative reading from a player
    /// that has not started, and a value large enough to overflow a naive multiplication.
    /// </remarks>
    public static class PlaybackPosition
    {
        /// <summary>100-nanosecond ticks in one second. Jellyfin's unit, and .NET's.</summary>
        public const long TicksPerSecond = TimeSpan.TicksPerSecond;

        /// <summary>
        /// A position below this is not a position. A player that has just been handed a URL
        /// reports zero for a moment, and reporting that as progress would tell the server
        /// somebody had started a film they had not.
        /// </summary>
        public const long MinimumMeaningfulTicks = TicksPerSecond;

        /// <summary>
        /// Seconds as the player reports them, in the server's ticks. Negative and non-finite
        /// readings become zero: VLC answers <c>-1</c> for a stream it has not opened yet, and a
        /// malformed document can produce a NaN, neither of which is a place in a film.
        /// </summary>
        public static long SecondsToTicks(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) return 0;

            // Clamped rather than allowed to wrap. A film cannot be longer than this and an
            // overflowing multiplication would arrive at the server as a negative position.
            var maxSeconds = long.MaxValue / (double)TicksPerSecond;
            if (seconds >= maxSeconds) return long.MaxValue;

            return (long)Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero);
        }

        /// <summary>Ticks back to whole seconds, for anything that has to be shown or logged.</summary>
        public static double TicksToSeconds(long ticks) => ticks <= 0 ? 0 : ticks / (double)TicksPerSecond;

        /// <summary>
        /// How far through the film a position is, between 0 and 1, or null when nothing here can
        /// say. A percentage the server worked out is preferred to dividing by a runtime: the
        /// server knows the length of the file it is serving, whereas a cached runtime can be
        /// absent, rounded to the minute, or describe a different cut of the film.
        /// </summary>
        public static double? Fraction(long positionTicks, long? runtimeTicks, double? playedPercentage)
        {
            if (playedPercentage is double percentage && !double.IsNaN(percentage) && percentage > 0)
                return Math.Clamp(percentage / 100d, 0d, 1d);

            if (positionTicks <= 0) return null;
            if (runtimeTicks is not > 0) return null;

            return Math.Clamp(positionTicks / (double)runtimeTicks.Value, 0d, 1d);
        }

        /// <summary>
        /// The line printed under a part-watched card: <c>"42 MIN LEFT"</c>.
        /// </summary>
        /// <remarks>
        /// Time remaining rather than time elapsed, because that is the question somebody scanning
        /// the row is actually asking — whether there is an evening's film left in it or ten
        /// minutes. It needs a runtime to be answerable at all; without one the card falls back to
        /// how far through it is, which the bar already shows but the text then says out loud.
        ///
        /// Rounded up, so a film with forty seconds left says "1 MIN LEFT" rather than "0 MIN
        /// LEFT", which reads as a finished film.
        /// </remarks>
        public static string? Describe(long positionTicks, long? runtimeTicks, double? playedPercentage)
        {
            if (positionTicks <= 0) return null;

            if (runtimeTicks is > 0 && runtimeTicks.Value > positionTicks)
            {
                var remaining = TimeSpan.FromTicks(runtimeTicks.Value - positionTicks);
                var minutes = (int)Math.Ceiling(remaining.TotalMinutes);

                if (minutes >= 60)
                {
                    var hours = minutes / 60;
                    var rest = minutes % 60;
                    return rest == 0
                        ? $"{hours.ToString(CultureInfo.InvariantCulture)} HR LEFT"
                        : $"{hours.ToString(CultureInfo.InvariantCulture)} HR {rest.ToString(CultureInfo.InvariantCulture)} MIN LEFT";
                }

                return $"{minutes.ToString(CultureInfo.InvariantCulture)} MIN LEFT";
            }

            var fraction = Fraction(positionTicks, runtimeTicks, playedPercentage);
            if (fraction is not double through) return null;

            var percent = (int)Math.Round(through * 100, MidpointRounding.AwayFromZero);
            return $"{Math.Clamp(percent, 1, 99).ToString(CultureInfo.InvariantCulture)}% IN";
        }
    }
}
