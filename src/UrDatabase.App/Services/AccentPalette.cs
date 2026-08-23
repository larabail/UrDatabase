using System;
using System.Collections.Generic;
using System.Globalization;

namespace UrDatabase.Services
{
    /// <summary>
    /// The accent ramp, which is the one colour Fluent paints every selected, checked and
    /// focused control from.
    ///
    /// Avalonia's Fluent theme does not hold a separate colour for a checked toggle, a
    /// selected list row, a text selection or a focus ring. It holds seven — an accent and
    /// three lighter and three darker shades of it — and derives all of those states from
    /// them. Left alone they come from the operating system, which on macOS means #007AFF,
    /// so a window built entirely out of this app's own warm palette still lit up in system
    /// blue the moment anything was selected.
    ///
    /// So the seven are computed here from <c>BrassBrush</c> rather than typed out. A ramp
    /// written by hand is seven more colours that can drift away from the token they were
    /// meant to match, which is the drift <c>Tokens.axaml</c> exists to prevent; a ramp
    /// derived from the token cannot drift, and changing the accent stays a one-line change
    /// in one file.
    ///
    /// The shades move in lightness only. Hue and saturation are held exactly, because the
    /// whole point is that no state of any control can introduce a colour the palette did
    /// not choose.
    /// </summary>
    public static class AccentPalette
    {
        /// <summary>
        /// The resource keys Fluent reads, darkest first. These names are Avalonia 11's and
        /// are matched literally: a key that is spelled wrong is not an error, it simply
        /// never resolves, and the control keeps painting itself the system colour.
        /// </summary>
        public static readonly IReadOnlyList<string> Keys = new[]
        {
            "SystemAccentColorDark3",
            "SystemAccentColorDark2",
            "SystemAccentColorDark1",
            "SystemAccentColor",
            "SystemAccentColorLight1",
            "SystemAccentColorLight2",
            "SystemAccentColorLight3",
        };

        /// <summary>
        /// How far apart the shades sit, in HSL lightness.
        ///
        /// 0.05 rather than a larger step because the ramp's outer shades are pressed and
        /// hover states of the accent itself: they have to read as the same colour under a
        /// pointer, not as a different one. It is the gap that already separates
        /// <c>BrassBrush</c> from <c>BrassHoverBrush</c>, so a hover on an accent-filled
        /// control moves by the same amount as a hover anywhere else in the app.
        /// </summary>
        public const double LightnessStep = 0.05;

        /// <summary>The index of <c>SystemAccentColor</c> itself within <see cref="Keys"/>.</summary>
        private const int BaseIndex = 3;

        /// <summary>
        /// The seven accent resources for a base colour, in <see cref="Keys"/> order.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, string>> Ramp(string? baseHex)
        {
            var ramp = new KeyValuePair<string, string>[Keys.Count];

            for (var i = 0; i < Keys.Count; i++)
                ramp[i] = new KeyValuePair<string, string>(Keys[i], Shade(baseHex, i - BaseIndex));

            return ramp;
        }

        /// <summary>
        /// The base colour moved <paramref name="steps"/> stops along the ramp: negative is
        /// darker, positive is lighter, zero is the colour itself.
        /// </summary>
        /// <remarks>
        /// Step zero short-circuits rather than round-tripping through HSL. The conversion
        /// is lossy by up to one part in 255, and an accent that is one shade off the token
        /// it was derived from would be a genuinely nasty thing to have to spot by eye.
        /// </remarks>
        public static string Shade(string? baseHex, int steps)
        {
            var (r, g, b) = Parse(baseHex);
            if (steps == 0) return Hex(r, g, b);

            var (h, s, l) = ToHsl(r, g, b);

            return FromHsl(h, s, Math.Clamp(l + steps * LightnessStep, 0, 1));
        }

        /// <summary>
        /// WCAG 2.1 relative luminance, which is what a contrast ratio is built out of and
        /// is not the same thing as HSL lightness — #C8973F and #3F7AC8 have an identical
        /// lightness and nothing like the same luminance.
        /// </summary>
        public static double Luminance(string? hex)
        {
            var (r, g, b) = Parse(hex);
            return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);

            static double Linear(int channel)
            {
                var c = channel / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
        }

        /// <summary>
        /// The WCAG contrast ratio between two colours, from 1 (identical) to 21 (black on
        /// white). Order does not matter.
        /// </summary>
        public static double Contrast(string? first, string? second)
        {
            var a = Luminance(first);
            var b = Luminance(second);

            return a > b ? (a + 0.05) / (b + 0.05) : (b + 0.05) / (a + 0.05);
        }

        // ---- colour conversion -------------------------------------------------------------

        /// <summary>
        /// Accepts <c>#RRGGBB</c> and <c>#AARRGGBB</c>, with or without the hash. Alpha is
        /// read and discarded: every colour on this ramp is opaque, and a half-transparent
        /// accent would make each derived state depend on whatever happened to be behind it.
        /// </summary>
        private static (int R, int G, int B) Parse(string? hex)
        {
            var text = (hex ?? string.Empty).Trim().TrimStart('#');

            if (text.Length == 8) text = text.Substring(2);

            if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
                throw new FormatException($"'{hex}' is not a #RRGGBB or #AARRGGBB colour.");

            return ((packed >> 16) & 0xFF, (packed >> 8) & 0xFF, packed & 0xFF);
        }

        private static string Hex(int r, int g, int b)
            => string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");

        private static (double H, double S, double L) ToHsl(int red, int green, int blue)
        {
            var r = red / 255.0;
            var g = green / 255.0;
            var b = blue / 255.0;

            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var delta = max - min;
            var l = (max + min) / 2;

            if (delta <= 0) return (0, 0, l);

            var s = delta / (1 - Math.Abs(2 * l - 1));

            double h;
            if (max == r) h = 60 * (((g - b) / delta % 6 + 6) % 6);
            else if (max == g) h = 60 * ((b - r) / delta + 2);
            else h = 60 * ((r - g) / delta + 4);

            return (h, s, l);
        }

        private static string FromHsl(double h, double s, double l)
        {
            var c = (1 - Math.Abs(2 * l - 1)) * s;
            var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            var m = l - c / 2;

            var (r, g, b) = h switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x),
            };

            return Hex(Byte(r + m), Byte(g + m), Byte(b + m));

            static int Byte(double channel)
                => (int)Math.Round(Math.Clamp(channel, 0, 1) * 255, MidpointRounding.AwayFromZero);
        }
    }
}
