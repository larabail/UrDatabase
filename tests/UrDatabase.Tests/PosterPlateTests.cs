using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// What a card shows when it has no artwork.
    ///
    /// The rule that matters is the one that was wrong: a card given a perfectly good URL is not
    /// therefore a card with a poster on it. Television made that common rather than exotic —
    /// every programme in the library is a server item, so a library browsed away from its server
    /// is a wall of cards with no artwork at all.
    /// </summary>
    public class PosterPlateTests
    {
        [Fact]
        public void A_card_with_no_artwork_path_shows_the_plate()
        {
            Assert.True(PosterPlate.ShouldShow(null, hasArtwork: false));
            Assert.True(PosterPlate.ShouldShow("   ", hasArtwork: false));
        }

        [Fact]
        public void A_card_whose_artwork_failed_to_arrive_still_shows_the_plate()
        {
            // The bug. A poster deleted out of the cache, a URL that 404s, or a server that cannot
            // be reached all leave a card holding a path and no picture, and it used to render as
            // an empty rectangle carrying whatever text the plate happened to have.
            Assert.True(PosterPlate.ShouldShow("http://media.example/poster.jpg", hasArtwork: false));
        }

        [Fact]
        public void A_card_with_artwork_does_not_show_the_plate()
        {
            Assert.False(PosterPlate.ShouldShow("http://media.example/poster.jpg", hasArtwork: true));
        }

        [Fact]
        public void The_plate_says_what_the_thing_is_called()
        {
            Assert.Equal("A Wholly Invented Programme", PosterPlate.Caption("  A Wholly Invented Programme  "));
        }

        [Fact]
        public void A_nameless_card_says_so_rather_than_showing_nothing()
        {
            Assert.Equal("Untitled", PosterPlate.Caption(null));
            Assert.Equal("Untitled", PosterPlate.Caption("   "));
        }

        [Fact]
        public void A_year_is_printed_only_when_there_is_one()
        {
            Assert.Equal("1994", PosterPlate.YearLabel(1994));
            Assert.Equal("", PosterPlate.YearLabel(null));
        }
    }
}
