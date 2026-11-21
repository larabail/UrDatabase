using System.Xml.Linq;
using Xunit;

namespace UrDatabase.Tests;

public class SeriesGroupingWiringTests
{
    [Fact]
    public void The_library_opens_the_grouping_picker_and_reloads_after_a_change()
    {
        var main = XDocument.Load(SourcePath("MainWindow.axaml"));
        Assert.Single(main.Descendants(), e => (string?)e.Attribute("Click") == "OrganiseSeries_Click");
        var source = File.ReadAllText(SourcePath("MainWindow.axaml.cs"));
        var start = source.IndexOf("private async void OrganiseSeries_Click(", StringComparison.Ordinal);
        var method = source[start..source.IndexOf("private void PersonBox_TextChanged(", start, StringComparison.Ordinal)];
        Assert.Contains("SeriesGroupingWindow.ShowAsync(this, _dbPath, _volumes)", method);
        Assert.Contains("is { } message", method);
        Assert.Contains("_searchLoop.RefreshAsync(SearchBox.Text)", method);
        Assert.Contains("SetStatus(message)", method);
        Assert.Contains("_scanning || _syncing", method);
    }

    [Fact]
    public void The_picker_binds_selection_and_both_numbers_and_confirms_before_writing()
    {
        var window = XDocument.Load(SourcePath("SeriesGroupingWindow.axaml"));
        Assert.Contains(window.Descendants(), e => (string?)e.Attribute("IsChecked") == "{Binding IsSelected, Mode=TwoWay}");
        Assert.Contains(window.Descendants(), e => (string?)e.Attribute("Text") == "{Binding SeasonText, Mode=TwoWay}");
        Assert.Contains(window.Descendants(), e => (string?)e.Attribute("Text") == "{Binding EpisodeText, Mode=TwoWay}");
        var source = File.ReadAllText(SourcePath("SeriesGroupingWindow.axaml.cs"));
        Assert.True(source.IndexOf("MessageBoxWindow.ConfirmAsync(", StringComparison.Ordinal)
            < source.IndexOf("LocalSeriesGrouping.Assign(", StringComparison.Ordinal));
        Assert.Contains("DatabaseWriteLane.RunAsync(", source);
        Assert.Contains("Task<string?> ShowAsync(Window owner, string dbPath, IVolumeSnapshotSource? volumes", source);
        Assert.Contains("LocalSeriesGrouping.Files(conn, snapshot)", source);
        Assert.Contains("_selection.SelectAllResults(_selection.AllSelected != true)", source);
    }

    [Fact]
    public void An_unknown_viewing_date_has_an_explicit_control_and_disables_the_picker()
    {
        var document = XDocument.Load(SourcePath("MovieDetailsView.axaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var unknown = Assert.Single(document.Descendants(), e => (string?)e.Attribute(xaml + "Name") == "UnknownViewingDate");
        Assert.Equal("CheckBox", unknown.Name.LocalName);
        var picker = Assert.Single(document.Descendants(), e => (string?)e.Attribute(xaml + "Name") == "WatchedOnPicker");
        Assert.Equal("{Binding !IsChecked, ElementName=UnknownViewingDate}", (string?)picker.Attribute("IsEnabled"));
    }

    private static string SourcePath(string file)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "UrDatabase.sln")))
            root = root.Parent;
        return Path.Combine(root?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "src/UrDatabase.App/Views", file);
    }
}
