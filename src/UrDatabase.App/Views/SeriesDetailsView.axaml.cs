using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UrDatabase.Models;
using UrDatabase.Services;

namespace UrDatabase.Views
{
    /// <summary>
    /// The series screen: what a programme is, and every episode of it.
    /// </summary>
    /// <remarks>
    /// A sibling of <see cref="MovieDetailsView"/> inside <see cref="MainWindow"/>, shown in the
    /// same place and on the same terms — awaited, and completing when the user leaves.
    ///
    /// It opens from the cache and asks the server afterwards, rather than the other way round.
    /// A show whose episodes were listed last week opens instantly and opens at all with the
    /// server switched off, and the request that follows only ever adds to what is already there.
    /// The alternative — a spinner over a list the app already had — would make a laptop away from
    /// home strictly worse than a laptop with no server configured at all.
    /// </remarks>
    public partial class SeriesDetailsView : UserControl
    {
        public SeriesDetailsVm? Vm { get; private set; }

        /// <summary>The seasons across the top. Bound; rebuilt whenever the episode list changes.</summary>
        public ObservableCollection<SeasonGroup> Seasons { get; } = new();

        /// <summary>The episodes of the selected season.</summary>
        public ObservableCollection<EpisodeRow> Episodes { get; } = new();

        private SeriesRefreshSession? _session;
        private TaskCompletionSource? _closed;

        private SeriesLoader? _loader;
        private LocalSeriesLoader? _localLoader;
        private JellyfinClient? _jellyfin;
        private Func<string?, CancellationToken, Task<double?>>? _loadRating;
        private Action? _cancelEnrichment;

        /// <summary>
        /// Which season is on screen, by name. Kept rather than an index because a refresh from
        /// the server can add a season, and an index would then be pointing at a different one.
        /// </summary>
        private string? _selectedSeason;

        /// <summary>
        /// The season to land on the first time the list is built, when the screen was opened from
        /// something that knows which one it wants — an episode in the Continue watching row.
        /// </summary>
        /// <remarks>
        /// Only ever consulted before the user has chosen a season, so the refresh that follows
        /// the cache does not drag them back to it. Null is the ordinary case and means the first
        /// season, as it always did.
        /// </remarks>
        private int? _openAtSeason;

        /// <summary>
        /// The window's own lifetime, for work that has to outlive this screen: an episode goes on
        /// playing after the viewer has gone back to the library, and progress reporting has to go
        /// on with it. Bounded by the app closing, which sends a last stop.
        /// </summary>
        private CancellationToken _appLifetime = CancellationToken.None;

        public SeriesDetailsView()
        {
            InitializeComponent();
            DataContext = this;
        }

        /// <summary>True while a series is on screen.</summary>
        public bool IsShowing => _closed is not null;

        /// <summary>
        /// Shows a series and returns when the user leaves it.
        /// </summary>
        /// <param name="loader">
        /// Where the seasons and episodes come from. Null shows the series with no episode list at
        /// all, which is what the XAML designer gets.
        /// </param>
        /// <param name="appLifetime">
        /// The window's lifetime, not this screen's. Progress reporting for an episode is given
        /// this so that going back to the library does not stop it mid-episode.
        /// </param>
        /// <param name="openAtSeason">
        /// The season to open on, when the caller knows which one is wanted.
        /// </param>
        /// <param name="loadRating">Retries the IMDb lookup during a manual refresh.</param>
        /// <param name="localLoader">
        /// Where a programme on this disk reads its episodes from. Consulted only when
        /// <see cref="SeriesDetailsVm.IsLocal"/>; a server programme ignores it.
        /// </param>
        public Task ShowAsync(
            SeriesDetailsVm vm,
            SeriesLoader? loader = null,
            JellyfinClient? jellyfin = null,
            CancellationToken appLifetime = default,
            int? openAtSeason = null,
            Func<string?, CancellationToken, Task<double?>>? loadRating = null,
            Action? cancelEnrichment = null,
            LocalSeriesLoader? localLoader = null)
        {
            if (_closed is not null) Close();

            Vm = vm;
            _loader = loader;
            _localLoader = localLoader;
            _jellyfin = jellyfin;
            _loadRating = loadRating;
            _cancelEnrichment = cancelEnrichment;
            _selectedSeason = null;
            _openAtSeason = openAtSeason;
            _appLifetime = appLifetime;

            var session = _session = new SeriesRefreshSession(appLifetime);
            _closed = new TaskCompletionSource();
            LoadNotice.IsVisible = false;

            BindMetadata(vm);
            Seasons.Clear();
            Episodes.Clear();
            SeasonRow.IsVisible = false;
            SetEpisodeNote("Looking for episodes…");
            NoEpisodesText.IsVisible = false;
            SetRefreshNote("");
            RefreshButton.Content = "Refresh";
            IsVisible = true;

            // Focus has to land inside this screen or Escape and the arrow keys keep going to the
            // library underneath, which is still there and still focusable.
            BackButton.Focus();

            _ = RunPageWorkAsync(session, ct => Task.WhenAll(
                LoadArtworkAsync(vm, session, ct),
                LoadEpisodesAsync(vm, loader, session, ct)), manual: false);

            return _closed.Task;
        }

        /// <summary>
        /// Dismisses the screen and releases whoever is awaiting <see cref="ShowAsync"/>.
        /// Safe to call when nothing is open.
        /// </summary>
        public void Close()
        {
            if (_closed is null) return;

            _cancelEnrichment?.Invoke();
            _cancelEnrichment = null;
            _session?.Dispose();
            _session = null;
            RefreshButton.IsEnabled = false;
            IsVisible = false;

            BackdropImage.Source = null;
            PosterImage.Source = null;

            Seasons.Clear();
            Episodes.Clear();

            var closed = _closed;
            _closed = null;
            Vm = null;
            _loadRating = null;

            closed.TrySetResult();
        }

        public void ReportLoadNotice(SeriesDetailsVm vm, string message)
        {
            if (!ReferenceEquals(Vm, vm)) return;
            LoadNotice.Text = message;
            LoadNotice.IsVisible = message.Length > 0;
        }

        public void UpdateRating(SeriesDetailsVm vm, double? rating)
        {
            if (!ReferenceEquals(Vm, vm)) return;
            vm.ImdbRating = rating;
            FactsList.ItemsSource = DetailFacts.For(vm);
        }

        private void BindMetadata(SeriesDetailsVm vm)
        {
            TitleText.Text = vm.Title;
            FactsList.ItemsSource = DetailFacts.For(vm);

            // The server is credited only for what it supplied. A programme on this disk was named
            // by the scan from its own folders, and saying otherwise would send somebody looking
            // for a server setting that has nothing to do with it.
            AttributionText.Text = LocalSeriesPage.Attribution(vm);

            GenresText.Text = vm.Genres ?? "";
            GenresText.IsVisible = !string.IsNullOrWhiteSpace(vm.Genres);

            OverviewText.Text = string.IsNullOrWhiteSpace(vm.Overview)
                ? LocalSeriesPage.MissingOverview(vm)
                : vm.Overview;

            var cast = vm.TopCast
                .Select(CreditLine.SplitCast)
                .Where(c => c.Name.Length > 0)
                .Select(c => new CreditEntry { Primary = c.Name, Secondary = c.Character })
                .ToList();

            CastList.ItemsSource = cast;
            NoCastText.Text = vm.IsLocal
                ? LocalSeriesPage.MissingCast(vm)
                : MissingMetadata.CreditsNotice(isRemote: true, tmdbConfigured: false);
            NoCastText.IsVisible = cast.Count == 0;
        }

        /// <summary>
        /// Fills the list from the cache, then from the server. Both halves land here rather than
        /// in <see cref="SeriesLoader"/> because only this screen knows whether it is still the
        /// screen the answer was asked for. A programme on this disk has only the first half: the
        /// catalogue is the record, and there is nothing further to ask.
        /// </summary>
        private async Task LoadEpisodesAsync(
            SeriesDetailsVm vm, SeriesLoader? loader, SeriesRefreshSession session, CancellationToken ct)
        {
            if (vm.IsLocal)
            {
                await ShowLocalEpisodesAsync(vm, session, ct);
                return;
            }

            if (loader is null)
            {
                if (IsCurrent(session)) ShowEpisodes(SeriesContents.Empty, cached: false);
                return;
            }

            var seriesId = vm.RemoteId;
            var cached = await Task.Run(() => loader.LoadCached(seriesId), ct);
            if (!IsCurrent(session)) return;
            if (!cached.IsEmpty) ShowEpisodes(cached, cached: true);

            var fresh = await loader.RefreshAsync(seriesId, ct);
            if (!IsCurrent(session)) return;
            ShowEpisodes(fresh, cached: false);
        }

        public Task RefreshAsync()
        {
            var session = _session;
            var vm = Vm;
            var loader = _loader;
            var client = _jellyfin;
            var loadRating = _loadRating;
            if (session is null || vm is null || !IsCurrent(session) || session.IsBusy) return Task.CompletedTask;
            if (vm.IsLocal) return RefreshLocalAsync(vm, session);
            if (loader is null || client is null)
            {
                SetRefreshNote("No Jellyfin server is configured, so this programme cannot be refreshed.");
                return Task.CompletedTask;
            }

            _cancelEnrichment?.Invoke();
            _cancelEnrichment = null;
            LoadNotice.IsVisible = false;
            return RunPageWorkAsync(session, async ct =>
            {
                var result = await SeriesDetailsRefresh.LoadAsync(vm, loader, client, loadRating, ct);
                if (!IsCurrent(session)) return;
                Vm = result.Details;
                BindMetadata(result.Details);
                ShowEpisodes(result.Episodes, cached: false);
                var artworkLoaded = await LoadArtworkAsync(result.Details, session, ct);
                if (!IsCurrent(session)) return;
                SetRefreshNote(string.Join(" ", new[]
                {
                    "Details refreshed.",
                    result.Notice,
                    artworkLoaded ? null : "Some artwork is unavailable; previous images kept."
                }.Where(note => note is not null)));
            }, manual: true);
        }

        private async Task RunPageWorkAsync(
            SeriesRefreshSession session, Func<CancellationToken, Task> load, bool manual)
        {
            if (!IsCurrent(session) || session.IsBusy) return;
            RefreshButton.IsEnabled = false;
            if (manual)
            {
                RefreshButton.Content = "Refreshing…";
                SetRefreshNote("Refreshing details, episodes and artwork…");
            }

            try
            {
                await session.RunAsync(load);
            }
            catch (OperationCanceledException)
            {
                if (IsCurrent(session) && manual)
                    SetRefreshNote("Refresh was interrupted. Try again.");
            }
            catch (Exception ex)
            {
                if (!IsCurrent(session)) return;
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"could not refresh the programme: {ex.Message}"));
                if (manual)
                    SetRefreshNote("Could not refresh this programme. Showing previous details; try again.");
                else if (Seasons.Count == 0)
                    SetEpisodeNote("The episodes of this programme could not be listed.");
            }
            finally
            {
                if (IsCurrent(session))
                {
                    RefreshButton.IsEnabled = true;
                    RefreshButton.Content = "Refresh";
                }
            }
        }

        private bool IsCurrent(SeriesRefreshSession session) =>
            ReferenceEquals(_session, session) && session.IsCurrent && IsShowing;

        /// <summary>
        /// Reads a local programme's episodes off the catalogue and lists them, then — for a
        /// programme TMDB has identified — asks about any season not described yet and lists them
        /// again with TMDB's names, plots and lengths. Off the UI thread, and checked against the
        /// session after each answer like everything else this screen waits for.
        /// </summary>
        /// <param name="force">Asks about every season, not only the undescribed ones. A manual refresh.</param>
        private async Task ShowLocalEpisodesAsync(
            SeriesDetailsVm vm, SeriesRefreshSession session, CancellationToken ct, bool force = false)
        {
            var loader = _localLoader;
            if (loader is null)
            {
                if (IsCurrent(session)) ShowGroups(Array.Empty<SeasonGroup>(), "");
                return;
            }

            var episodes = await Task.Run(() => loader.Load(vm.LocalId), ct);
            if (!IsCurrent(session)) return;
            ShowLocalEpisodes(episodes);

            if (!vm.IsIdentified) return;

            try
            {
                var described = await Task.Run(() => loader.RefreshEpisodesAsync(vm.LocalId, force, ct), ct);
                if (described is null || !IsCurrent(session)) return;
                ShowLocalEpisodes(described);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The episodes are already listed under the names their files give them. What
                // failed is the improvement, and the seasons it did not reach are asked about
                // again the next time the programme is opened.
                AppLog.Write("posters.log", $"seriesId={vm.LocalId} episode details: {ex}");
                if (force && IsCurrent(session))
                    SetRefreshNote("Could not fetch episode details from TMDB. Showing what the catalogue has; try again.");
            }
        }

        private void ShowLocalEpisodes(IReadOnlyList<LocalEpisode> episodes)
        {
            var groups = SeriesGrouping.GroupLocal(episodes);
            var missing = episodes.Count(e => !e.IsAvailable);
            var described = SeriesGrouping.Describe(groups);

            ShowGroups(groups, missing == 0
                ? $"{described} · on this computer. Play opens an episode in your video player."
                : $"{described} · {missing.ToString(System.Globalization.CultureInfo.InvariantCulture)} not found by the last scan.");
        }

        /// <summary>
        /// Refreshing a local programme re-reads what the catalogue says, which changes whenever a
        /// scan has run since the screen opened, and asks TMDB about every season again when the
        /// programme has been identified. It never scans: that is the library's job, and a scan
        /// started from here would walk every watch folder to answer a question about one show.
        /// </summary>
        private Task RefreshLocalAsync(SeriesDetailsVm vm, SeriesRefreshSession session)
        {
            var loadRating = _loadRating;

            // The opening's IMDb lookup is superseded by the one below, as on a server programme.
            _cancelEnrichment?.Invoke();
            _cancelEnrichment = null;
            LoadNotice.IsVisible = false;

            return RunPageWorkAsync(session, async ct =>
            {
                SetRefreshNote("");
                await ShowLocalEpisodesAsync(vm, session, ct, force: true);

                if (loadRating is not null && !string.IsNullOrWhiteSpace(vm.ImdbId))
                {
                    var rating = await loadRating(vm.ImdbId, ct);
                    if (IsCurrent(session)) UpdateRating(vm, rating);
                }

                if (IsCurrent(session) && !RefreshNote.IsVisible)
                    SetRefreshNote(vm.IsIdentified
                        ? "Episodes re-read from the catalogue and described again by TMDB."
                        : "Episodes re-read from the catalogue.");
            }, manual: true);
        }

        private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshAsync();

        /// <summary>
        /// Puts one answer on screen, keeping the season the user was reading selected when it
        /// still exists. A refresh that silently jumped back to season one would undo a click the
        /// user made while it was in flight.
        /// </summary>
        private void ShowEpisodes(SeriesContents contents, bool cached)
        {
            var groups = SeriesGrouping.Group(contents.Seasons, contents.Episodes);
            var empty = groups.Sum(g => g.Episodes.Count) == 0;

            NoEpisodesText.Text = _jellyfin is null
                ? "No Jellyfin server is configured, so this programme has no episodes to list."
                : "The server listed no episodes for this programme.";

            var described = SeriesGrouping.Describe(groups);

            ShowGroups(groups, empty
                ? ""
                : cached
                    ? $"{described} · from the last sync; checking the server…"
                    : $"{described} · Play opens an episode in VLC or IINA.");
        }

        /// <summary>
        /// Puts a list of seasons on screen, wherever it came from, keeping the season the user was
        /// reading selected when it still exists.
        /// </summary>
        private void ShowGroups(IReadOnlyList<SeasonGroup> groups, string note)
        {
            Seasons.Clear();
            foreach (var group in groups) Seasons.Add(group);

            // A single season is not offered as a choice: a row of one chip is a label pretending
            // to be a control, and its episodes are already the whole list below it.
            SeasonRow.IsVisible = Seasons.Count > 1;

            var selected = SeriesGrouping.SeasonToShow(groups, _selectedSeason, _openAtSeason);

            SelectSeason(selected);

            var empty = groups.Sum(g => g.Episodes.Count) == 0;

            NoEpisodesText.IsVisible = empty;
            if (empty && Vm?.IsLocal == true)
                NoEpisodesText.Text = "The catalogue holds no episode of this programme that a scan has found.";

            SetEpisodeNote(empty ? "" : note);
        }

        private void SelectSeason(SeasonGroup? season)
        {
            _selectedSeason = season?.Name;

            foreach (var group in Seasons)
                group.IsSelected = ReferenceEquals(group, season);

            // The chips carry their own selected state and are not observable, so the row is
            // rebuilt to make the tick appear. Cheap: this is at most a dozen items.
            SeasonChipsList.ItemsSource = null;
            SeasonChipsList.ItemsSource = Seasons;

            Episodes.Clear();
            if (season is null) return;

            foreach (var episode in season.Episodes) Episodes.Add(episode);
        }

        private void SeasonChip_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Avalonia.Controls.Primitives.ToggleButton tb && tb.DataContext is SeasonGroup season)
                SelectSeason(season);
        }

        /// <summary>
        /// Plays one episode. The stream URL is built here, at the moment it is needed, rather than
        /// carried on every row: it contains an access token, and a list of twenty-four of them is
        /// twenty-four credentials sitting in a bound collection.
        /// </summary>
        /// <remarks>
        /// Followed afterwards exactly as a film is. Nothing in that path was ever about films —
        /// the report is an item id, a position and a state — so an episode watched here appears
        /// in Continue watching on the television, and one watched on the television appears here.
        /// Without this an episode played from this app was invisible to every other client, which
        /// is precisely the complaint the film half of the feature was built to answer.
        /// </remarks>
        private async void Episode_Click(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed) return;
            if ((sender as Control)?.DataContext is not EpisodeRow episode) return;

            if (Vm?.IsLocal == true)
            {
                await PlayLocalEpisodeAsync(episode);
                return;
            }

            if (string.IsNullOrWhiteSpace(episode.ItemId)) return;

            if (_jellyfin is null)
            {
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", "No Jellyfin server is configured.");
                return;
            }

            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_session?.Token ?? default);
                deadline.CancelAfter(TimeSpan.FromSeconds(12));

                // The first point in this screen's life where the network is strictly needed: a
                // stream URL is only valid with a token, and the token comes from a sign-in.
                await _jellyfin.ConnectAsync(deadline.Token);

                // The same door the Continue watching row plays through, so neither can start an
                // episode without following it. Not awaited: it lasts as long as the episode, and
                // the viewer is going back to the list. Given the window's lifetime rather than
                // this screen's, so leaving the programme does not stop the reporting — and so
                // closing the app does, with a last word.
                //
                // From the beginning, deliberately: this list is where somebody picks an episode
                // out of a season, and picking one is asking to watch it rather than to carry on
                // with it. Carrying on is what the row above the genres is for, and it passes the
                // position.
                _ = StreamPlayback.Start(_jellyfin, episode.ItemId, startTicks: 0, appLifetime: _appLifetime);
            }
            catch (OperationCanceledException)
            {
                // The screen was closed, or the window is.
            }
            catch (MediaPlayerNotFoundException ex)
            {
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", ex.Message);
            }
            catch (JellyfinException ex)
            {
                await MessageBoxWindow.ShowAsync(
                    Owner(),
                    "UrDatabase",
                    $"{ex.Message} This episode will play again once the server is back.");
            }
            catch (Exception ex)
            {
                // Deliberately not the URL, which contains an access token.
                AppLog.Write("jellyfin.log", $"playback failed: {JellyfinClient.Redact(ex.Message)}");
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", $"Could not start playback:{Environment.NewLine}{ex.Message}");
            }
        }

        /// <summary>
        /// Plays an episode on this disk, through the same door a local film's Play goes through:
        /// the path is checked against the video types the scanner records and against the disk,
        /// then handed to the operating system's player. Nothing streams, so nothing is reported.
        /// </summary>
        private async Task PlayLocalEpisodeAsync(EpisodeRow episode)
        {
            var refusal = episode.IsAvailable
                ? PlayTargetResolver.DescribeLinkRefusal(episode.FilePath)
                : "The last scan could not find this episode. Is its drive connected?";

            if (refusal is not null)
            {
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", refusal);
                return;
            }

            try
            {
                FileLauncher.Open(episode.FilePath!);
            }
            catch (Exception ex)
            {
                AppLog.Write("app.log", $"could not open an episode: {ex.Message}");
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", $"Could not start playback:{Environment.NewLine}{ex.Message}");
            }
        }

        private async Task<bool> LoadArtworkAsync(
            SeriesDetailsVm vm, SeriesRefreshSession session, CancellationToken ct)
        {
            var poster = await ImageLoader.LoadAsync(vm.PosterPath, ct);
            if (!IsCurrent(session) || !ReferenceEquals(Vm, vm)) return false;
            if (poster is not null) PosterImage.Source = poster;

            var backdrop = await ImageLoader.LoadAsync(vm.BackdropUrl, ct);
            if (!IsCurrent(session) || !ReferenceEquals(Vm, vm)) return false;
            if (backdrop is not null) BackdropImage.Source = backdrop;
            return (poster is not null || string.IsNullOrWhiteSpace(vm.PosterPath))
                && (backdrop is not null || string.IsNullOrWhiteSpace(vm.BackdropUrl));
        }

        private void SetRefreshNote(string text)
        {
            RefreshNote.Text = text;
            RefreshNote.IsVisible = text.Length > 0;
        }

        private void SetEpisodeNote(string text)
        {
            EpisodeNote.Text = text;
            EpisodeNote.IsVisible = text.Length > 0;
        }

        /// <summary>
        /// Escape leaves the programme, which a screen occupying the whole window has to honour:
        /// there is no title bar close button on it.
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

        private Window? Owner() => TopLevel.GetTopLevel(this) as Window;
    }
}
