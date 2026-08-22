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

        public MovieDetailsWindow() : this(new MovieDetailsVm())
        {
        }

        public MovieDetailsWindow(MovieDetailsVm vm)
        {
            Vm = vm;
            DataContext = Vm;
            InitializeComponent();

            UpdateFileNote();
            LoadArtwork();

            Closed += (_, __) => _cts.Cancel();
        }

        private void UpdateFileNote()
        {
            if (Vm.IsRemote)
            {
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
        /// </summary>
        private async Task PlayFromServerAsync()
        {
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
