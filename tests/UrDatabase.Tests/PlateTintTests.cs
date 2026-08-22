using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Every poster card needs something behind the artwork: for the moment before the bitmap
    /// decodes, for a poster with transparency, and for a film whose poster has not been fetched
    /// yet. A single flat grey for all of them is what turned a freshly scanned library into a
    /// wall of identical holes, so the plate is tinted from the title instead.
    ///
    /// The property that matters is that it is the *same* colour every time. A plate that picks
    /// a new hue on each launch is worse than a grey one — it makes the library look like it is
    /// still loading long after it has finished.
    /// </summary>
    public class PlateTintTests
    {
        [Fact]
        public void The_same_title_is_always_the_same_colour()
        {
            Assert.Equal(PlateTint.HueFor("Stalker"), PlateTint.HueFor("Stalker"));
            Assert.Equal(PlateTint.TopColorFor("Stalker"), PlateTint.TopColorFor("Stalker"));
            Assert.Equal(PlateTint.BottomColorFor("Stalker"), PlateTint.BottomColorFor("Stalker"));
        }

        /// <summary>
        /// The reason FNV-1a is used rather than <see cref="string.GetHashCode()"/>: .NET
        /// randomises string hashing per process, so plates keyed on it would change colour on
        /// every launch. This pins the actual numbers, which is the only way that regression
        /// would ever be caught — it cannot reproduce inside a single test run.
        /// </summary>
        [Theory]
        [InlineData("Stalker", 163)]
        [InlineData("Heat", 171)]
        [InlineData("", 61)]
        public void The_hue_is_pinned_so_a_process_local_hash_cannot_creep_back_in(string title, int expected)
        {
            Assert.Equal(expected, PlateTint.HueFor(title));
        }

        [Fact]
        public void Case_and_surrounding_space_do_not_change_the_colour()
        {
            Assert.Equal(PlateTint.HueFor("Blade Runner"), PlateTint.HueFor("  blade runner  "));
        }

        [Fact]
        public void A_missing_title_still_produces_a_colour_rather_than_throwing()
        {
            Assert.InRange(PlateTint.HueFor(null), 0, 359);
            Assert.Equal(PlateTint.TopColorFor(null), PlateTint.TopColorFor(""));
        }

        [Fact]
        public void Different_titles_generally_get_different_hues()
        {
            var titles = new[] { "Heat", "Alien", "Stalker", "Amadeus", "Ran", "Solaris", "Brazil" };
            var hues = new System.Collections.Generic.HashSet<int>();

            foreach (var t in titles) hues.Add(PlateTint.HueFor(t));

            // Not a guarantee of no collisions — 360 hues and a hash will collide eventually —
            // but a spread this poor would mean the hash is not doing its job at all.
            Assert.True(hues.Count >= titles.Length - 1, $"only {hues.Count} distinct hues for {titles.Length} titles");
        }

        [Fact]
        public void Every_colour_is_a_parseable_six_digit_hex()
        {
            foreach (var title in new[] { "Heat", "Alien", "", "ラン" })
            {
                var top = PlateTint.TopColorFor(title);
                var bottom = PlateTint.BottomColorFor(title);

                Assert.Matches("^#[0-9A-F]{6}$", top);
                Assert.Matches("^#[0-9A-F]{6}$", bottom);
            }
        }

        /// <summary>
        /// The plate is a surround for artwork, not the artwork. A bright one would compete with
        /// the poster it is holding, and white title text on the pending plate would stop being
        /// legible.
        /// </summary>
        [Fact]
        public void The_plate_stays_dark_enough_to_sit_behind_artwork()
        {
            foreach (var title in new[] { "Heat", "Alien", "Stalker", "Amadeus", "Ran", "Solaris" })
            {
                AssertDark(PlateTint.TopColorFor(title));
                AssertDark(PlateTint.BottomColorFor(title));
            }

            static void AssertDark(string hex)
            {
                var r = System.Convert.ToInt32(hex.Substring(1, 2), 16);
                var g = System.Convert.ToInt32(hex.Substring(3, 2), 16);
                var b = System.Convert.ToInt32(hex.Substring(5, 2), 16);

                // Rec. 601 luma, which is close enough for "is this dark".
                var luma = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                Assert.True(luma < 0.34, $"{hex} has luma {luma:0.00} and is too bright for a plate");
            }
        }

        [Fact]
        public void The_bottom_of_the_gradient_is_darker_than_the_top()
        {
            foreach (var title in new[] { "Heat", "Alien", "Stalker", "Amadeus" })
            {
                var top = System.Convert.ToInt32(PlateTint.TopColorFor(title).Substring(1), 16);
                var bottom = System.Convert.ToInt32(PlateTint.BottomColorFor(title).Substring(1), 16);

                Assert.True(bottom < top, $"{title}: the gradient does not darken downwards");
            }
        }

        [Theory]
        [InlineData(0, 0.0, 0.0, "#000000")]
        [InlineData(0, 0.0, 1.0, "#FFFFFF")]
        [InlineData(0, 1.0, 0.5, "#FF0000")]
        [InlineData(120, 1.0, 0.5, "#00FF00")]
        [InlineData(240, 1.0, 0.5, "#0000FF")]
        public void The_HSL_conversion_agrees_with_the_known_corners(int h, double s, double l, string expected)
        {
            Assert.Equal(expected, PlateTint.ToHex(h, s, l));
        }

        [Fact]
        public void A_hue_outside_the_wheel_wraps_rather_than_clipping()
        {
            Assert.Equal(PlateTint.ToHex(0, 1.0, 0.5), PlateTint.ToHex(360, 1.0, 0.5));
            Assert.Equal(PlateTint.ToHex(350, 1.0, 0.5), PlateTint.ToHex(-10, 1.0, 0.5));
        }
    }
}
