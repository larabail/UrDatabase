using System.Xml.Linq;
using Xunit;

namespace UrDatabase.Tests
{
    public class ShelfNavigationMarkupTests
    {
        [Theory]
        [InlineData("MainWindow.axaml", 2)]
        [InlineData("MovieDetailsView.axaml", 1)]
        [InlineData("SeriesDetailsView.axaml", 1)]
        public void Every_horizontal_row_has_mouse_navigation(string file, int count)
        {
            var document = XDocument.Load(Path.Combine(AppSource(), "Views", file));

            Assert.DoesNotContain(document.Descendants(), e =>
                e.Name.LocalName == "ScrollViewer"
                && ((string?)e.Attribute("Classes") ?? "").Split(' ').Contains("shelf"));
            Assert.Equal(count, document.Descendants()
                .Count(e => e.Name.LocalName == "ScrollableShelf"));
        }

        [Fact]
        public void Shelf_template_keeps_a_native_scroller_and_accessible_buttons_outside_its_viewport()
        {
            var theme = XDocument.Load(Path.Combine(AppSource(), "Styles", "Theme.axaml"));
            var shelf = Assert.Single(theme.Descendants(), e =>
                e.Name.LocalName == "ControlTheme"
                && (string?)e.Attribute("TargetType") == "ctrls:ScrollableShelf");
            var scroller = Assert.Single(shelf.Descendants(), e => e.Name.LocalName == "ScrollViewer");
            var buttons = shelf.Descendants().Where(e => e.Name.LocalName == "Button").ToArray();

            Assert.Equal("shelf", (string?)scroller.Attribute("Classes"));
            Assert.Equal("{TemplateBinding Content}", (string?)scroller.Attribute("Content"));
            Assert.Equal("{TemplateBinding ContentTemplate}", (string?)scroller.Attribute("ContentTemplate"));
            Assert.Equal("1", (string?)scroller.Attribute("Grid.Column"));
            Assert.Null(scroller.Attribute("PointerWheelChanged"));
            Assert.Equal(2, buttons.Length);
            Assert.All(buttons, button =>
            {
                Assert.Equal(scroller.Parent, button.Parent);
                Assert.NotEqual("1", (string?)button.Attribute("Grid.Column"));
                Assert.False(string.IsNullOrWhiteSpace((string?)button.Attribute("AutomationProperties.Name")));
                Assert.False(string.IsNullOrWhiteSpace((string?)button.Attribute("ToolTip.Tip")));
            });
        }

        internal static string AppSource()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "UrDatabase.sln")))
                    return Path.Combine(dir.FullName, "src", "UrDatabase.App");
            }

            throw new InvalidOperationException("Cannot find the UrDatabase source tree.");
        }
    }
}
