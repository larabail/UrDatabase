using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UrDatabase.Services;

namespace UrDatabase.Views;

public partial class SeriesGroupingWindow : Window
{
    private readonly string _dbPath = "";
    private readonly IReadOnlyList<SeriesGroupingFile> _files = Array.Empty<SeriesGroupingFile>();
    private bool _saving;

    public SeriesGroupingWindow()
    {
        InitializeComponent();
        Closing += (_, e) => e.Cancel = _saving;
    }

    private SeriesGroupingWindow(string dbPath, IReadOnlyList<SeriesGroupingFile> files,
        IReadOnlyList<LocalSeriesTarget> targets) : this()
    {
        _dbPath = dbPath;
        _files = files;
        TargetBox.ItemsSource = new[] { new LocalSeriesTarget { Title = "Create a new series" } }.Concat(targets);
        TargetBox.SelectedIndex = 0;
        FilesList.ItemsSource = files;
        SaveButton.IsEnabled = files.Count > 0;
        StatusText.Text = files.Count == 0 ? "No local files are catalogued. Scan your folders first." : "";
    }

    public static async Task<bool> ShowAsync(Window owner, string dbPath, IVolumeSnapshotSource? volumes = null)
    {
        var data = await Task.Run(async () =>
        {
            using var conn = Database.Open(dbPath);
            var snapshot = await (volumes ?? VolumeSnapshotSource.Shared).CaptureAsync(FileLocations.Files(conn).Select(f => f.FilePath));
            return (Files: LocalSeriesGrouping.Files(conn, snapshot), Targets: LocalSeriesGrouping.Targets(conn));
        });
        return await new SeriesGroupingWindow(dbPath, data.Files, data.Targets).ShowDialog<bool>(owner);
    }

    private void Target_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NewTitleBox is not null)
            NewTitleBox.IsEnabled = TargetBox.SelectedItem is LocalSeriesTarget { Id: 0 };
    }

    private void Filter_Changed(object? sender, TextChangedEventArgs e)
    {
        var query = (FilterBox.Text ?? "").Trim();
        FilesList.ItemsSource = _files.Where(file =>
            file.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || file.FilePath.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (_saving) return;
        try
        {
            var assignments = _files.Where(file => file.IsSelected).Select(file => file.Assignment()).ToArray();
            if (assignments.Length == 0)
            {
                StatusText.Text = "Select at least one file using its checkbox.";
                return;
            }
            if (TargetBox.SelectedItem is not LocalSeriesTarget target)
            {
                StatusText.Text = "Choose a destination series.";
                return;
            }
            var title = (NewTitleBox.Text ?? "").Trim();
            if (target.Id == 0 && MovieIndex.NormalizeTitle(title).Length == 0)
            {
                StatusText.Text = "Enter a title for the new series.";
                return;
            }
            _saving = true;
            SaveButton.IsEnabled = false;
            var confirmed = await MessageBoxWindow.ConfirmAsync(this, "Organise series",
                $"Group {assignments.Length} selected file(s) in \"{(target.Id > 0 ? target.Label : title)}\"?\n\n"
                + "Matching season and episode numbers are treated as copies of the same episode. "
                + "Manual assignments survive later scans. No video files are changed.\n\n"
                + "An old film entry with your rating, viewings, Watchlist entry, reported problem or chosen match is kept separately.",
                "Group files");
            if (!confirmed) return;

            await Task.Run(async () =>
            {
                using var conn = Database.Open(_dbPath);
                await DatabaseWriteLane.RunAsync(conn, _ =>
                {
                    LocalSeriesGrouping.Assign(conn, target.Id > 0 ? target.Id : null, title, assignments);
                    return Task.CompletedTask;
                }, default);
            });
            _saving = false;
            Close(true);
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Write("app.log", $"could not group local episodes: {ex.Message}");
            StatusText.Text = $"Could not group these files: {ex.Message}";
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = _files.Count > 0;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (!_saving) Close(false);
    }
}
