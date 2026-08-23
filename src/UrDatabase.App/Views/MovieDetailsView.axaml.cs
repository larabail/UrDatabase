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

        /// <summary>
        /// Needed to search TMDB when the automatic match was wrong. Null in the designer and when
        /// the screen was shown without one, which disables the correction rather than failing at
        /// it.
        /// </summary>
        private AppConfig? _config;

        /// <summary>
        /// How to look up an IMDb rating for a film that has just been re-identified. Owned by the
        /// main window, which holds the rating service and the connection it caches through; this
        /// screen borrows it rather than building a second one that would ask OMDb again.
        /// </summary>
        private Func<string?, long?, CancellationToken, Task<double?>>? _ratingLookup;

        /// <summary>
        /// The server, for downloading a film off it. Null when none is configured, which hides
        /// the button rather than offering something that cannot work.
        /// </summary>
        private JellyfinClient? _jellyfin;

        /// <summary>
        /// Cancels the transfer without leaving the film. Separate from <see cref="_cts"/> because
        /// a download is the one thing here somebody stops while carrying on reading the page.
        /// </summary>
        private CancellationTokenSource? _downloadCts;

        /// <summary>
        /// The same, for a transfer going the other way. A second source rather than a shared one
        /// because the two buttons are never shown for the same film — a film is on the server or
        /// on this disk — and one token cancelled by the wrong button would be a puzzle.
        /// </summary>
        private CancellationTokenSource? _uploadCts;

        /// <summary>
        /// The window's own lifetime, for work that has to outlive this screen.
        /// </summary>
        /// <remarks>
        /// Only progress reporting uses it, and it has to: a film keeps playing after the viewer
        /// goes back to the library, and following it on <see cref="_cts"/> would stop reporting
        /// the moment they pressed Back. It is still bounded — the app closing ends it, with a
        /// final stop at the last position seen.
        /// </remarks>
        private CancellationToken _appLifetime = CancellationToken.None;

        /// <summary>
        /// True once a download has finished while this screen was open. Read by the caller after
        /// <see cref="ShowAsync"/> returns: the library behind it now has a row it did not have.
        /// </summary>
        public bool DownloadedSomething { get; private set; }

        /// <summary>
        /// True once a correction on this screen renamed the film. Read by the caller after
        /// <see cref="ShowAsync"/> returns, for the same reason as
        /// <see cref="DownloadedSomething"/>: the library behind it is sorted and grouped by a name
        /// that has just changed, so the card is in the wrong place under the wrong text until
        /// something reloads it.
        /// </summary>
        public bool RenamedSomething { get; private set; }

        public MovieDetailsView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Shows a film and returns when the user leaves it.
        /// </summary>
        public Task ShowAsync(
            MovieDetailsVm vm,
            string? dbPath = null,
            AppConfig? config = null,
            Func<string?, long?, CancellationToken, Task<double?>>? ratingLookup = null,
            JellyfinClient? jellyfin = null,
            CancellationToken appLifetime = default)
        {
            // Leaving one film open behind another would strand its completion source and hang
            // whichever caller was awaiting it.
            if (_closed is not null) Close();

            Vm = vm;
            _dbPath = dbPath;
            _config = config;
            _ratingLookup = ratingLookup;
            _jellyfin = jellyfin;
            _appLifetime = appLifetime;
            DownloadedSomething = false;
            RenamedSomething = false;
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

            // A transfer belongs to the film that is on screen. Left running, it would finish
            // against a screen showing something else and report itself there.
            _downloadCts?.Cancel();
            _uploadCts?.Cancel();

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
            CorrectMatchButton.IsVisible = !vm.IsRemote;

            // Asked here rather than by the caller so that a film downloaded and then deleted in
            // Finder offers its download again instead of insisting it is already there.
            if (vm.IsRemote && string.IsNullOrWhiteSpace(vm.DownloadedPath))
                vm.DownloadedPath = JellyfinDownload.FindExisting(vm.DownloadFolder, vm.Title, vm.Year);

            UpdateDownloadButton();
            UpdateUploadButton();

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
        /// Streams the film, or opens the downloaded copy when there is one. Both failure modes
        /// here are ordinary rather than exceptional — the server is not always reachable and a
        /// player is not always installed — so each gets a sentence that says what to do about it,
        /// rather than an exception message.
        /// </summary>
        private async Task PlayFromServerAsync()
        {
            if (Vm is null) return;

            // The point of having downloaded it: the local copy plays whether or not the server
            // is there, so it is preferred over a stream that may be about to fail.
            if (!string.IsNullOrWhiteSpace(Vm.DownloadedPath) && File.Exists(Vm.DownloadedPath))
            {
                try
                {
                    FileLauncher.Open(Vm.DownloadedPath);
                }
                catch (Exception ex)
                {
                    await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", $"Could not launch file:{Environment.NewLine}{ex.Message}");
                }

                return;
            }

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
                // The interface is only asked for when there is somewhere for it to report to. A
                // port and a password for a film with no id on it, or with no server behind it,
                // would be a socket opened for nothing.
                var canReport = _jellyfin is not null && !string.IsNullOrWhiteSpace(Vm.RemoteId);

                var launch = MediaPlayerLauncher.Play(Vm.StreamUrl, withProgressReporting: canReport);

                // Not awaited: it lasts as long as the film, and the viewer is going back to the
                // library. Given the app's lifetime rather than this screen's, so leaving the film
                // does not stop the reporting — and so closing the window does, with a last word.
                _ = PlaybackTracking.Follow(launch, _jellyfin, Vm.RemoteId, _appLifetime);
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

        private void UpdateDownloadButton()
        {
            DownloadButton.IsVisible = Vm is not null && Vm.CanDownload && _jellyfin is not null;
        }

        /// <summary>
        /// Starts the download, or stops one already running. One button for both, because there
        /// is only ever one transfer on this screen and a separate Cancel would spend almost all
        /// of its life disabled.
        /// </summary>
        private async void Download_Click(object? sender, RoutedEventArgs e)
        {
            if (_downloadCts is not null)
            {
                _downloadCts.Cancel();
                return;
            }

            var vm = Vm;
            if (vm is null || _jellyfin is null || string.IsNullOrWhiteSpace(vm.RemoteId)) return;

            _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? default);
            DownloadButton.Content = "Cancel";
            DownloadProgress.IsVisible = true;
            DownloadProgress.IsIndeterminate = true;
            DownloadProgress.Value = 0;

            var progress = new Progress<JellyfinDownloadProgress>(report =>
            {
                // The screen may have moved on to another film while this was in flight.
                if (!ReferenceEquals(Vm, vm)) return;

                FileNote.Text = $"Downloading… {report.Describe()}";

                // A server that sends no length leaves the bar sweeping rather than sitting at
                // zero, which reads as stalled.
                DownloadProgress.IsIndeterminate = report.Fraction is null;
                if (report.Fraction is double fraction) DownloadProgress.Value = fraction;
            });

            try
            {
                // The first point in this screen's life where the network is needed at all.
                await _jellyfin.ConnectAsync(_downloadCts.Token);

                var result = await new JellyfinDownloader(_jellyfin).DownloadAsync(
                    vm.RemoteId!,
                    vm.Title,
                    vm.Year,
                    vm.DownloadFolder,
                    container: null,
                    progress: progress,
                    ct: _downloadCts.Token);

                vm.DownloadedPath = result.Path;
                if (!result.AlreadyExisted) DownloadedSomething = true;

                await RegisterDownloadAsync(result.Path);

                if (ReferenceEquals(Vm, vm))
                {
                    FileNote.Text = result.AlreadyExisted
                        ? $"Already downloaded to {result.Path}."
                        : $"Downloaded {JellyfinDownload.DescribeSize(result.Bytes)} to {result.Path}. Plays with the server switched off.";
                }
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(Vm, vm))
                    FileNote.Text = "Download stopped. What was transferred is kept, and starting again carries on from there.";
            }
            catch (JellyfinException ex)
            {
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", ex.Message);
                if (ReferenceEquals(Vm, vm)) UpdateFileNote();
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"download failed: {ex}"));
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", $"Could not download this film:{Environment.NewLine}{ex.Message}");
                if (ReferenceEquals(Vm, vm)) UpdateFileNote();
            }
            finally
            {
                _downloadCts.Dispose();
                _downloadCts = null;

                DownloadButton.Content = "Download";
                DownloadProgress.IsVisible = false;
                DownloadProgress.IsIndeterminate = false;
                UpdateDownloadButton();
            }
        }

        /// <summary>
        /// Puts the finished file in the catalogue so it is playable and searchable straight away,
        /// rather than after the user works out that a scan is what makes a film appear.
        ///
        /// Failing to record it is not worth interrupting anybody over: the film downloaded, it
        /// plays from this screen, and the next scan of the folder catalogues it.
        /// </summary>
        private async Task RegisterDownloadAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(_dbPath)) return;

            try
            {
                using var conn = Database.Open(_dbPath);
                await ScanService.RecordSingleFileAsync(conn, path);
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", $"downloaded film not catalogued: {ex.Message}");
            }
        }

        /// <summary>
        /// The SFTP account films are uploaded through, or null when there is none. Read from the
        /// configuration the screen was given rather than held separately, so switching the
        /// feature on in Settings and reopening a film is enough to see the button.
        /// </summary>
        private JellyfinSftpSettings? SftpSettings =>
            _config?.JellyfinSftp is { IsConfigured: true } settings ? settings : null;

        /// <summary>
        /// Shown only when all three parts are there: a film worth sending, somewhere to send it,
        /// and a server to tell about it afterwards. An install with no SFTP account configured
        /// never sees this button at all.
        /// </summary>
        private void UpdateUploadButton()
        {
            UploadButton.IsVisible =
                Vm is not null && Vm.CanUpload && SftpSettings is not null && _jellyfin is not null;
        }

        /// <summary>
        /// Sends the film to the server, or stops a transfer already running. One button for both,
        /// for the reason the Download button is one button: there is only ever one transfer on
        /// this screen and a separate Cancel would spend almost all of its life disabled.
        /// </summary>
        private async void Upload_Click(object? sender, RoutedEventArgs e)
        {
            if (_uploadCts is not null)
            {
                _uploadCts.Cancel();
                return;
            }

            var vm = Vm;
            var settings = SftpSettings;
            if (vm is null || settings is null || _jellyfin is null) return;

            // Asked again here rather than trusted from when the button was drawn: the linked file
            // is ordinary local state and may have been moved or deleted since.
            var refusal = UploadPrompts.DescribeRefusal(vm);
            if (refusal is not null)
            {
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", refusal);
                if (ReferenceEquals(Vm, vm)) UpdateFileNote();
                return;
            }

            if (UploadPrompts.NeedsConfirmation(vm))
            {
                var confirmed = await MessageBoxWindow.ConfirmAsync(
                    Owner(),
                    "UrDatabase",
                    UploadPrompts.ConfirmationQuestion(vm),
                    confirmText: "Upload");

                if (!confirmed) return;
            }

            _uploadCts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? default);
            UploadButton.Content = UploadPrompts.CancelLabel;
            UploadProgress.IsVisible = true;
            UploadProgress.IsIndeterminate = true;
            UploadProgress.Value = 0;

            var progress = new Progress<JellyfinUploadProgress>(report =>
            {
                // The screen may have moved on to another film while this was in flight.
                if (!ReferenceEquals(Vm, vm)) return;

                FileNote.Text = UploadPrompts.Progress(report);

                UploadProgress.IsIndeterminate = report.Fraction is null;
                if (report.Fraction is double fraction) UploadProgress.Value = fraction;
            });

            // Disposed here rather than kept: a connection is worth holding open for one transfer
            // and not for the rest of an evening spent reading about films.
            using var transport = new SshNetSftpTransport(settings);

            try
            {
                var result = await new JellyfinUploader(transport, _jellyfin).UploadAsync(
                    vm.FilePath,
                    vm.Title,
                    vm.Year,
                    settings.MoviesPath,
                    progress,
                    _uploadCts.Token);

                // The server has it now, which is what hides the button and what the facts row
                // above has to agree with. Deliberately not a signal to the library behind this
                // screen: nothing about the local catalogue changed, and the server's own view of
                // its library will not have caught up until its scan finishes anyway.
                vm.IsOnServer = true;

                if (ReferenceEquals(Vm, vm))
                {
                    FactsList.ItemsSource = DetailFacts.For(vm);
                    FileNote.Text = UploadPrompts.Describe(result);
                }
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(Vm, vm)) FileNote.Text = UploadPrompts.Cancelled;
            }
            catch (JellyfinException ex)
            {
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", ex.Message);
                if (ReferenceEquals(Vm, vm)) UpdateFileNote();
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"upload failed: {ex}"));
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", $"Could not upload this film:{Environment.NewLine}{ex.Message}");
                if (ReferenceEquals(Vm, vm)) UpdateFileNote();
            }
            finally
            {
                _uploadCts.Dispose();
                _uploadCts = null;

                UploadButton.Content = UploadPrompts.ButtonLabel;
                UploadProgress.IsVisible = false;
                UploadProgress.IsIndeterminate = false;
                UpdateUploadButton();
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

        /// <summary>
        /// Lets the user say which film this actually is, and rebuilds the screen from their
        /// answer.
        ///
        /// TMDB is searched by title, and a title identifies nothing: the search returns its most
        /// popular near miss rather than nothing at all, and the poster it suggested was written to
        /// the catalogue and then never revisited, because the poster column is only ever filled
        /// when it is empty. That made one wrong guess permanent, with no way in the app to say
        /// otherwise.
        /// </summary>
        private async void CorrectMatch_Click(object? sender, RoutedEventArgs e)
        {
            var vm = Vm;
            if (vm is null) return;

            var owner = Owner();
            if (owner is null) return;

            if (_config is null)
            {
                await MessageBoxWindow.ShowAsync(owner, "UrDatabase",
                    "This screen was opened without any settings, so it cannot search TMDB.");
                return;
            }

            var chosen = await TmdbMatchWindow.ChooseAsync(owner, _config, vm.Title, vm.Year);
            if (chosen is null) return;

            try
            {
                await ApplyMatchAsync(vm, chosen);
            }
            catch (OperationCanceledException)
            {
                // The screen was dismissed, or the whole thing outran its timeout.
            }
            catch (Exception ex)
            {
                AppLog.Write("posters.log", $"could not apply tmdb match {chosen.TmdbId} to movie {vm.LocalId}: {ex}");
                await MessageBoxWindow.ShowAsync(owner, "UrDatabase",
                    $"Could not fetch that film from TMDB:{Environment.NewLine}{ex.Message}");
            }
        }

        /// <summary>
        /// Replaces everything TMDB told the app about this film, and records which film it is.
        ///
        /// The order matters. The poster and the id are saved before the details are fetched, so a
        /// correction survives the request failing or the screen being left under it: the right
        /// artwork with a stale plot is recoverable, and losing the answer altogether is the thing
        /// the user came here to stop happening.
        /// </summary>
        private async Task ApplyMatchAsync(MovieDetailsVm vm, TmdbCandidateVm chosen)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            using var tmdb = new TmdbService(
                apiKey: _config!.TmdbApiKey ?? "",
                posterCacheDir: _config.PosterCacheDir ?? "",
                imageSize: _config.TmdbImageSize ?? "w342",
                downloadPosters: _config.DownloadPosters);

            var poster = await ResolvePosterAsync(tmdb, vm.LocalId, chosen, _config.DownloadPosters, cts.Token);

            vm.TmdbId = chosen.TmdbId;
            if (poster is not null) vm.PosterPath = poster;

            // The film is called what the person just said it is. Leaving the filename's guess on
            // the card after they have identified the film is the app disagreeing with an answer it
            // asked for — and it is the name the library, the search box and the genre shelves all
            // show, so the correction has to reach it rather than stopping at the artwork.
            var renamed = MovieMatch.RenameTo(vm.Title, chosen.TmdbTitle);
            if (renamed is not null)
            {
                vm.Title = renamed;
                RenamedSomething = true;
            }

            await SaveMatchAsync(vm, chosen.TmdbId, poster, renamed, cts.Token);

            // Everything below repaints a screen the user may already have left. Vm is the guard:
            // showing another film swaps it, and Close sets it to null.
            var details = await tmdb.GetDetailsByIdAsync(chosen.TmdbId, cts.Token);
            if (!ReferenceEquals(Vm, vm)) return;

            if (details is null)
            {
                // The artwork and the identification are saved, so this is a partial success and
                // is worth saying so rather than looking like nothing happened.
                Bind(vm);
                LoadArtwork(cts.Token);

                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase",
                    "The poster was changed, but TMDB did not return the rest of the details. " +
                    "Reopening the film will try again.");
                return;
            }

            var credits = await tmdb.GetCreditsByIdAsync(details.Id, cts.Token);
            if (!ReferenceEquals(Vm, vm)) return;

            vm.Overview = details.Overview ?? "";
            vm.Runtime = details.Runtime;
            vm.ImdbId = details.ImdbId;
            vm.Genres = CreditLine.Genres(details);
            vm.BackdropUrl = string.IsNullOrWhiteSpace(details.BackdropPath) ? null : tmdb.BuildImageUrl(details.BackdropPath!);
            vm.TopCast = CreditLine.Cast(credits);
            vm.KeyCrew = CreditLine.Crew(credits);

            // Cleared before it is asked for again: the number on screen belongs to the film the
            // user has just said this is not, and OMDb may have nothing for the new one.
            vm.ImdbRating = null;
            if (_ratingLookup is not null)
            {
                vm.ImdbRating = await _ratingLookup(vm.ImdbId, vm.LocalId > 0 ? vm.LocalId : null, cts.Token);
                if (!ReferenceEquals(Vm, vm)) return;
            }

            Bind(vm);
            LoadArtwork(cts.Token);
        }

        /// <summary>
        /// The poster to store for a chosen film: a cached file when the app downloads posters, a
        /// TMDB URL when it does not, and null when TMDB has no artwork for this one.
        /// </summary>
        /// <remarks>
        /// The cache filename carries the TMDB id as well as the movie's. The automatic loader
        /// names its file after the movie alone and a download skips a file that already exists, so
        /// without the id a correction would write the old, wrong poster straight back over the new
        /// one and look like it had failed.
        /// </remarks>
        private static async Task<string?> ResolvePosterAsync(
            TmdbService tmdb,
            long movieId,
            TmdbCandidateVm chosen,
            bool download,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(chosen.PosterPath)) return null;

            var url = tmdb.BuildImageUrl(chosen.PosterPath!);
            if (!download) return url;

            // Falls back to the URL when the download fails, so the screen still shows the right
            // poster over the network rather than keeping the wrong one from disk.
            return await tmdb.DownloadForPublic(url, $"{movieId}-{chosen.TmdbId}.jpg", ct) ?? url;
        }

        /// <summary>
        /// Records the choice against the movie row. Failing is reported to the log rather than to
        /// the user, who can see the corrected screen in front of them; all they lose is that it
        /// will have to be corrected again next time.
        /// </summary>
        private async Task SaveMatchAsync(MovieDetailsVm vm, int tmdbId, string? posterPath, string? title, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_dbPath) || vm.LocalId <= 0) return;

            try
            {
                using var conn = Database.Open(_dbPath);
                await MovieMatch.SaveAsync(conn, vm.LocalId, tmdbId, posterPath, title, ct);
            }
            catch (Exception ex)
            {
                AppLog.Write("posters.log", $"could not save tmdb match {tmdbId} for movie {vm.LocalId}: {ex.Message}");
            }
        }
    }
}
