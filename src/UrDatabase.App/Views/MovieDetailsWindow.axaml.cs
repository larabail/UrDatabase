using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UrDatabase.Models;
using UrDatabase.Services;

namespace UrDatabase.Views
{
    public partial class MovieDetailsWindow : Window
    {
        public MovieDetailsVm Vm { get; }

        private readonly CancellationTokenSource _cts = new();
        private readonly JellyfinClient? _jellyfin;

        /// <summary>
        /// Cancels the transfer only, not the window. Separate from <see cref="_cts"/> because a
        /// download is the one thing here a user stops without closing what they are looking at.
        /// </summary>
        private CancellationTokenSource? _downloadCts;

        /// <summary>True once a download finished, which the caller reads to refresh the library.</summary>
        public bool DownloadedSomething { get; private set; }

        public MovieDetailsWindow() : this(new MovieDetailsVm())
        {
        }

        public MovieDetailsWindow(MovieDetailsVm vm, JellyfinClient? jellyfin = null)
        {
            Vm = vm;
            DataContext = Vm;
            _jellyfin = jellyfin;
            InitializeComponent();

            // Asked here rather than by the caller so that a film downloaded, then deleted from
            // Finder, offers its download again instead of insisting it is already there.
            if (Vm.IsRemote && string.IsNullOrWhiteSpace(Vm.DownloadedPath))
                Vm.DownloadedPath = JellyfinDownload.FindExisting(Vm.DownloadFolder, Vm.Title, Vm.Year);

            UpdateFileNote();
            UpdateDownloadButton();
            LoadArtwork();

            Closed += (_, __) => { _downloadCts?.Cancel(); _cts.Cancel(); };
        }

        private void UpdateDownloadButton()
        {
            DownloadBtn.IsVisible = Vm.CanDownload && _jellyfin is not null;
        }

        private void UpdateFileNote()
        {
            if (Vm.IsRemote)
            {
                if (!string.IsNullOrWhiteSpace(Vm.DownloadedPath))
                {
                    FileNote.Text = $"Downloaded to {Vm.DownloadedPath}. Plays with the server switched off.";
                    return;
                }

                // Never the URL itself: it carries an access token.
                FileNote.Text = string.IsNullOrWhiteSpace(Vm.StreamUrl)
                    ? "On the Jellyfin server, which could not be reached. Play will not work until it is back."
                    : "Streams from your Jellyfin server. Play opens it in VLC or IINA.";
                return;
            }

            FileNote.Text = string.IsNullOrWhiteSpace(Vm.FilePath)
                ? "No local file linked. Play will open nothing."
                : $"File: {Path.GetFileName(Vm.FilePath)}";
        }

        private async void LoadArtwork()
        {
            PosterImage.Source = await ImageLoader.LoadAsync(Vm.PosterPath, _cts.Token);
            BackdropImage.Source = await ImageLoader.LoadAsync(Vm.BackdropUrl, _cts.Token);
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();

        private async void PlayBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (Vm.IsRemote)
            {
                await PlayFromServerAsync();
                return;
            }

            if (string.IsNullOrWhiteSpace(Vm.FilePath) || !File.Exists(Vm.FilePath))
            {
                await MessageBoxWindow.ShowAsync(this, "UrDatabase", "No playable file found for this title.");
                return;
            }

            try
            {
                FileLauncher.Open(Vm.FilePath);
            }
            catch (Exception ex)
            {
                await MessageBoxWindow.ShowAsync(this, "UrDatabase", $"Could not launch file:{Environment.NewLine}{ex.Message}");
            }
        }

        /// <summary>
        /// Streams the film. Both failure modes here are ordinary rather than exceptional — the
        /// server is not always reachable and a player is not always installed — so each gets a
        /// sentence that says what to do about it, rather than an exception message.
        ///
        /// A downloaded copy is preferred over the stream whenever one exists. That is what makes
        /// downloading worth doing: the same button keeps working when the server does not.
        /// </summary>
        private async Task PlayFromServerAsync()
        {
            if (!string.IsNullOrWhiteSpace(Vm.DownloadedPath) && File.Exists(Vm.DownloadedPath))
            {
                try
                {
                    FileLauncher.Open(Vm.DownloadedPath);
                    return;
                }
                catch (Exception ex)
                {
                    await MessageBoxWindow.ShowAsync(this, "UrDatabase", $"Could not launch file:{Environment.NewLine}{ex.Message}");
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(Vm.StreamUrl))
            {
                await MessageBoxWindow.ShowAsync(
                    this,
                    "UrDatabase",
                    "This film is on your Jellyfin server, which could not be reached. " +
                    "It will play again once you are back on the same network as the server.");
                return;
            }

            try
            {
                MediaPlayerLauncher.Play(Vm.StreamUrl);
            }
            catch (MediaPlayerNotFoundException ex)
            {
                await MessageBoxWindow.ShowAsync(this, "UrDatabase", ex.Message);
            }
            catch (Exception ex)
            {
                // Deliberately not the URL, which contains an access token.
                AppLog.Write("jellyfin.log", $"playback failed: {JellyfinClient.Redact(ex.Message)}");
                await MessageBoxWindow.ShowAsync(this, "UrDatabase", $"Could not start playback:{Environment.NewLine}{ex.Message}");
            }
        }

        /// <summary>
        /// Starts the download, or stops one already running. A single button for both because
        /// there is only ever one transfer in this window and a separate Cancel would be disabled
        /// almost all of the time.
        /// </summary>
        private async void DownloadBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (_downloadCts is not null)
            {
                _downloadCts.Cancel();
                return;
            }

            if (_jellyfin is null || string.IsNullOrWhiteSpace(Vm.RemoteId)) return;

            _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            DownloadBtn.Content = "Cancel";
            DownloadProgress.IsVisible = true;
            DownloadProgress.IsIndeterminate = true;
            DownloadProgress.Value = 0;

            var progress = new Progress<JellyfinDownloadProgress>(report =>
            {
                FileNote.Text = $"Downloading… {report.Describe()}";

                // A server that sends no length leaves the bar sweeping rather than sitting at
                // zero, which reads as stalled.
                DownloadProgress.IsIndeterminate = report.Fraction is null;
                if (report.Fraction is double fraction) DownloadProgress.Value = fraction;
            });

            try
            {
                var downloader = new JellyfinDownloader(_jellyfin);

                // The token is needed for the request itself, and this is the first point in the
                // window's life where the network is required at all.
                await _jellyfin.ConnectAsync(_downloadCts.Token);

                var result = await downloader.DownloadAsync(
                    Vm.RemoteId!,
                    Vm.Title,
                    Vm.Year,
                    Vm.DownloadFolder,
                    container: null,
                    progress: progress,
                    ct: _downloadCts.Token);

                Vm.DownloadedPath = result.Path;
                DownloadedSomething = !result.AlreadyExisted;

                await RegisterDownloadAsync(result.Path);

                FileNote.Text = result.AlreadyExisted
                    ? $"Already downloaded to {result.Path}."
                    : $"Downloaded {JellyfinDownload.DescribeSize(result.Bytes)} to {result.Path}. Plays with the server switched off.";
            }
            catch (OperationCanceledException)
            {
                FileNote.Text = "Download stopped. What was transferred is kept, and starting again carries on from there.";
            }
            catch (JellyfinException ex)
            {
                await MessageBoxWindow.ShowAsync(this, "UrDatabase", ex.Message);
                UpdateFileNote();
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"download failed: {ex}"));
                await MessageBoxWindow.ShowAsync(this, "UrDatabase", $"Could not download this film:{Environment.NewLine}{ex.Message}");
                UpdateFileNote();
            }
            finally
            {
                _downloadCts.Dispose();
                _downloadCts = null;

                DownloadBtn.Content = "Download";
                DownloadProgress.IsVisible = false;
                DownloadProgress.IsIndeterminate = false;
                UpdateDownloadButton();
            }
        }

        /// <summary>
        /// Puts the finished file in the catalogue so it is playable and searchable straight away
        /// rather than after the user works out that a scan is what makes a film appear.
        ///
        /// Failing to record it is not worth interrupting anybody over: the film downloaded, it
        /// plays from this window, and the next scan of the folder will catalogue it.
        /// </summary>
        private async Task RegisterDownloadAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(Vm.DatabasePath)) return;

            try
            {
                using var conn = Database.Open(Vm.DatabasePath);
                await ScanService.RecordSingleFileAsync(conn, path);
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", $"downloaded film not catalogued: {ex.Message}");
            }
        }

        private async void LinkFile_Click(object? sender, RoutedEventArgs e)
        {
            // Avalonia's StorageProvider replaces Microsoft.Win32.OpenFileDialog and is the
            // only picker that works on macOS.
            var videoFiles = new FilePickerFileType("Video files")
            {
                Patterns = ScanService.SupportedExtensions.Select(ext => "*" + ext).ToArray(),
                AppleUniformTypeIdentifiers = new[] { "public.movie" },
                MimeTypes = new[] { "video/*" }
            };

            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose movie file",
                AllowMultiple = false,
                FileTypeFilter = new[] { videoFiles, FilePickerFileTypes.All }
            });

            var path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
            if (string.IsNullOrWhiteSpace(path)) return;

            Vm.FilePath = path;
            UpdateFileNote();
        }
    }
}
