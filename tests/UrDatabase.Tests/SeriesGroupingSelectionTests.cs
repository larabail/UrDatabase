using System.ComponentModel;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public class SeriesGroupingSelectionTests
{
    [Fact]
    public void Editable_rows_notify_bindings_once_per_change()
    {
        var file = new SeriesGroupingFile();
        var observable = Assert.IsAssignableFrom<INotifyPropertyChanged>(file);
        var changed = new List<string?>();
        observable.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        file.IsSelected = true;
        file.IsSelected = true;
        file.SeasonText = "2";
        file.SeasonText = "2";
        file.EpisodeText = "42";
        file.EpisodeText = "42";

        Assert.Equal(new[] { "IsSelected", "SeasonText", "EpisodeText" }, changed);
    }

    [Fact]
    public void Selecting_and_deselecting_a_filtered_subset_keeps_hidden_selections_and_edits()
    {
        var files = new[]
        {
            new SeriesGroupingFile { Id = 1, SeriesTitle = "The Simpsons", SeasonText = "1", EpisodeText = "1" },
            new SeriesGroupingFile { Id = 2, MovieTitle = "The Simpsons pilot", EpisodeText = "2" },
            new SeriesGroupingFile { Id = 3, MovieTitle = "Other", IsSelected = true },
            new SeriesGroupingFile { Id = 4, FilePath = "/media/simpsons/episode.mkv" }
        };
        using var selection = new SeriesGroupingSelection(files);
        selection.Filter("  SIMPSONS ");
        Assert.Equal(new long[] { 1, 2, 4 }, selection.Results.Select(file => file.Id));
        Assert.False(selection.AllSelected);

        selection.SelectAllResults(true);
        Assert.All(files, file => Assert.True(file.IsSelected));
        Assert.True(selection.AllSelected);
        Assert.Equal(4, selection.SelectedCount);
        files[0].EpisodeText = "300";
        selection.SelectAllResults(false);
        Assert.True(files[2].IsSelected);
        Assert.Equal(1, selection.SelectedCount);
        Assert.False(selection.AllSelected);
        selection.Filter(null);

        Assert.Null(selection.AllSelected);
        Assert.Equal("1", files[0].SeasonText);
        Assert.Equal("300", files[0].EpisodeText);
        Assert.Equal(300, files[0].Assignment().Episode);
    }

    [Fact]
    public void Individual_edits_and_filter_changes_notify_the_aggregate_state()
    {
        var files = new[]
        {
            new SeriesGroupingFile { MovieTitle = "First" },
            new SeriesGroupingFile { MovieTitle = "Second" }
        };
        using var selection = new SeriesGroupingSelection(files);
        var changes = new List<string?>();
        selection.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.False(selection.AllSelected);

        files[0].IsSelected = true;
        Assert.Null(selection.AllSelected);
        Assert.Contains(nameof(selection.AllSelected), changes);
        Assert.Contains(nameof(selection.Summary), changes);
        files[1].IsSelected = true;
        Assert.True(selection.AllSelected);
        files[0].IsSelected = false;
        Assert.Null(selection.AllSelected);
        selection.Filter("first");
        Assert.False(selection.AllSelected);
        selection.Filter("second");
        Assert.True(selection.AllSelected);
        Assert.Contains(nameof(selection.Results), changes);
        Assert.Contains(nameof(selection.HasResults), changes);
    }

    [Fact]
    public void Empty_results_are_disabled_unchecked_and_do_not_change_hidden_selections()
    {
        var file = new SeriesGroupingFile { IsSelected = true };
        using var selection = new SeriesGroupingSelection(new[] { file });
        selection.Filter("no match");
        Assert.False(selection.HasResults);
        Assert.False(selection.AllSelected);
        Assert.Contains("No files match", selection.Summary);
        selection.SelectAllResults(false);
        Assert.True(file.IsSelected);
        selection.SelectAllResults(true);
        Assert.False(selection.AllSelected);
        Assert.Equal(1, selection.SelectedCount);

        using var empty = new SeriesGroupingSelection(Array.Empty<SeriesGroupingFile>());
        Assert.False(empty.HasResults);
        Assert.False(empty.AllSelected);
        Assert.Equal(0, empty.SelectedCount);
    }

    [Fact]
    public void A_large_result_set_changes_every_row_without_needing_realised_controls()
    {
        var files = Enumerable.Range(1, 300).Select(i => new SeriesGroupingFile
        {
            Id = i, SeriesTitle = "The Simpsons", EpisodeText = i.ToString()
        }).ToArray();
        using var selection = new SeriesGroupingSelection(files);
        var changed = new HashSet<long>();
        foreach (var file in files)
            file.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(file.IsSelected)) changed.Add(file.Id);
            };
        var aggregateChanges = 0;
        selection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(selection.AllSelected)) aggregateChanges++;
        };

        selection.SelectAllResults(true);
        Assert.Equal(300, selection.SelectedCount);
        Assert.Equal(300, changed.Count);
        Assert.Equal(1, aggregateChanges);
        selection.Filter("no match");
        selection.Filter("simpsons");
        Assert.All(selection.Results, file => Assert.True(file.IsSelected));
        Assert.True(selection.AllSelected);
        selection.SelectAllResults(false);
        Assert.Equal(0, selection.SelectedCount);
    }
}
