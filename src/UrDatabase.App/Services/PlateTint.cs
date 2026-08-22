using System;
using System.Globalization;

namespace UrDatabase.Services
{
    /// <summary>
    /// The colour of the plate a poster sits on.
    ///
    /// Every card needs something behind the artwork: for the moment before the bitmap
    /// decodes, for a poster with transparency, and for the letterboxing when a poster is
    /// not quite 2:3. A single flat grey for all of them turns a wall of loading cards into
    /// a wall of identical holes, which is the state a freshly scanned library spends its
    /// first minute in.
    ///
    /// So the plate is tinted from the title, deterministically. The same film is the same
    /// colour on every launch and on both platforms — no persistence, no randomness, and no
    /// second thing to keep in step with the database.
    ///
    /// Hues are kept dark and desaturated on purpose. This is the surround for artwork, and
    /// a saturated plate would compete with the poster it is holding.
    /// </summary>
    public static class PlateTint
    {
        // Deliberately narrow: dark enough that white title text over a bare plate stays
        // legible, muted enough never to read as the poster itself.
        private const double TopSaturation = 0.26;
        private const double TopLightness = 0.22;
        private const double BottomSaturation = 0.30;
        private const double BottomLightness = 0.11;

        /// <summary>
        /// The hue, in degrees, for a title. Stable across runs, processes and platforms.
        /// </summary>
        /// <remarks>
        /// <see cref="string.GetHashCode()"/> is deliberately not used: .NET randomises it
        /// per process, so the plates would change colour on every launch. This is FNV-1a,
        /// which is small, has no dependencies and is entirely reproducible.
        /// </remarks>
        public static int HueFor(string? title)
        {
            var text = (title ?? string.Empty).Trim().ToLowerInvariant();

            unchecked
            {
                const uint offset = 2166136261;
                const uint prime = 16777619;

                var hash = offset;
                foreach (var c in text)
                {
                    hash ^= c;
                    hash *= prime;
                }

                return (int)(hash % 360);
            }
        }

        /// <summary>The lighter, upper colour of the plate gradient, as <c>#RRGGBB</c>.</summary>
        public static string TopColorFor(string? title)
            => ToHex(HueFor(title), TopSaturation, TopLightness);

        /// <summary>
        /// The darker, lower colour. Offset around the wheel rather than merely darkened, so
        /// the gradient has somewhere to travel and the plate does not read as a flat block.
        /// </summary>
        public static string BottomColorFor(string? title)
            => ToHex((HueFor(title) + 24) % 360, BottomSaturation, BottomLightness);

        /// <summary>
        /// HSL to <c>#RRGGBB</c>. Hue in degrees, saturation and lightness from 0 to 1.
        /// </summary>
        internal static string ToHex(int hueDegrees, double saturation, double lightness)
        {
            var h = ((hueDegrees % 360) + 360) % 360 / 360.0;

            double r, g, b;

            if (saturation <= 0)
            {
                r = g = b = lightness;
            }
            else
            {
                var q = lightness < 0.5
                    ? lightness * (1 + saturation)
                    : lightness + saturation - lightness * saturation;
                var p = 2 * lightness - q;

                r = HueToChannel(p, q, h + 1.0 / 3.0);
                g = HueToChannel(p, q, h);
                b = HueToChannel(p, q, h - 1.0 / 3.0);
            }

            return string.Create(CultureInfo.InvariantCulture,
                $"#{ToByte(r):X2}{ToByte(g):X2}{ToByte(b):X2}");
        }

        private static double HueToChannel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;

            if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
            return p;
        }

        private static int ToByte(double channel)
            => (int)Math.Round(Math.Clamp(channel, 0, 1) * 255, MidpointRounding.AwayFromZero);
    }
}
