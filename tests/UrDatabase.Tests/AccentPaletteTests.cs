using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Avalonia's Fluent theme derives every selected, checked and focused state from a
    /// single accent taken from the operating system, so an app that never asks for blue
    /// anywhere still painted a checked genre chip, a text selection and a focus ring in
    /// macOS's #007AFF. The fix is to compute the accent from the palette instead, and this
    /// is the part of it that can be asserted rather than looked at.
    ///
    /// A colour in XAML cannot be tested, but the ramp behind it can, and so can the promise
    /// the palette makes about contrast — which is the promise the accent has to keep too,
    /// since it is now the colour behind a tick, a selection and a focus ring.
    /// </summary>
    public class AccentPaletteTests
    {
        /// <summary>
        /// BrassBrush, as declared in Tokens.axaml. Passed in rather than read out of the
        /// file so these stay tests of the ramp; that the file still says this is asserted
        /// separately, in <see cref="The_palette_still_declares_the_colours_these_tests_assume"/>.
        /// </summary>
        private const string Brass = "#C8973F";
        private const string Room = "#0B0A09";
        private const string Bone = "#F1ECE3";

        // ---- the regression -----------------------------------------------------------------

        /// <summary>
        /// The whole point. Every shade Fluent can reach for has to be warm, because any one
        /// of them that is not is a control that goes blue in some state nobody clicked
        /// during review.
        /// </summary>
        [Fact]
        public void No_shade_of_the_accent_is_blue()
        {
            foreach (var (key, hex) in AccentPalette.Ramp(Brass))
            {
                var (r, _, b) = Channels(hex);
                Assert.True(r > b, $"{key} is {hex}, which is not a warm colour");
            }
        }

        [Fact]
        public void The_ramp_covers_exactly_the_seven_resources_fluent_reads()
        {
            var keys = AccentPalette.Ramp(Brass).Select(shade => shade.Key).ToArray();

            Assert.Equal(new[]
            {
                "SystemAccentColorDark3",
                "SystemAccentColorDark2",
                "SystemAccentColorDark1",
                "SystemAccentColor",
                "SystemAccentColorLight1",
                "SystemAccentColorLight2",
                "SystemAccentColorLight3",
            }, keys);
        }

        /// <summary>
        /// The accent itself must be the token exactly, not a value that has been through a
        /// lossy conversion and come back a shade out. Nobody would ever catch that by eye.
        /// </summary>
        [Fact]
        public void The_middle_of_the_ramp_is_the_token_it_was_derived_from()
        {
            Assert.Equal(Brass, AccentPalette.Shade(Brass, 0));
            Assert.Equal(Brass, Find(AccentPalette.Ramp(Brass), "SystemAccentColor"));
        }

        // ---- the shape of the ramp ----------------------------------------------------------

        [Fact]
        public void The_ramp_runs_from_darkest_to_lightest_without_a_step_backwards()
        {
            var shades = AccentPalette.Ramp(Brass).ToArray();

            for (var i = 1; i < shades.Length; i++)
            {
                Assert.True(
                    AccentPalette.Luminance(shades[i].Value) > AccentPalette.Luminance(shades[i - 1].Value),
                    $"{shades[i].Key} ({shades[i].Value}) is not lighter than {shades[i - 1].Key} ({shades[i - 1].Value})");
            }
        }

        /// <summary>
        /// Lightness is the only thing allowed to move. If hue drifted, a pressed accent
        /// button would be a slightly different colour from the accent, which is the drift
        /// the single-token palette exists to prevent.
        /// </summary>
        [Fact]
        public void Every_shade_keeps_the_hue_of_the_accent()
        {
            var expected = Hue(Brass);

            foreach (var (key, hex) in AccentPalette.Ramp(Brass))
                Assert.True(Math.Abs(Hue(hex) - expected) < 1.5, $"{key} is {hex}, a hue of {Hue(hex):0.0} rather than {expected:0.0}");
        }

        [Fact]
        public void A_step_either_way_lands_on_the_shade_next_to_it()
        {
            Assert.Equal(AccentPalette.Shade(Brass, 1), Find(AccentPalette.Ramp(Brass), "SystemAccentColorLight1"));
            Assert.Equal(AccentPalette.Shade(Brass, -1), Find(AccentPalette.Ramp(Brass), "SystemAccentColorDark1"));
        }

        [Fact]
        public void Stepping_off_the_end_of_the_scale_clamps_rather_than_wrapping()
        {
            Assert.Equal("#FFFFFF", AccentPalette.Shade(Brass, 40));
            Assert.Equal("#000000", AccentPalette.Shade(Brass, -40));
        }

        [Theory]
        [InlineData("#C8973F")]
        [InlineData("C8973F")]
        [InlineData("#FFC8973F")]
        [InlineData("  #c8973f  ")]
        public void The_colour_can_be_written_the_ways_avalonia_writes_it(string written)
        {
            Assert.Equal(Brass, AccentPalette.Shade(written, 0));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("brass")]
        [InlineData("#12345")]
        public void A_colour_that_is_not_a_colour_is_refused_rather_than_guessed(string? written)
        {
            Assert.Throws<FormatException>(() => AccentPalette.Shade(written, 0));
        }

        // ---- contrast -----------------------------------------------------------------------

        [Fact]
        public void The_contrast_maths_agrees_with_the_corners_of_the_scale()
        {
            Assert.Equal(21.0, AccentPalette.Contrast("#000000", "#FFFFFF"), 2);
            Assert.Equal(1.0, AccentPalette.Contrast(Brass, Brass), 2);
            Assert.Equal(
                AccentPalette.Contrast(Bone, Room),
                AccentPalette.Contrast(Room, Bone),
                6);
        }

        /// <summary>
        /// The accent is now what marks a focused control and fills a checked one, so every
        /// shade has to stay visible against the room it sits in. 3:1 is the floor for
        /// something that is not text — a ring, a fill, a track.
        /// </summary>
        [Fact]
        public void Every_shade_stays_visible_against_the_darkest_surface()
        {
            foreach (var (key, hex) in AccentPalette.Ramp(Brass))
            {
                var ratio = AccentPalette.Contrast(hex, Room);
                Assert.True(ratio >= 3.0, $"{key} ({hex}) is only {ratio:0.00}:1 against the room and would disappear");
            }
        }

        /// <summary>
        /// What goes on top of an accent fill: the tick in a checked box, a label on an
        /// accent button. Dark ink, because of the test below.
        /// </summary>
        [Fact]
        public void Dark_ink_is_legible_on_the_accent_and_on_every_shade_above_it()
        {
            foreach (var (key, hex) in AccentPalette.Ramp(Brass).Skip(3))
            {
                var ratio = AccentPalette.Contrast(Room, hex);
                Assert.True(ratio >= 4.5, $"dark ink on {key} ({hex}) is only {ratio:0.00}:1");
            }
        }

        /// <summary>
        /// The reason the CheckBox glyph, the accent button's label and selected text are all
        /// re-inked rather than left as Fluent had them. Fluent puts white on its accent,
        /// which is fine on #007AFF and is not fine on brass. Pinned so that a later change
        /// putting light ink back on the accent has to argue with a failing test.
        /// </summary>
        [Fact]
        public void Light_ink_on_the_accent_is_the_thing_that_does_not_work()
        {
            Assert.True(AccentPalette.Contrast(Bone, Brass) < 4.5);
            Assert.True(AccentPalette.Contrast("#FFFFFF", Brass) < 4.5);
        }

        // ---- the palette this all rests on --------------------------------------------------

        /// <summary>
        /// Tokens.axaml documents a contrast figure for each of its inks against the room,
        /// and those figures are the argument for the palette being usable at all. They were
        /// computed once, by hand, and written into a comment. This recomputes them.
        ///
        /// It reads the shipped file rather than a copy, so editing a token to a colour that
        /// no longer holds its ratio fails here instead of shipping.
        /// </summary>
        [Theory]
        [InlineData("BoneBrush", 16.8)]
        [InlineData("ProseBrush", 11.1)]
        [InlineData("BrassBrush", 7.5)]
        [InlineData("AwayBrush", 6.6)]
        [InlineData("DimBrush", 6.0)]
        [InlineData("EdgeBrush", 1.6)]
        public void The_contrast_table_in_the_palette_is_true(string key, double documented)
        {
            var tokens = Tokens();
            var actual = AccentPalette.Contrast(tokens[key], tokens["RoomBrush"]);

            Assert.True(
                Math.Abs(actual - documented) < 0.05,
                $"Tokens.axaml says {key} is {documented:0.0}:1 against RoomBrush; it is {actual:0.00}:1");
        }

        /// <summary>
        /// The accent is read out of the palette at startup rather than compiled in, and
        /// nothing falls back to a hardcoded blue if it is missing — a missing token means
        /// Fluent keeps the system accent and the blue quietly returns. This is what stops
        /// that happening silently.
        /// </summary>
        [Fact]
        public void The_palette_still_declares_the_colours_these_tests_assume()
        {
            var tokens = Tokens();

            Assert.Equal(Brass, tokens[UrDatabase.App.AccentBaseKey]);
            Assert.Equal(Brass, tokens["BrassBrush"]);
            Assert.Equal(Room, tokens["RoomBrush"]);
            Assert.Equal(Bone, tokens["BoneBrush"]);
        }

        /// <summary>
        /// Fluent paints these itself, from resources that are not derived from the accent,
        /// so overriding the accent alone would have left white ink on a brass fill at
        /// 2.2:1. Named literally because Avalonia resolves a resource key at runtime and a
        /// misspelling is not an error — it is a control that silently keeps Fluent's colour.
        /// </summary>
        [Theory]
        [InlineData("CheckBoxCheckGlyphForegroundChecked")]
        [InlineData("CheckBoxCheckGlyphForegroundCheckedPointerOver")]
        [InlineData("CheckBoxCheckGlyphForegroundCheckedPressed")]
        [InlineData("AccentButtonForeground")]
        [InlineData("ToggleButtonForegroundChecked")]
        [InlineData("ToggleSwitchKnobFillOn")]
        public void Anything_fluent_puts_on_an_accent_fill_is_overridden_to_dark_ink(string key)
        {
            var tokens = Tokens();

            Assert.True(tokens.ContainsKey(key), $"Tokens.axaml no longer overrides {key}, so Fluent's white ink is back");
            Assert.Equal(Room, tokens[key]);
        }

        /// <summary>
        /// Restyled, never removed. A focus ring is how the app is usable without a mouse,
        /// and the failure mode of "fix the blue" is deleting the indicator instead of
        /// recolouring it.
        /// </summary>
        [Fact]
        public void The_focus_ring_is_brass_and_still_stands_out()
        {
            var tokens = Tokens();

            Assert.Equal(Brass, tokens["SystemControlFocusVisualPrimaryBrush"]);
            Assert.True(AccentPalette.Contrast(tokens["SystemControlFocusVisualPrimaryBrush"], Room) >= 3.0);
        }

        // ---- helpers ------------------------------------------------------------------------

        private static string Find(IEnumerable<KeyValuePair<string, string>> ramp, string key)
            => ramp.Single(shade => shade.Key == key).Value;

        private static (int R, int G, int B) Channels(string hex)
            => (Convert.ToInt32(hex.Substring(1, 2), 16),
                Convert.ToInt32(hex.Substring(3, 2), 16),
                Convert.ToInt32(hex.Substring(5, 2), 16));

        private static double Hue(string hex)
        {
            var (r, g, b) = Channels(hex);
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));

            if (max == min) return 0;

            double delta = max - min;

            if (max == r) return 60 * ((((g - b) / delta) % 6 + 6) % 6);
            if (max == g) return 60 * ((b - r) / delta + 2);
            return 60 * ((r - g) / delta + 4);
        }

        /// <summary>
        /// Every <c>x:Key</c> in Tokens.axaml that names a flat colour, as <c>#RRGGBB</c>.
        /// Both spellings are collected: a <c>Color</c> holding a literal, and a
        /// <c>SolidColorBrush</c> holding either a literal or a reference to one of those
        /// colours.
        /// </summary>
        private static Dictionary<string, string> Tokens()
        {
            var text = File.ReadAllText(Path.Combine(
                RepositoryRoot(), "src", "UrDatabase.App", "Styles", "Tokens.axaml"));

            var colors = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (Match match in Regex.Matches(text, @"<Color\s+x:Key=""(?<key>[^""]+)""\s*>\s*(?<hex>#[0-9A-Fa-f]{6})\s*</Color>"))
                colors[match.Groups["key"].Value] = Normalise(match.Groups["hex"].Value);

            foreach (Match match in Regex.Matches(text, @"<SolidColorBrush\s+x:Key=""(?<key>[^""]+)""\s+Color=""(?<value>[^""]+)""\s*/>"))
            {
                var value = match.Groups["value"].Value.Trim();
                var reference = Regex.Match(value, @"^\{StaticResource\s+(?<ref>[^}]+)\}$");

                if (reference.Success)
                {
                    if (colors.TryGetValue(reference.Groups["ref"].Value.Trim(), out var resolved))
                        colors[match.Groups["key"].Value] = resolved;
                }
                else if (Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$"))
                {
                    colors[match.Groups["key"].Value] = Normalise(value);
                }
            }

            Assert.True(colors.Count > 0, "no colours were read out of Tokens.axaml, so this test proved nothing");

            return colors;
        }

        private static string Normalise(string hex) => hex.ToUpperInvariant();

        /// <summary>
        /// Walks up from the test assembly looking for the solution file. The tests run out
        /// of <c>tests/UrDatabase.Tests/bin/…</c>, so the repository is always above them.
        /// </summary>
        private static string RepositoryRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "UrDatabase.sln"))) return dir.FullName;
            }

            throw new InvalidOperationException(
                $"No UrDatabase.sln above {AppContext.BaseDirectory}, so the palette could not be checked.");
        }
    }
}
