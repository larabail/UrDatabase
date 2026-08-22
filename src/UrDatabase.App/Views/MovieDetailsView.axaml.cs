using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UrDatabase.Models;
using UrDatabase.Services;

namespace UrDatabase.Views
{
    /// <summary>
    /// The details screen, hosted inside <see cref="MainWindow"/> rather than opened as a window
    /// of its own.
    /// </summary>
    /// <remarks>
    /// It was a 1000x640 dialog. The one thing this screen has that the library does not is a
    /// backdrop, and that window gave a 16:9 photograph about a third of the screen to be seen
    /// in. In place it gets the whole window, and there is no second window to position, to own,
    /// or to lose behind the first.
    ///
    /// <see cref="ShowAsync"/> keeps the shape the call sites already used — it is awaited and
    /// completes when the screen is dismissed — so opening a film still reads as one statement
    /// and the caller's cancellation and error handling are unchanged.
    /// </remarks>
    public partial class MovieDetailsView : UserControl
    {
        public MovieDetailsVm? Vm { get; private set; }

        private CancellationTokenSource? _cts;

        /// <summary>
        /// Where to record a file the user links by hand. Null when the screen was shown without
        /// one — a Jellyfin film, or the XAML designer — in which case linking still updates what
        /// is on screen and simply does not outlive it.
        /// </summary>
        private string? _dbPath;

        /// <summary>Completed when the screen is dismissed. Null while nothing is being shown.</summary>
        private TaskCompletionSource? _closed;

        public MovieDetailsView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Shows a film and returns when the user leaves it.
        /// </summary>
        public Task ShowAsync(MovieDetailsVm vm, string? dbPath = null)
        {
            // Leaving one film open behind another would strand its completion source and hang
            // whichever caller was awaiting it.
            if (_closed is not null) Close();

            Vm = vm;
            _dbPath = dbPath;
            DataContext = vm;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _closed = new TaskCompletionSource();

            Bind(vm);
            IsVisible = true;

            // Focus has to land inside this screen or Escape and the arrow keys keep going to
            // the library underneath, which is still there and still focusable.
            BackButton.Focus();

            LoadArtwork(_cts.Token);

            return _closed.Task;
        }

        /// <summary>
        /// Dismisses the screen and releases whoever is awaiting <see cref="ShowAsync"/>.
        /// Safe to call when nothing is open.
        /// </summary>
        public void Close()
        {
            if (_closed is null) return;

            _cts?.Cancel();
            IsVisible = false;

            BackdropImage.Source = null;
            PosterImage.Source = null;

            var closed = _closed;
            _closed = null;
            Vm = null;

            closed.TrySetResult();
        }

        /// <summary>True while a film is on screen.</summary>
        public bool IsShowing => _closed is not null;

        private void Bind(MovieDetailsVm vm)
        {
            TitleText.Text = vm.Title;
            FactsList.ItemsSource = DetailFacts.For(vm);

            GenresText.Text = vm.Genres ?? "";
            GenresText.IsVisible = !string.IsNullOrWhiteSpace(vm.Genres);

            // An empty panel where the plot should be reads as a failed request. Which sentence
            // goes there depends on why it is empty — see MissingMetadata.
            OverviewText.Text = string.IsNullOrWhiteSpace(vm.Overview)
                ? MissingMetadata.OverviewNotice(vm.IsRemote, vm.TmdbConfigured)
                : vm.Overview;

            var cast = vm.TopCast
                .Select(CreditLine.SplitCast)
                .Where(c => c.Name.Length > 0)
                .Select(c => new CreditEntry { Primary = c.Name, Secondary = c.Character })
                .ToList();

            var crew = vm.KeyCrew
                .Select(CreditLine.SplitCrew)
                .Where(c => c.Name.Length > 0)
                .Select(c => new CreditEntry { Primary = c.Name, Secondary = c.Job })
                .ToList();

            CastList.ItemsSource = cast;
            CrewList.ItemsSource = crew;

            ShowMissingCredits(cast, crew, vm);

            LinkFileButton.IsVisible = !vm.IsRemote;

            AttributionText.Text = vm.IsRemote
                ? "Metadata and artwork supplied by your Jellyfin server. IMDb rating retrieved from the OMDb API; neither IMDb nor OMDb endorses this application."
                : "Metadata and artwork from TMDB. This product uses the TMDB API but is not endorsed or certified by TMDB. IMDb rating retrieved from the OMDb API; neither IMDb nor OMDb endorses this application.";

            UpdateFileNote();
        }

        /// <summary>
        /// Says why a credit list is empty instead of leaving a heading over nothing, and says it
        /// accurately: a film nobody asked TMDB about has not been "not found".
        /// </summary>
        private void ShowMissingCredits(List<CreditEntry> cast, List<CreditEntry> crew, MovieDetailsVm vm)
        {
            var reason = MissingMetadata.CreditsNotice(vm.IsRemote, vm.TmdbConfigured);

            NoCastText.Text = reason;
            NoCastText.IsVisible = cast.Count == 0;

            NoCrewText.Text = reason;
            NoCrewText.IsVisible = crew.Count == 0;
        }

        /// <summary>
        /// Says which file Play will open and how confident the app is about it. The wording lives
        /// in <see cref="PlayPrompts"/>, where it can be asserted on.
        /// </summary>
        private void UpdateFileNote()
        {
            if (Vm is null) return;

            FileNote.Text = PlayPrompts.FileNote(Vm);
        }

        private async void LoadArtwork(CancellationToken ct)
        {
            var vm = Vm;
            if (vm is null) return;

            var poster = await ImageLoader.LoadAsync(vm.PosterPath, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(Vm, vm)) return;
            PosterImage.Source = poster;

            var backdrop = await ImageLoader.LoadAsync(vm.BackdropUrl, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(Vm, vm)) return;
            BackdropImage.Source = backdrop;
        }

        /// <summary>
        /// Escape leaves the film, which is what a screen occupying the whole window has to
        /// honour: there is no title bar close button on it any more.
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && IsShowing)
            {
                Close();
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        private void Back_Click(object? sender, RoutedEventArgs e) => Close();

        private async void PlayBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (Vm is null) return;

            // Re-asked here rather than trusted from when the link was made: the path is ordinary
            // local state, and a catalogue restored from elsewhere or written by an older build
            // can name a file that is missing, or one the operating system would execute rather
            // than play.
            var refusal = PlayPrompts.DescribeRefusal(Vm);
            if (refusal is not null)
            {
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", refusal);
                return;
            }

            if (Vm.IsRemote)
            {
                await PlayFromServerAsync();
                return;
            }

            // A guess gets a question. The whole bug was that it did not: a title short enough to
            // appear inside another film's filename opened that other film, with the screen still
            // reporting the one you asked for.
            if (PlayPrompts.NeedsConfirmation(Vm) && !await ConfirmSuggestionAsync())
                return;

            try
            {
                FileLauncher.Open(Vm.FilePath!);
            }
            catch (Exception ex)
            {
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", $"Could not launch file:{Environment.NewLine}{ex.Message}");
            }
        }

        /// <summary>
        /// Asks before opening a file the app only guessed at, and records the answer when it is
        /// yes so the question is asked once rather than every time.
        /// </summary>
        private async Task<bool> ConfirmSuggestionAsync()
        {
            var confirmed = await MessageBoxWindow.ConfirmAsync(
                Owner(),
                "UrDatabase",
                PlayPrompts.ConfirmationQuestion(Vm!),
                confirmText: "Play");

            if (confirmed)
            {
                RememberLink(Vm!.FilePath!);
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
            if (Vm is null) return;

            if (string.IsNullOrWhiteSpace(Vm.StreamUrl))
            {
                await MessageBoxWindow.ShowAsync(
                    Owner(),
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
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", ex.Message);
            }
            catch (Exception ex)
            {
                // Deliberately not the URL, which contains an access token.
                AppLog.Write("jellyfin.log", $"playback failed: {JellyfinClient.Redact(ex.Message)}");
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", $"Could not start playback:{Environment.NewLine}{ex.Message}");
            }
        }

        private async void LinkFile_Click(object? sender, RoutedEventArgs e)
        {
            if (Vm is null) return;

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null) return;

            // Avalonia's StorageProvider replaces Microsoft.Win32.OpenFileDialog and is the
            // only picker that works on macOS.
            var videoFiles = new FilePickerFileType("Video files")
            {
                Patterns = ScanService.SupportedExtensions.Select(ext => "*" + ext).ToArray(),
                AppleUniformTypeIdentifiers = new[] { "public.movie" },
                MimeTypes = new[] { "video/*" }
            };

            var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose movie file",
                AllowMultiple = false,
                FileTypeFilter = new[] { videoFiles, FilePickerFileTypes.All }
            });

            var path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
            if (string.IsNullOrWhiteSpace(path)) return;

            // The picker's type filter is advisory — macOS honours it loosely and the dialog
            // offers "All files" besides — so what was actually chosen is checked here, and the
            // refusal is shown rather than swallowed: the user picked this file deliberately and
            // is owed a reason.
            var refusal = PlayTargetResolver.DescribeLinkRefusal(path);
            if (refusal is not null)
            {
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", refusal);
                return;
            }

            Vm.FilePath = path;
            Vm.FileMatch = PlayTargetKind.Linked;
            RememberLink(path);
            UpdateFileNote();
        }

        /// <summary>
        /// Writes the link to the catalogue so it survives leaving the film. It used to live only
        /// in this object, so choosing a file fixed the problem until you reopened the film and it
        /// was forgotten — which made the whole feature something you had to redo every time.
        ///
        /// A failure here is reported to the log and not to the user: the file they picked is
        /// already playable on this screen, and interrupting them to say the choice will not be
        /// remembered helps nobody mid-film.
        /// </summary>
        private void RememberLink(string path)
        {
            if (Vm is null || string.IsNullOrWhiteSpace(_dbPath) || Vm.LocalId <= 0) return;

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

        /// <summary>
        /// The window this screen is inside. A message box still needs a window to be modal to,
        /// and this control is no longer one.
        /// </summary>
        private Window? Owner() => TopLevel.GetTopLevel(this) as Window;
    }
}
