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

        /// <summary>
        /// Where to record a file the user links by hand. Null when the window was opened without
        /// one — a Jellyfin film, or the XAML designer — in which case linking still updates the
        /// open window and simply does not outlive it.
        /// </summary>
        private readonly string? _dbPath;

        private readonly CancellationTokenSource _cts = new();

        public MovieDetailsWindow() : this(new MovieDetailsVm())
        {
        }

        public MovieDetailsWindow(MovieDetailsVm vm, string? dbPath = null)
        {
            Vm = vm;
            _dbPath = dbPath;
            DataContext = Vm;
            InitializeComponent();

            UpdateFileNote();
            LoadArtwork();

            Closed += (_, __) => _cts.Cancel();
        }

        /// <summary>
        /// Says which file Play will open and how confident the app is about it. The wording lives
        /// in <see cref="PlayPrompts"/>, where it can be asserted on.
        /// </summary>
        private void UpdateFileNote() => FileNote.Text = PlayPrompts.FileNote(Vm);

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
                await MessageBoxWindow.ShowAsync(this, "UrDatabase", PlayPrompts.NothingToPlay);
                return;
            }

            // A guess gets a question. The whole bug was that it did not: a title short enough to
            // appear inside another film's filename opened that other film, with the window still
            // reporting the one you asked for.
            if (PlayPrompts.NeedsConfirmation(Vm) && !await ConfirmSuggestionAsync())
                return;

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
        /// Asks before opening a file the app only guessed at, and records the answer when it is
        /// yes so the question is asked once rather than every time.
        /// </summary>
        private async Task<bool> ConfirmSuggestionAsync()
        {
            var confirmed = await MessageBoxWindow.ConfirmAsync(
                this,
                "UrDatabase",
                PlayPrompts.ConfirmationQuestion(Vm),
                confirmText: "Play");

            if (confirmed)
            {
                RememberLink(Vm.FilePath!);
                UpdateFileNote();
            }

            return confirmed;
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
            Vm.FileMatch = PlayTargetKind.Linked;
            RememberLink(path);
            UpdateFileNote();
        }

        /// <summary>
        /// Writes the link to the catalogue so it survives closing the window. It used to live
        /// only in this object, so choosing a file fixed the problem until you reopened the film
        /// and it was forgotten — which made the whole feature something you had to redo every
        /// time.
        ///
        /// A failure here is reported to the log and not to the user: the file they picked is
        /// already playable in this window, and interrupting them to say the choice will not be
        /// remembered helps nobody mid-film.
        /// </summary>
        private void RememberLink(string path)
        {
            if (string.IsNullOrWhiteSpace(_dbPath) || Vm.LocalId <= 0) return;

            try
            {
                using var conn = Database.Open(_dbPath);
                PlayTargetResolver.LinkFile(conn, Vm.LocalId, path);
                Vm.FileMatch = PlayTargetKind.Linked;
            }
            catch (Exception ex)
            {
                AppLog.Write("app.log", $"could not link {path} to movie {Vm.LocalId}: {ex.Message}");
            }
        }
    }
}
