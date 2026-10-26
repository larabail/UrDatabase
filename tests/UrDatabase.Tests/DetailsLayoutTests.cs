using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class DetailsLayoutTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        /// <summary>
        /// The text column of a maximised 2560-pixel window: less the page's own margins (24 and
        /// 48), the poster column and its gutter (250 and 48), the room kept clear for the scroll
        /// bar (14), and the two 38-pixel arrow columns a shelf keeps either side of its row.
        /// </summary>
        internal const double WideTextColumn = 2560 - 24 - 48 - 250 - 48 - 14 - 2 * 38;

        /// <summary>How far apart the stills sit, read from the template so the two cannot drift.</summary>
        internal static double StillPitch => Pitch("models:FilmStill");

        /// <summary>How far apart the related posters sit, read from the template.</summary>
        internal static double PosterPitch => Pitch("models:UiMovie");

        [Fact]
        public void With_neither_the_plot_is_not_capped()
        {
            Assert.Equal(double.PositiveInfinity, DetailsLayout.PlotMaxHeight(hasRelated: false, hasStills: false));
        }

        [Fact]
        public void A_shelf_caps_the_plot_and_the_stills_take_their_room_from_it()
        {
            var shelf = DetailsLayout.PlotMaxHeight(hasRelated: true, hasStills: false);
            var both = DetailsLayout.PlotMaxHeight(hasRelated: true, hasStills: true);

            Assert.Equal(DetailsLayout.PlotCapWithShelf, shelf);
            Assert.True(both < shelf);
        }

        [Fact]
        public void Stills_alone_sit_directly_under_a_capped_plot()
        {
            Assert.False(double.IsInfinity(DetailsLayout.PlotMaxHeight(hasRelated: false, hasStills: true)));
        }

        /// <summary>
        /// A star row in the text column is the bug this replaced: on a tall window it took every
        /// spare pixel, centred the shelf in a band of empty backdrop and pinned the credits to the
        /// foot of the window. Every row is sized by its content, and the column scrolls instead.
        /// </summary>
        [Fact]
        public void No_row_of_the_text_column_stretches_to_fill_the_window()
        {
            var column = Named(Document(), "DetailsColumn");

            var rows = ((string?)column.Attribute("RowDefinitions") ?? "").Split(',');
            Assert.All(rows, row => Assert.Equal("Auto", row.Trim()));
            Assert.Equal("Top", (string?)column.Attribute("VerticalAlignment"));

            foreach (var name in new[] { "PlotPane", "StillsPane", "RelatedPane" })
                Assert.Null(Named(Document(), name).Attribute("VerticalAlignment"));
        }

        /// <summary>
        /// With nothing stretching, a short window needs somewhere for the rest of the column to
        /// go. It scrolls vertically and never horizontally: a horizontal scroller would measure
        /// the shelves against infinite width and they would stop filling the window.
        /// </summary>
        [Fact]
        public void The_text_column_scrolls_down_and_never_sideways()
        {
            var column = Named(Document(), "DetailsColumn");
            var scroller = column.Parent!;

            Assert.Equal("ScrollViewer", scroller.Name.LocalName);
            Assert.Equal("DetailsScroller", (string?)scroller.Attribute(X + "Name"));
            Assert.Equal("Disabled", (string?)scroller.Attribute("HorizontalScrollBarVisibility"));
            Assert.NotEqual("Disabled", (string?)scroller.Attribute("VerticalScrollBarVisibility"));
        }

        /// <summary>
        /// The stills and the shelf are spaced from what is above them alike, and from the
        /// credits, so the page reads as one list rather than as sections that drifted apart.
        /// </summary>
        [Fact]
        public void The_sections_under_the_plot_are_spaced_alike()
        {
            var document = Document();
            var credits = Named(document, "DetailsColumn").Elements()
                .Single(e => (string?)e.Attribute("Grid.Row") == "6");

            var tops = new[] { Named(document, "StillsPane"), Named(document, "RelatedPane"), credits }
                .Select(e => Top((string?)e.Attribute("Margin")))
                .Distinct()
                .ToList();

            Assert.Single(tops);
        }

        [Fact]
        public void The_markup_keeps_the_plot_stills_and_shelf_in_one_column_in_order()
        {
            var document = Document();
            string? Row(string name) => (string?)Named(document, name).Attribute("Grid.Row");

            Assert.Equal("3", Row("PlotPane"));
            Assert.Equal("4", Row("StillsPane"));
            Assert.Equal("5", Row("RelatedPane"));

            var column = Named(document, "DetailsColumn");
            Assert.Same(column, Named(document, "PlotPane").Parent);
            Assert.Same(column, Named(document, "StillsPane").Parent);
            Assert.Same(column, Named(document, "RelatedPane").Parent);
        }

        // ---- helpers -------------------------------------------------------------------------

        private static XDocument Document() =>
            XDocument.Load(Path.Combine(ShelfNavigationMarkupTests.AppSource(), "Views", "MovieDetailsView.axaml"));

        private static XElement Named(XDocument document, string name) =>
            document.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

        private static double Pitch(string dataType)
        {
            var template = Document().Descendants()
                .Single(e => e.Name.LocalName == "DataTemplate" && (string?)e.Attribute("DataType") == dataType);
            var card = template.Elements().First();

            var width = double.Parse((string)card.Attribute("Width")!, CultureInfo.InvariantCulture);
            var margin = Thickness((string?)card.Attribute("Margin"));
            return width + margin[0] + margin[2];
        }

        private static double Top(string? margin) => Thickness(margin)[1];

        private static double[] Thickness(string? value)
        {
            var parts = (value ?? "0")
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => double.Parse(p, CultureInfo.InvariantCulture))
                .ToArray();

            return parts.Length switch
            {
                1 => new[] { parts[0], parts[0], parts[0], parts[0] },
                2 => new[] { parts[0], parts[1], parts[0], parts[1] },
                _ => parts,
            };
        }
    }
}