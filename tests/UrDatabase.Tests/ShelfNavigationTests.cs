using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class ShelfNavigationTests
    {
        [Theory]
        [InlineData(0, 2000, 500, 0, 400, false, true)]
        [InlineData(400, 2000, 500, 0, 800, true, true)]
        [InlineData(1400, 2000, 500, 1000, 1500, true, true)]
        [InlineData(1500, 2000, 500, 1100, 1500, true, false)]
        [InlineData(0, 300, 500, 0, 0, false, false)]
        [InlineData(0, 500, 500, 0, 0, false, false)]
        [InlineData(0, 0, 500, 0, 0, false, false)]
        [InlineData(0, 2000, 0, 0, 0, false, false)]
        [InlineData(0, 501, 500, 0, 1, false, true)]
        [InlineData(1, 501, 500, 0, 1, true, false)]
        [InlineData(1500, 600, 500, 0, 100, true, false)]
        [InlineData(1500, 2000, 2500, 0, 0, false, false)]
        public void Navigation_tracks_content_viewport_and_current_position(
            double offset, double extent, double viewport,
            double left, double right, bool canLeft, bool canRight)
        {
            var navigation = ShelfNavigation.Calculate(offset, extent, viewport);

            Assert.Equal(left, navigation.LeftOffset);
            Assert.Equal(right, navigation.RightOffset);
            Assert.Equal(canLeft, navigation.CanScrollLeft);
            Assert.Equal(canRight, navigation.CanScrollRight);
        }

        [Fact]
        public void Repeated_clicks_reach_both_ends_of_a_six_thousand_film_shelf()
        {
            const double extent = 6000 * PosterGrid.CardStride;
            const double viewport = 987;
            var offset = 0d;
            var clicks = 0;

            while (ShelfNavigation.Calculate(offset, extent, viewport) is { CanScrollRight: true } next)
            {
                Assert.InRange(next.RightOffset - offset, double.Epsilon, viewport);
                offset = next.RightOffset;
                Assert.True(++clicks < 2000);
            }

            Assert.Equal(extent - viewport, offset);
            clicks = 0;

            while (ShelfNavigation.Calculate(offset, extent, viewport) is { CanScrollLeft: true } next)
            {
                Assert.InRange(offset - next.LeftOffset, double.Epsilon, viewport);
                offset = next.LeftOffset;
                Assert.True(++clicks < 2000);
            }

            Assert.Equal(0, offset);
        }

        [Theory]
        [InlineData(double.NaN, 500, 200)]
        [InlineData(double.PositiveInfinity, 500, 200)]
        [InlineData(0, double.PositiveInfinity, 200)]
        [InlineData(0, double.NaN, 200)]
        [InlineData(0, -1, 200)]
        [InlineData(0, 500, double.PositiveInfinity)]
        [InlineData(0, 500, double.NaN)]
        [InlineData(0, 500, -1)]
        public void Invalid_geometry_is_reported_instead_of_producing_an_unusable_offset(
            double offset, double extent, double viewport) =>
            Assert.Throws<ArgumentOutOfRangeException>(() => ShelfNavigation.Calculate(offset, extent, viewport));
    }
}
