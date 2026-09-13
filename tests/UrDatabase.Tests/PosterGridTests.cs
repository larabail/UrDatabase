using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The arithmetic behind the grid views.
    ///
    /// It exists because Avalonia ships no virtualising wrap panel, so the wrapping the two grids
    /// used to get from <c>WrapPanel</c> is done here instead and handed to a panel that does
    /// virtualise. That is only safe if the rows this produces are the rows a wrap panel would
    /// have produced, which is what most of these check.
    /// </summary>
    public class PosterGridTests
    {
        private static UiMovie Film(string title) => new() { Title = title };

        private static UiMovie[] Films(int count) =>
            Enumerable.Range(1, count).Select(i => Film($"Film {i}")).ToArray();

        // ---------- how many fit ----------

        [Theory]
        [InlineData(1400, 8)]   // 1400 / 166 = 8.4
        [InlineData(996, 6)]    // exactly six
        [InlineData(1000, 6)]   // four pixels short of a seventh
        [InlineData(166, 1)]
        public void A_row_holds_as_many_cards_as_fit_across_it(double width, int expected) =>
            Assert.Equal(expected, PosterGrid.Columns(width));

        /// <summary>
        /// A control reports no width until it has been laid out, and a zero-column grid would put
        /// every film on a row of its own — the exact realisation storm the class exists to stop.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(-40)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void A_width_that_is_not_a_width_still_gives_a_usable_row(double width) =>
            Assert.Equal(1, PosterGrid.Columns(width));

        /// <summary>
        /// A card narrower than one stride still gets a column. Returning zero here divides a
        /// library into infinitely many rows.
        /// </summary>
        [Fact]
        public void A_window_too_narrow_for_a_card_still_shows_one_per_row() =>
            Assert.Equal(1, PosterGrid.Columns(20));

        // ---------- the rows ----------

        [Fact]
        public void Films_are_cut_into_rows_of_the_given_width()
        {
            var rows = PosterGrid.Chunk(Films(10), columns: 4);

            Assert.Equal(3, rows.Count);
            Assert.Equal(4, rows[0].Items.Count);
            Assert.Equal(4, rows[1].Items.Count);
            Assert.Equal(2, rows[2].Items.Count);
        }

        /// <summary>
        /// Nothing may be dropped or duplicated by the wrapping. A grid that loses the last two
        /// films of a genre is a catalogue that lies about what is in it.
        /// </summary>
        [Fact]
        public void Every_film_appears_exactly_once_and_in_the_order_given()
        {
            var films = Films(23);

            var flattened = PosterGrid.Chunk(films, columns: 5).SelectMany(r => r.Items).ToList();

            Assert.Equal(23, flattened.Count);
            Assert.Equal(films.Select(f => f.Title), flattened.Select(f => f.Title));
        }

        [Fact]
        public void An_exact_multiple_leaves_no_short_row()
        {
            var rows = PosterGrid.Chunk(Films(12), columns: 4);

            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal(4, r.Items.Count));
        }

        [Fact]
        public void An_empty_library_produces_no_rows()
        {
            Assert.Empty(PosterGrid.Chunk(System.Array.Empty<UiMovie>(), columns: 4));
            Assert.Empty(PosterGrid.Chunk(null, columns: 4));
        }

        /// <summary>
        /// A column count of zero or less would otherwise divide by nothing and never terminate.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public void A_nonsensical_column_count_still_terminates(int columns)
        {
            var rows = PosterGrid.Chunk(Films(5), columns);

            Assert.Equal(5, rows.Count);
            Assert.All(rows, r => Assert.Single(r.Items));
        }

        /// <summary>
        /// The size the bug report was about. Six thousand films must become a few hundred rows
        /// and not six thousand of them, because the row is what the virtualising panel counts.
        /// </summary>
        [Fact]
        public void A_library_of_six_thousand_becomes_rows_not_cards()
        {
            var rows = PosterGrid.Chunk(Films(6000), PosterGrid.Columns(1400));

            Assert.Equal(750, rows.Count);
            Assert.Equal(6000, rows.Sum(r => r.Items.Count));
        }
    }
}
