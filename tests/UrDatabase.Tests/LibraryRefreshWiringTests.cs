using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace UrDatabase.Tests;

public class LibraryRefreshWiringTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Refresh_is_in_the_common_library_toolbar()
    {
        var doc = XDocument.Load(Path.Combine(Root(), "src/UrDatabase.App/Views/MainWindow.axaml"));
        var button = Assert.Single(doc.Descendants(), e => e.Name.LocalName == "Button"
            && (string?)e.Attribute("Click") == "RefreshButton_Click");
        Assert.Equal("Refresh", (string?)button.Attribute("Content"));
        Assert.Contains(button.Ancestors(), e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "LibraryRoot");
    }

    [Fact]
    public void Reloads_preserve_the_query_and_reapply_the_existing_filters()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "src/UrDatabase.App/Views/MainWindow.axaml.cs"));
        var apply = code[code.IndexOf("private void ApplyLibrary(", StringComparison.Ordinal)..];
        apply = apply[..apply.IndexOf("private void ShowSearchResults(", StringComparison.Ordinal)];
        Assert.Contains("ReapplyFilters();", apply);
        var sync = code[code.IndexOf("private async Task SyncJellyfinAsync(", StringComparison.Ordinal)..];
        sync = sync[..sync.IndexOf("private async Task LogConnectionDiagnosticAsync(", StringComparison.Ordinal)];
        Assert.Contains("_searchLoop.RefreshAsync(SearchBox.Text)", sync);
        Assert.Contains("if (found.ArtworkRepaired) RetryVisibleArtwork(movieId);", code);
    }
}
