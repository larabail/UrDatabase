using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UrDatabase.Services;

namespace UrDatabase.Views;

public partial class DiskRecoveryWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private string _db = "";
    private IVolumeSnapshotSource _volumes = VolumeSnapshotSource.Shared;
    private Func<IReadOnlyList<ConfiguredRoot>> _configured = () => Array.Empty<ConfiguredRoot>();
    private IReadOnlyList<LocatedFile> _files = Array.Empty<LocatedFile>();
    private IReadOnlyList<LibraryRoot> _roots = Array.Empty<LibraryRoot>();
    private DiskRecovery? _recovery;
    private RecoveryPreview? _preview;
    private string? _sourceId;
    private bool _busy;
    public bool Changed { get; private set; }

    public DiskRecoveryWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _lifetime.Cancel();
    }

    public static async Task<bool> ShowAsync(Window owner, string db, AppConfig config,
        IVolumeSnapshotSource? volumes = null, long? fileId = null, string? oldRoot = null)
    {
        var window = new DiskRecoveryWindow();
        await window.LoadAsync(db, () => AppConfig.Load(config.SourcePath).LocationRoots(), volumes, fileId, oldRoot);
        await window.ShowDialog(owner);
        return window.Changed;
    }

    internal async Task LoadAsync(string db, Func<IReadOnlyList<ConfiguredRoot>> configured,
        IVolumeSnapshotSource? volumes = null, long? fileId = null, string? oldRoot = null)
    {
        _db = db;
        _configured = configured;
        _volumes = volumes ?? VolumeSnapshotSource.Shared;
        _recovery = new DiskRecovery(db, configured, _volumes);
        var data = await Task.Run(() =>
        {
            using var conn = Database.Open(db);
            return (Files: FileLocations.Files(conn),
                Roots: FileLocations.Roots(conn), Configured: configured());
        });
        _files = data.Files;
        _roots = data.Roots;
        FileList.ItemsSource = _files;
        RootList.ItemsSource = data.Configured.Select(r => FileRootPath(r))
            .Concat(_roots.Where(r => r.ScopePath.Length > 0 && data.Configured.Any(c => c.Kind == r.Kind && c.Token == r.Token))
                .Select(r => r.FilePath))
            .Concat(_files.Select(f => DriveVolumes.VolumeOf(f.FilePath)).OfType<string>())
            .Distinct().OrderBy(p => p).ToList();
        if (fileId is not null) FileList.SelectedItem = _files.FirstOrDefault(f => f.Id == fileId);
        if (oldRoot is not null) OldRootBox.Text = oldRoot;
        StatusText.Text = "Legacy links need one confirmation per disk or folder. Cancel leaves everything unchanged.";
    }

    private string FileRootPath(ConfiguredRoot root) =>
        _roots.FirstOrDefault(r => r.Kind == root.Kind && r.Token == root.Token)?.FilePath ?? root.Path;

    private void Search_Changed(object? sender, TextChangedEventArgs e)
    {
        if (FileList is null) return;
        FileList.ItemsSource = _files.Where(f => f.FilePath.Contains(SearchBox.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void File_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not LocatedFile file) return;
        _sourceId = file.Mode == "volume" ? file.VolumeId : null;
        OldRootBox.Text = DriveVolumes.VolumeOf(file.FilePath);
        Invalidate();
    }

    private void Root_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (RootList.SelectedItem is not string path) return;
        var root = _roots.FirstOrDefault(r => r.FilePath == path);
        _sourceId = root?.Mode == "volume" ? root.VolumeId : null;
        OldRootBox.Text = path;
        Invalidate();
    }

    private void Mapping_Changed(object? sender, RoutedEventArgs e) => Invalidate();
    private void Invalidate()
    {
        _preview = null;
        if (ApplyButton is not null) ApplyButton.IsEnabled = false;
        if (PreviewList is not null) PreviewList.ItemsSource = null;
    }

    private async void Locate_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || FileList.SelectedItem is not LocatedFile file)
        {
            StatusText.Text = "Select the saved file you recognise first.";
            return;
        }
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate this known video file", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Video files")
            { Patterns = ScanService.SupportedExtensions.Select(ext => "*" + ext).ToArray() } }
        });
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        await LocateAsync(file, path);
    }

    internal async Task LocateAsync(LocatedFile file, string path)
    {
        await RunAsync(async () =>
        {
            var request = await Task.Run(async () =>
            {
                var snapshot = await _volumes.CaptureAsync(new[] { path }, _lifetime.Token);
                return DiskRecovery.FromFile(file, path, snapshot);
            });
            _sourceId = request.SourceVolumeId;
            OldRootBox.Text = request.OldRoot;
            NewRootBox.Text = request.NewRoot;
            StatusText.Text = "The full relative path matches. Preview the proposed disk mapping before confirming.";
        });
    }

    private async void Folder_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "Choose the current folder corresponding to the old root", AllowMultiple = false });
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) NewRootBox.Text = path;
    }

    private async void Preview_Click(object? sender, RoutedEventArgs e) => await PreviewAsync();
    internal async Task PreviewAsync()
    {
        if (_recovery is null || _busy) return;
        var request = new RelocationRequest(OldRootBox.Text ?? "", NewRootBox.Text ?? "", _sourceId, PathOnlyCheck.IsChecked == true);
        await RunAsync(async () =>
        {
            _preview = await _recovery.PreviewAsync(request, _lifetime.Token);
            PreviewList.ItemsSource = _preview.Files.Select(f => $"{f.OldPath}\n  -> {f.Target.Path} [{f.State}]")
                .Concat(_preview.Roots.Select(r => $"{(r.ScopePath.Length > 0 ? "Additional scan scope for" : r.Kind)}: {r.Token}\n  -> {r.Target.Path} [{r.State}]"))
                .Concat(_preview.Conflicts.Select(c => "CONFLICT: " + c)).ToList();
            StatusText.Text = _preview.Summary;
        });
    }

    private async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        var preview = _preview;
        if (_busy || preview?.CanApply != true || _recovery is null) return;
        var confirmed = await MessageBoxWindow.ConfirmAsync(this, "Confirm this disk/folder mapping?",
            preview.Summary + "\n\nConfirm that these are the files you recognise. A matching path is not a content check. " +
            "Files still absent will not be marked found. No media file, title assignment or history will be changed.",
            "Repair links");
        if (!confirmed) return;
        await RunAsync(async () =>
        {
            await _recovery.CommitAsync(preview, _lifetime.Token);
            Changed = true;
            Close();
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        OldRootBox.IsEnabled = NewRootBox.IsEnabled = FileList.IsEnabled = RootList.IsEnabled = PathOnlyCheck.IsEnabled = false;
        PreviewButton.IsEnabled = ApplyButton.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Invalidate();
            AppLog.Write("drives.log", $"Recovery failed: {ex.Message}");
            StatusText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            OldRootBox.IsEnabled = NewRootBox.IsEnabled = FileList.IsEnabled = RootList.IsEnabled = PathOnlyCheck.IsEnabled = true;
            PreviewButton.IsEnabled = true;
            ApplyButton.IsEnabled = _preview?.CanApply == true;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
