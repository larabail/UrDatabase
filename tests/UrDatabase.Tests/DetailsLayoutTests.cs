using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class DetailsLayoutTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Exactly_one_row_takes_the_space_that_is_left(bool related, bool stills)
        {
            var layout = DetailsLayout.For(related, stills);

            Assert.Equal(1, new[] { layout.PlotFills, layout.StillsFill, layout.RelatedFills }.Count(f => f));
        }

        [Fact]
        public void With_neither_the_plot_has_the_column_as_it_did_before_either_existed()
        {
            var layout = DetailsLayout.For(hasRelated: false, hasStills: false);

            Assert.True(layout.PlotFills);
            Assert.Equal(double.PositiveInfinity, layout.PlotMaxHeight);
        }

        [Fact]
        public void A_shelf_keeps_its_space_and_the_stills_take_theirs_from_the_plot()
        {
            var shelf = DetailsLayout.For(hasRelated: true, hasStills: false);
            var both = DetailsLayout.For(hasRelated: true, hasStills: true);

            Assert.True(shelf.RelatedFills);
            Assert.True(both.RelatedFills);
            Assert.Equal(DetailsLayout.PlotCapWithShelf, shelf.PlotMaxHeight);
            Assert.True(both.PlotMaxHeight < shelf.PlotMaxHeight);
        }

        [Fact]
        public void Stills_alone_sit_directly_under_a_capped_plot()
        {
            var layout = DetailsLayout.For(hasRelated: false, hasStills: true);

            Assert.True(layout.StillsFill);
            Assert.False(double.IsInfinity(layout.PlotMaxHeight));
        }

        /// <summary>
        /// The code-behind addresses the plot, the stills and the shelf as rows 3, 4 and 5 of the
        /// column. Moving one in the markup without the other would put the stretch on the wrong
        /// row with nothing to say so but a strange-looking page.
        /// </summary>
        [Fact]
        public void The_markup_puts_the_plot_stills_and_shelf_in_the_rows_the_layout_sizes()
        {
            var document = XDocument.Load(Path.Combine(ShelfNavigationMarkupTests.AppSource(), "Views", "MovieDetailsView.axaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            string? Row(string name) => (string?)document.Descendants()
                .Single(e => (string?)e.Attribute(x + "Name") == name).Attribute("Grid.Row");

            Assert.Equal("3", Row("PlotPane"));
            Assert.Equal("4", Row("StillsPane"));
            Assert.Equal("5", Row("RelatedPane"));

            var plot = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "PlotPane");
            Assert.Same(plot.Parent, document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "StillsPane").Parent);
        }
    }
}
