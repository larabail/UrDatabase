using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace UrDatabase.Tests;

/// <summary>
/// Remove from library is code-behind, so what can be checked without a UI thread is that the
/// pieces are connected: the button exists and is wired, the screen asks before acting, and the
/// library behind it reloads afterwards rather than keeping a card for a row that is gone.
/// </summary>
public class RemoveFromLibraryWiringTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void The_button_sits_in_the_no_file_banner_and_starts_hidden()
    {
        var doc = XDocument.Load(Path.Combine(Root(), "src/UrDatabase.App/Views/MovieDetailsView.axaml"));
        var x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var button = Assert.Single(doc.Descendants(), e => e.Name.LocalName == "Button"
            && (string?)e.Attribute("Click") == "RemoveFromLibrary_Click");
        Assert.Equal("Remove from library", (string?)button.Attribute("Content"));

        var banner = Assert.Single(button.Ancestors(), e => (string?)e.Attribute(XName.Get("Name", x)) == "OrphanBanner");
        Assert.Equal("False", (string?)banner.Attribute("IsVisible"));
    }

    [Fact]
    public void Removing_asks_first_and_the_library_reloads_afterwards()
    {
        var view = File.ReadAllText(Path.Combine(Root(), "src/UrDatabase.App/Views/MovieDetailsView.axaml.cs")).Replace("\r\n", "\n");
        var click = view[view.IndexOf("private async void RemoveFromLibrary_Click(", StringComparison.Ordinal)..];
        click = click[..click.IndexOf("\n        }\n", StringComparison.Ordinal)];

        var ask = click.IndexOf("MessageBoxWindow.ConfirmAsync(", StringComparison.Ordinal);
        var remove = click.IndexOf("OrphanFilms.Remove(", StringComparison.Ordinal);
        Assert.True(ask >= 0 && remove > ask, "The film must be removed only after the question is answered.");
        Assert.Contains("DatabaseWriteLane.RunAsync(", click);
        Assert.Contains("RemovedSomething = true;", click);

        var main = File.ReadAllText(Path.Combine(Root(), "src/UrDatabase.App/Views/MainWindow.axaml.cs"));
        var details = main[main.IndexOf("private async Task ShowDetailsAsync(", StringComparison.Ordinal)..];
        details = details[..details.IndexOf("finally", StringComparison.Ordinal)];
        Assert.Contains("DetailsView.RemovedSomething", details);
        Assert.Contains("_searchLoop.RefreshAsync(SearchBox.Text)", details);
    }
}
