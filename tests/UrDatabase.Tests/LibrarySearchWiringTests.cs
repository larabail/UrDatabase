using System.Xml.Linq;
using Xunit;

namespace UrDatabase.Tests;

public class LibrarySearchWiringTests
{
    [Fact]
    public void Title_and_person_search_share_a_visible_row_and_keep_the_existing_handlers()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "UrDatabase.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var document = XDocument.Load(Path.Combine(root.FullName, "src/UrDatabase.App/Views/MainWindow.axaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var row = Assert.Single(document.Descendants(),
            e => (string?)e.Attribute(xaml + "Name") == "LibrarySearchFields");
        var title = Assert.Single(row.Descendants(), e => (string?)e.Attribute(xaml + "Name") == "SearchBox");
        var person = Assert.Single(row.Descendants(), e => (string?)e.Attribute(xaml + "Name") == "PersonBox");
        Assert.Equal("SearchBox_TextChanged", (string?)title.Attribute("TextChanged"));
        Assert.Equal("PersonBox_TextChanged", (string?)person.Attribute("TextChanged"));
        Assert.Equal("Actor or director (optional)", (string?)person.Attribute("Watermark"));
        Assert.Single(document.Descendants(), e => (string?)e.Attribute(xaml + "Name") == "PersonBox");
    }
}
