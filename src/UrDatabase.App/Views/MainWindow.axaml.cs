using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UrDatabase.Models;
using UrDatabase.Services;

namespace UrDatabase.Views
{
    public partial class MainWindow : Window
    {
        private AppConfig _config = new AppConfig();
        public ObservableCollection<UiMovie> SingleGenreItems { get; } = new();

        private string _dbPath = "";

        /// <summary>
        /// The genre row across the top. Each entry carries its own count, which a bare list of
        /// names could not: a row that says a library has a Western bucket without saying whether
        /// it holds two films or two hundred is telling you the least useful half of the fact.
        /// </summary>
        public ObservableCollection<GenreChip> GenreChips { get; } = new();

        /// <summary>
        /// Where films are, as opposed to what they are. Empty unless the library actually draws
        /// on more than one place, so a single-source install grows no controls it cannot use.
        /// </summary>
        public ObservableCollection<SourceChip> SourceChips { get; } = new();

        /// <summary>
        /// What is being looked at: everything, the films, or the television. Empty unless the
        /// library actually holds both, on exactly the same terms as the row above — an install
        /// with no television, which is every install this app had until now, grows nothing.
        /// </summary>
        public ObservableCollection<KindChip> KindChips { get; } = new();

        private LibrarySource _source = LibrarySource.Everywhere;
        private LibraryKind _kind = LibraryKind.Everything;
        public string? SelectedGenre { get; set; } = LibraryGrouping.AllGenres;
        public ObservableCollection<GenreGroup> VisibleGroups { get; } = new();
        public ObservableCollection<UiMovie> FlatResults { get; } = new();

        private IReadOnlyList<UiMovie> _allMovies = Array.Empty<UiMovie>();

        /// <summary>
        /// The library as the window is currently showing it: everything, or only what is on this
        /// machine, or only what is on the server — and, crossed with that, only the films or only
        /// the television. Genre counts, the shelves and the search results are all built from
        /// this rather than from <c>_allMovies</c>, so a filtered view is filtered consistently
        /// rather than in the one place somebody remembered.
        /// </summary>
        private IReadOnlyList<UiMovie> VisibleMovies =>
            LibraryFilter.Apply(LibraryFilter.Apply(_allMovies, _source), _kind);

        /// <summary>The server's whole library as cards, unfiltered. Empty when Jellyfin is off.</summary>
        private List<UiMovie> _remoteMovies = new();

        /// <summary>
        /// The same films keyed by item id, holding the fields a card does not show. Jellyfin
        /// already supplies the overview, runtime, rating and IMDb id, so opening a server film
        /// needs no request at all — which is what makes it work with no TMDB key configured.
        /// </summary>
        private Dictionary<string, JellyfinMovie> _remoteById = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The server's television, keyed the same way and for the same reason.</summary>
        private Dictionary<string, JellyfinSeries> _remoteSeriesById = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Where a series' seasons and episodes come from. Rebuilt with the configuration, because
        /// both the catalogue and the server can move. Null until <see cref="ApplyConfig"/> runs.
        /// </summary>
        private SeriesLoader? _series;

        /// <summary>
        /// What the last sync said the viewer is part way through. Read from the cache with the
        /// library, so the Continue watching row is on screen before the server has been asked
        /// anything and stays there when it cannot be asked at all.
        /// </summary>
        private IReadOnlyList<JellyfinResumeItem> _resume = Array.Empty<JellyfinResumeItem>();

        private PosterAutoLoader? _posterLoader;
        private int _posterFailuresReported;

        /// <summary>Rebuilt whenever the configuration changes; never null once the window exists.</summary>
        private ImdbRatingService _ratings = null!;

        /// <summary>Rebuilt with the configuration, because the catalogue can move. Never null once the window exists.</summary>
        private LibraryLoader _library = null!;

        /// <summary>
        /// Every library read goes through here, so only one is ever in flight and only the newest
        /// one reaches the screen. Built once: it reads <see cref="_library"/> at the moment a
        /// request runs, so a change of database needs no new coordinator.
        /// </summary>
        private readonly SearchCoordinator<LibraryView> _searchLoop;

        /// <summary>
        /// The last read that reached the screen. Kept because the source row filters what is
        /// already here rather than asking the database again: changing where you are looking
        /// does not change what the query matched.
        /// </summary>
        private LibraryView _view = LibraryView.Empty;

        /// <summary>Null unless a server is configured, which is what keeps this feature off by default.</summary>
        private JellyfinClient? _jellyfin;

        private readonly CancellationTokenSource _cts = new();
        private bool _scanning;
        private bool _syncing;

        /// <summary>
        /// The release the banner is offering, or null when there is nothing to offer — which is
        /// every launch on the newest build, every launch with the check switched off, and every
        /// launch that could not reach GitHub.
        /// </summary>
        private AvailableUpdate? _update;

        /// <summary>Non-null only while a build is being fetched, which is what makes one button do Cancel too.</summary>
        private CancellationTokenSource? _updateCts;

        /// <summary>Where the fetched build landed, so pressing the button again opens it rather than fetching it twice.</summary>
        private string? _updateDownloadPath;

        /// <summary>
        /// Set when a fetch failed. The button then offers the website, because a failed download
        /// that leaves somebody with nothing to press is a dead end, and the website is where they
        /// would have gone had the app never offered at all.
        /// </summary>
        private bool _updateFetchFailed;

        /// <summary>
        /// Set once the poster loader has been given its chance to finish, so the close that
        /// follows the drain is not deferred a second time.
        /// </summary>
        private bool _postersDrained;

        public MainWindow()
        {
            InitializeComponent();

            ApplyConfig();

            _searchLoop = new SearchCoordinator<LibraryView>(
                run: (query, ct) => _library.LoadAsync(query, _remoteMovies, ct),
                apply: ApplyLibrary,
                lifetime: _cts.Token,
                onError: ReportLibraryFailure);

            // Runs after the poster drain in OnClosing, and has to: cancelling this first would
            // cut short the very fetches the drain is there to let finish.
            Closed += (_, __) => { _cts.Cancel(); _searchLoop.Dispose(); _posterLoader?.Dispose(); _ratings.Dispose(); _jellyfin?.Dispose(); };

            DataContext = this;

            WireSearchShortcut();

            LoadRemoteCache();

            // Not awaited, because a constructor cannot be. Reading a large catalogue used to
            // happen here, on the UI thread, so the window did not appear until it had finished.
            //
            // Nothing chooses a panel until the read lands: the grouped view starts visible and
            // empty, which shows nothing for an instant, whereas asking ShowAllGenres now would
            // see an empty library and flash the "there is nothing here yet" invitation at
            // somebody whose library is about to appear.
            _ = _searchLoop.RefreshAsync();

            // Last of the four, because each of them writes the status line and a configuration
            // the app could not understand is the more important thing for it to be saying.
            ReportUnknownSettings();

            // The window is already painted from the cache by the time this runs, so a slow or
            // absent server delays nothing anybody is looking at.
            if (_jellyfin is not null)
                Dispatcher.UIThread.Post(() => _ = SyncJellyfinAsync(announceFailure: false));

            // Same reasoning, and the same posture: the check happens behind an already usable
            // window, and an unreachable GitHub costs a background task and nothing on screen.
            // Switched off in configuration it does not happen at all, rather than happening and
            // having its answer hidden — an install kept off the network stays off it.
            if (_config.CheckForUpdates)
                Dispatcher.UIThread.Post(() => _ = CheckForUpdateAsync());
        }

        /// <summary>
        /// A close waits for the posters already being fetched instead of walking away from
        /// them. Each one is a TMDB request that has been paid for; abandoning it at the last
        /// moment loses the answer and asks the same question again on the next launch.
        ///
        /// The window is hidden first, so closing still looks instant, and the wait is bounded
        /// by <see cref="PosterAutoLoader.DefaultStopTimeout"/> — a fetch that will not finish
        /// is cancelled rather than allowed to hold the app open.
        ///
        /// Only a window being closed is deferred. An application or OS shutdown is not ours to
        /// postpone: cancelling one to buy two seconds is how an app comes to look like it
        /// refuses to quit.
        /// </summary>
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);

            if (e.Cancel || _postersDrained) return;
            if (e.CloseReason != WindowCloseReason.WindowClosing) return;

            var loader = _posterLoader;
            if (loader is null) return;

            e.Cancel = true;
            Hide();
            _ = DrainPostersThenCloseAsync(loader);
        }

        /// <summary>
        /// Closes for real once the loader has stopped. Every path through this ends in a close:
        /// a window that stayed open because a poster misbehaved would be a worse bug than the
        /// one being fixed.
        /// </summary>
        private async Task DrainPostersThenCloseAsync(PosterAutoLoader loader)
        {
            try
            {
                if (!await loader.StopAsync())
                    AppLog.Write("posters.log", "closed with poster fetches still running; they were cancelled.");
            }
            catch (Exception ex)
            {
                AppLog.Write("posters.log", $"draining posters on close: {ex}");
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _postersDrained = true;
                    Close();
                });
            }
        }

        /// <summary>
        /// Reads the configuration and rebuilds everything that depends on it. Called once at
        /// startup and again whenever the setup screen saves, so that changing a server or a key
        /// takes effect immediately rather than at the next launch — the alternative, a dialog
        /// asking the user to restart, would be the app admitting it had not really applied
        /// what it had just accepted.
        ///
        /// Each service is disposed before being replaced: they own an HTTP client apiece.
        /// </summary>
        private void ApplyConfig()
        {
            try { _config = AppConfig.Load(); } catch { _config = new AppConfig(); }
            _dbPath = _config.DatabasePath;

            // Constructed eagerly but idle: no request is made until a movie is opened, and none
            // at all when no OMDb key is available.
            _ratings?.Dispose();
            _ratings = new ImdbRatingService(new OmdbService(_config.OmdbApiKey), ownsLookup: true);

            // Nothing is constructed, and no database is touched, when no server is configured.
            _jellyfin?.Dispose();
            _jellyfin = _config.Jellyfin?.IsConfigured == true
                ? new JellyfinClient(
                    _config.Jellyfin,
                    JellyfinDeviceId.Resolve(),
                    version: typeof(MainWindow).Assembly.GetName().Version?.ToString())
                : null;

            _posterLoader?.Dispose();
            _posterFailuresReported = 0;
            _posterLoader = new PosterAutoLoader(_config, _dbPath, maxConcurrency: 4, onFailure: ReportPosterFailure);

            _library = new LibraryLoader(
                new MovieRepository(_dbPath),
                onQueryFailed: ex => AppLog.Write("startup.log", $"LoadMovies failed: {ex}"));

            _series = new SeriesLoader(
                _dbPath,
                _jellyfin,
                onFailure: message => Dispatcher.UIThread.Post(() => SetStatus(message)));

            if (JellyfinButton is not null) JellyfinButton.IsVisible = _jellyfin is not null;
        }

        /// <summary>
        /// Reads the last synced server library out of SQLite. Only ever called when a server is
        /// configured, so an install without one neither opens nor creates a database it would
        /// not otherwise have touched.
        /// </summary>
        private void LoadRemoteCache()
        {
            _remoteMovies = new List<UiMovie>();
            _remoteById = new Dictionary<string, JellyfinMovie>(StringComparer.OrdinalIgnoreCase);
            _remoteSeriesById = new Dictionary<string, JellyfinSeries>(StringComparer.OrdinalIgnoreCase);
            _resume = Array.Empty<JellyfinResumeItem>();

            if (_jellyfin is null) return;

            try
            {
                using var conn = Database.Open(_dbPath);
                var cached = JellyfinCache.Load(conn);
                var series = JellyfinCache.LoadSeries(conn);

                foreach (var movie in cached) _remoteById[movie.ItemId] = movie;
                foreach (var show in series) _remoteSeriesById[show.ItemId] = show;

                // One list, films and television together, because the library below is one wall.
                // What keeps them distinguishable is the card, not the collection.
                _remoteMovies = JellyfinLibrary
                    .ToUiMovies(cached, m => _jellyfin.BuildPrimaryImageUrl(m.ItemId, m.ImageTag))
                    .Concat(JellyfinLibrary.ToUiSeriesList(series, s => _jellyfin.BuildPrimaryImageUrl(s.ItemId, s.ImageTag)))
                    .ToList();

                _resume = JellyfinResumeCache.Load(conn);
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", $"could not read the cached server library: {ex.Message}");
            }
        }

        /// <summary>
        /// Puts one settled library read on screen. Called by <see cref="_searchLoop"/>, on the UI
        /// thread, and only ever for the newest request — a search for "ma" that finished after
        /// the search for "matrix" never gets here.
        /// </summary>
        private void ApplyLibrary(string? query, LibraryView view)
        {
            _view = view;
            _allMovies = view.All;

            SetStatus(view.Status);
            WarmPosters(_allMovies);

            // searching → flat view
            if (view.IsSearch)
            {
                ShowSearchResults();
                return;
            }

            // not searching → grouped view
            BuildGenres();

            // A genre that no longer exists — because the source changed while a search was up,
            // or because the library did — would otherwise stay selected, match no shelf and
            // render a blank page that looks like the library having emptied itself.
            if (!GenreChips.Any(c => string.Equals(c.Name, SelectedGenre, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedGenre = LibraryGrouping.AllGenres;
                BuildGenres();
            }

            RebuildGroups();
            ShowAllGenres();
        }

        /// <summary>
        /// The search results, narrowed to wherever the user is currently looking.
        /// </summary>
        /// <remarks>
        /// The one place the flat list is built, deliberately. A search landing and a source being
        /// picked are the same question — "these films, from this place" — and having each answer
        /// it separately is precisely how they came to disagree: clicking a source while results
        /// were up went off to rebuild the shelves instead and threw the search away.
        ///
        /// The source row is rebuilt from the results rather than left describing the whole
        /// library, so the row and the list below it always agree, and a place these results do
        /// not reach is not offered at all. Offering it would let a click empty the page with
        /// nothing left on screen to say how to get back.
        /// </remarks>
        private void ShowSearchResults()
        {
            BuildSources();

            FlatResults.Clear();

            // Grouped on Key rather than Id: every server film carries id 0, so grouping on
            // the local id would collapse the whole remote half of the results into one card.
            foreach (var m in VisibleMovies
                    .GroupBy(x => x.Key)
                    .Select(g => g.First())
                    .OrderByDescending(x => x.Year ?? 0)
                    .ThenBy(x => x.Title))
            {
                FlatResults.Add(m);
            }

            SearchCountText.Text = LibraryGrouping.CountLabel(FlatResults);

            // A search that found nothing has to say so. Silence reads as a broken search box.
            NoResultsText.IsVisible = FlatResults.Count == 0;
            NoResultsText.Text = $"Nothing in the library matches \u201c{_view.Query}\u201d.";

            // The genre row deliberately keeps describing the whole library rather than the
            // current results, so it does not rearrange itself under the cursor between one
            // keystroke and the next.
            ShowSearch();
        }

        /// <summary>
        /// A library read that failed outright, as opposed to one that found nothing. The loader
        /// already handles and logs a query SQLite refused; this is for anything left over, and it
        /// exists mainly so that nothing thrown on the search path escapes into an event handler,
        /// where it would end the process rather than the search.
        /// </summary>
        private void ReportLibraryFailure(Exception ex)
        {
            AppLog.Write("startup.log", $"the library could not be updated: {ex}");
            SetStatus($"Could not read the library: {ex.Message}");
        }

        private void SetStatus(string message)
        {
            Title = $"UrDatabase — {message}";
            if (StatusText is not null) StatusText.Text = message;
        }

        /// <summary>
        /// Says so when the configuration file contains a key this app does not have. The log
        /// alone would not do: the symptom is an empty library or an absent server, and nobody
        /// opens <c>startup.log</c> to explain something that looks like it is working. The
        /// status line is where the library already accounts for itself, so it is where the
        /// reason it is empty belongs.
        /// </summary>
        private void ReportUnknownSettings()
        {
            var message = ConfigDiagnostics.Report(_config);
            if (message is not null) SetStatus(message);
        }

        /// <summary>
        /// Shows or hides the one moving thing in the window. The accent is spent here because
        /// something is genuinely running; a progress bar that is always on screen is furniture.
        /// </summary>
        private void SetBusy(bool busy)
        {
            if (LiveTrack is not null) LiveTrack.IsVisible = busy;
        }

        /// <summary>
        /// Prints the shortcut on the search field, and makes it work. The app has always been
        /// able to focus search from the keyboard in the sense that Tab reaches it; this is the
        /// shortcut people actually try, and nothing else in the window would ever mention it.
        /// </summary>
        private void WireSearchShortcut()
        {
            var mac = OperatingSystem.IsMacOS();

            SearchKeycapText.Text = mac ? "\u2318F" : "Ctrl F";

            var gesture = new KeyGesture(Key.F, mac ? KeyModifiers.Meta : KeyModifiers.Control);

            KeyBindings.Add(new KeyBinding
            {
                Gesture = gesture,
                Command = new FocusSearchCommand(this)
            });
        }

        /// <summary>
        /// Focuses and selects the search box. A command rather than a handler because
        /// <see cref="KeyBinding"/> takes one, and selecting the existing text means the
        /// shortcut starts a new search rather than appending to the last one.
        /// </summary>
        private sealed class FocusSearchCommand : System.Windows.Input.ICommand
        {
            private readonly MainWindow _window;

            public FocusSearchCommand(MainWindow window) => _window = window;

            public event EventHandler? CanExecuteChanged { add { } remove { } }

            // Never while a film or a programme is open: the search box is behind the details
            // screen, and focusing something the user cannot see is worse than doing nothing.
            public bool CanExecute(object? parameter) =>
                !_window.DetailsView.IsShowing && !_window.SeriesView.IsShowing;

            public void Execute(object? parameter)
            {
                if (!CanExecute(parameter)) return;

                _window.SearchBox.Focus();
                _window.SearchBox.SelectAll();
            }
        }

        private void BuildGenres()
        {
            BuildSources();

            GenreChips.Clear();
            foreach (var chip in LibraryGrouping.BuildGenreChips(VisibleMovies))
            {
                chip.IsSelected = string.Equals(chip.Name, SelectedGenre, StringComparison.OrdinalIgnoreCase);
                GenreChips.Add(chip);
            }

            // The search field says how much it is about to search, which is the cheapest
            // possible answer to "did the scan actually find anything".
            if (SearchBox is not null)
                SearchBox.Watermark = LibraryGrouping.SearchWatermark(VisibleMovies);
        }

        /// <summary>
        /// Rebuilds the source row, and hides it when the library comes from a single place.
        /// </summary>
        /// <remarks>
        /// The counts come from the whole library rather than the filtered view, or selecting
        /// "Offline" would leave the server's own control reading zero and there would
        /// be nothing to say how to get back.
        /// </remarks>
        private void BuildSources()
        {
            BuildKinds();

            // Narrowed by kind, and deliberately: with television selected, the source row has to
            // count the television. It said "412 Offline" beside an empty page otherwise, because
            // every offline film is a film and none of them is a programme.
            var scope = LibraryFilter.Apply(_allMovies, _kind);
            var available = LibraryFilter.Available(scope);

            SourceChips.Clear();
            foreach (var source in available)
            {
                SourceChips.Add(new SourceChip
                {
                    Source = source,
                    Count = LibraryFilter.Count(scope, source),
                    IsSelected = source == _source
                });
            }

            var show = SourceChips.Count > 0;
            if (SourceChipsList is not null) SourceChipsList.IsVisible = show;
            if (SourceDivider is not null) SourceDivider.IsVisible = show;

            // Whichever of the two rows comes first carries the window's left margin, and either
            // of them can be absent. Left in XAML it would be a fixed indent that is wrong in one
            // of the three arrangements.
            if (KindChipsList is not null)
                KindChipsList.Margin = new Avalonia.Thickness(show ? 0 : 24, 0, 0, 0);

            // A source that has just stopped existing — the last local film removed, or a server
            // switched off — must not leave the window filtered to nothing with no way back.
            if (!show && _source != LibrarySource.Everywhere) _source = LibrarySource.Everywhere;
        }

        /// <summary>
        /// Rebuilds the kind row, and hides it unless the library actually holds both films and
        /// television.
        /// </summary>
        /// <remarks>
        /// Counted across the whole library rather than the filtered view, for the same reason the
        /// source row is: selecting "Television" must not leave "Films" reading zero, or the way
        /// back out would be a control that looks like it does nothing.
        /// </remarks>
        private void BuildKinds()
        {
            var available = LibraryFilter.AvailableKinds(_allMovies);

            KindChips.Clear();
            foreach (var kind in available)
            {
                KindChips.Add(new KindChip
                {
                    Kind = kind,
                    Count = LibraryFilter.Count(_allMovies, kind),
                    IsSelected = kind == _kind
                });
            }

            var show = KindChips.Count > 0;
            if (KindChipsList is not null) KindChipsList.IsVisible = show;
            if (KindDivider is not null) KindDivider.IsVisible = show;

            // The last programme leaving the library — a server switched off, or one that has
            // stopped reporting television — must not leave the window filtered to nothing.
            if (!show && _kind != LibraryKind.Everything) _kind = LibraryKind.Everything;
        }

        /// <summary>
        /// Narrows the library to films or to television, or widens it again. Genre and source
        /// stay as they were: three different questions, and answering one should not silently
        /// discard the others.
        /// </summary>
        private void KindChip_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton tb || tb.DataContext is not KindChip chip) return;

            _kind = chip.Kind;

            ReapplyFilters();
        }

        /// <summary>
        /// Redraws the library for a filter that has just changed, without asking the database
        /// again: narrowing what you are looking at does not change what the query matched.
        /// </summary>
        private void ReapplyFilters()
        {
            // A search and a filter are two narrowings of one library, not two views of it, so
            // narrowing has to narrow the results already in front of you rather than throwing
            // away whatever has been typed.
            if (_view.IsSearch)
            {
                ShowSearchResults();
                return;
            }

            // A genre that only the other half had would otherwise stay selected and show an
            // empty page.
            var stillThere = LibraryGrouping.BuildGenreChips(VisibleMovies)
                                            .Any(c => string.Equals(c.Name, SelectedGenre, StringComparison.OrdinalIgnoreCase));
            if (!stillThere) SelectedGenre = LibraryGrouping.AllGenres;

            BuildGenres();
            RebuildGroups();

            if (string.Equals(SelectedGenre, LibraryGrouping.AllGenres, StringComparison.OrdinalIgnoreCase))
            {
                ShowAllGenres();
            }
            else
            {
                ShowGenre(SelectedGenre);
            }
        }

        /// <summary>
        /// Narrows the library to one place, or widens it again. Genre stays as it was: the two
        /// are different questions and answering one should not silently discard the other.
        /// </summary>
        private void SourceChip_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton tb || tb.DataContext is not SourceChip chip) return;

            _source = chip.Source;

            ReapplyFilters();
        }

        /// <summary>
        /// The vertical flow for a single genre. One method rather than the same six lines in each
        /// of the three places that used to select a genre.
        /// </summary>
        private void ShowGenre(string? genre)
        {
            SingleGenreItems.Clear();
            foreach (var m in LibraryGrouping.ItemsForGenre(VisibleMovies, genre))
                SingleGenreItems.Add(m);

            SingleGenreCountText.Text = LibraryGrouping.CountLabel(SingleGenreItems);
            WarmPosters(SingleGenreItems);
            ShowSingleGenre();
        }

        private void WarmPosters(IEnumerable<UiMovie> movies)
        {
            var loader = _posterLoader;
            if (loader is null) return;

            foreach (var m in movies)
            {
                // A film that is only on the server is already described by the server, artwork
                // included. Sending it to TMDB would ask for an answer the app already has, and
                // would make a Jellyfin library depend on a TMDB key it has no reason to need.
                //
                // A film that is in both places is not skipped: it is showing the server's poster
                // only until the catalogue on this machine has one of its own, and that copy is
                // the one that still has a poster with the server switched off. A film whose own
                // copy has since gone is skipped again, because there is no longer a local copy
                // for that artwork to belong to — it keeps whatever poster it already had.
                if (m.IsRemote) continue;
                if (!string.IsNullOrWhiteSpace(m.PosterPath)) continue;

                // Queued rather than discarded. The task used to be dropped on the floor here,
                // which is what let a closing window walk away from work it had started: nothing
                // held it, so nothing could wait for it or notice it had gone.
                loader.Queue(
                    movieId: m.Id,
                    title: m.Title,
                    year: m.Year,
                    onFetched: path => ShowOnUiThread(() => m.PosterPath = path),
                    ct: _cts.Token);
            }
        }

        /// <summary>
        /// Runs <paramref name="update"/> on the UI thread, and only while there is still a
        /// window to update. Checked on both sides of the hop: once here, to save posting work
        /// that has already been abandoned, and again when it runs, because the window can close
        /// in between.
        /// </summary>
        private void ShowOnUiThread(Action update)
        {
            if (_cts.IsCancellationRequested) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (_cts.IsCancellationRequested) return;
                update();
            });
        }

        /// <summary>
        /// A poster the loader gave up on. Reported once per configured library rather than once
        /// per film: whatever stops one poster — no key, no network, a catalogue somebody else has
        /// open — stops all of them, and a status line rewritten several hundred times says less
        /// than a single sentence. The rest are in <c>posters.log</c>.
        ///
        /// Called from whichever background worker failed, so it hops to the UI thread to say so.
        /// </summary>
        private void ReportPosterFailure(string message)
        {
            if (Interlocked.Increment(ref _posterFailuresReported) > 1) return;

            Dispatcher.UIThread.Post(() => SetStatus(message));
        }

        private void RebuildGroups()
        {
            VisibleGroups.Clear();

            var showingEverything =
                string.IsNullOrWhiteSpace(SelectedGenre) ||
                string.Equals(SelectedGenre, LibraryGrouping.AllGenres, StringComparison.OrdinalIgnoreCase);

            var buckets = showingEverything
                ? GenreChips.Select(c => c.Name)
                : new[] { SelectedGenre! };

            // Materialised once. It is a filtered copy built on each read, and the row below has
            // to be stamped onto the same card objects the shelves are built from.
            var visible = VisibleMovies;

            // Built from the same filtered list as the shelves below it, so narrowing to one place
            // narrows this row too rather than leaving it describing a library nothing else on the
            // page is showing. Only shown when every genre is on screen: a single genre is a page
            // about one thing, and this row is not about that thing.
            //
            // Called unconditionally, because building it is also what clears the progress mark
            // off a film that is no longer part-watched.
            var continueWatching = ResumeRow.Build(visible, _resume);

            foreach (var shelf in LibraryGrouping.BuildShelves(
                         visible,
                         buckets,
                         showingEverything ? continueWatching : null))
            {
                VisibleGroups.Add(shelf);
            }

            foreach (var group in VisibleGroups)
                WarmPosters(group.Items);
        }

        /// <summary>
        /// The UI boundary for a scan, and one of the two <c>async void</c> handlers in the class.
        /// It owns the button state and the error reporting; the work itself is an awaited Task,
        /// so the connection stays open for as long as the scan needs it.
        /// </summary>
        private async void ScanButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_scanning) return;

            // Scanning nothing would report success and change nothing, which reads as a broken
            // button rather than as a library that was never pointed anywhere.
            if (_config.WatchFolders is null || _config.WatchFolders.Length == 0)
            {
                SetStatus("No folders to scan. Add one under Settings.");
                await MessageBoxWindow.ShowAsync(
                    this,
                    "UrDatabase",
                    "There are no folders to scan. Add one under Settings, or use a Jellyfin server instead.");
                return;
            }

            _scanning = true;
            if (ScanButton is not null) ScanButton.IsEnabled = false;
            SetBusy(true);

            try
            {
                SetStatus("Scanning…");
                var result = await RunScanAsync(_cts.Token);

                await _searchLoop.RefreshAsync();

                // The scan's own sentence, not a rewrite of it. It is the one thing that knows
                // whether it finished, what it added and what it could no longer find; a status
                // line that reduced all of that back to a single "updated" number is the reason
                // nobody could tell a scan that did nothing from one that did everything.
                //
                // Said after the reload rather than before it, because the reload now carries a
                // status line of its own and would otherwise overwrite this one.
                SetStatus($"{result.Summary} {_allMovies.Count} movies in the library.");
            }
            catch (OperationCanceledException)
            {
                // The window is closing. Nothing left to report to.
            }
            catch (Exception ex)
            {
                AppLog.Write("scan.log", $"scan failed: {ex}");
                SetStatus($"Scan failed: {ex.Message}");
                await MessageBoxWindow.ShowAsync(this, "UrDatabase", $"Scan failed: {ex.Message}");
            }
            finally
            {
                _scanning = false;
                if (ScanButton is not null) ScanButton.IsEnabled = true;
                SetBusy(false);
            }
        }

        /// <summary>
        /// Runs the scan off the UI thread, on a connection that lives exactly as long as the scan.
        /// Enumerating a film folder and writing thousands of rows is synchronous work underneath;
        /// left on the dispatcher it froze the window and no progress message ever painted.
        /// </summary>
        private Task<ScanResult> RunScanAsync(CancellationToken ct)
        {
            var folders = _config.WatchFolders ?? Array.Empty<string>();
            var progress = new Progress<string>(msg => Dispatcher.UIThread.Post(() => SetStatus(msg)));

            return Task.Run(() => ScanService.ScanLibraryAsync(_dbPath, folders, progress, ct), ct);
        }

        private async void JellyfinSyncButton_Click(object? sender, RoutedEventArgs e)
            => await SyncJellyfinAsync(announceFailure: true);

        /// <summary>
        /// Refreshes the cached server library.
        /// </summary>
        /// <param name="announceFailure">
        /// True when somebody pressed the button and is owed an answer. False for the refresh at
        /// startup, which must never greet a person with a dialog because their laptop is not at
        /// home — the status line says so and the cached library carries on working.
        /// </param>
        private async Task SyncJellyfinAsync(bool announceFailure)
        {
            if (_jellyfin is null || _syncing) return;

            _syncing = true;
            if (JellyfinButton is not null) JellyfinButton.IsEnabled = false;
            SetBusy(true);

            try
            {
                SetStatus("Jellyfin: contacting the server…");

                var progress = new Progress<string>(msg => Dispatcher.UIThread.Post(() => SetStatus(msg)));

                var count = await Task.Run(async () =>
                {
                    using var conn = Database.Open(_dbPath);
                    return await JellyfinSync.RefreshAsync(_jellyfin, conn, progress, _cts.Token);
                }, _cts.Token);

                LoadRemoteCache();
                await _searchLoop.RefreshAsync();

                SetStatus(count.Describe());
            }
            catch (OperationCanceledException)
            {
                // The window is closing.
            }
            catch (JellyfinException ex)
            {
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"sync failed: {ex.Message}"));

                // Deliberately not awaited. It is one more request against a server that has just
                // failed to answer, and on the timeout case that is another fifteen seconds; the
                // person is owed the message they already have now, not after a second wait.
                _ = LogConnectionDiagnosticAsync();

                var cached = _remoteMovies.Count;
                SetStatus(cached > 0
                    ? $"{ex.Message} Showing the {cached} {(cached == 1 ? "item" : "items")} from the last sync."
                    : ex.Message);

                if (announceFailure)
                    await MessageBoxWindow.ShowAsync(this, "Jellyfin", ex.Message);
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"sync failed: {ex}"));
                SetStatus($"Jellyfin sync failed: {ex.Message}");

                if (announceFailure)
                    await MessageBoxWindow.ShowAsync(this, "Jellyfin", $"Jellyfin sync failed: {ex.Message}");
            }
            finally
            {
                _syncing = false;
                if (JellyfinButton is not null) JellyfinButton.IsEnabled = true;
                SetBusy(false);
            }
        }

        /// <summary>
        /// After a failed sync, asks the server to identify itself and writes what came back to
        /// the log. One extra request, only ever on the failure path, and it is the difference
        /// between a log that says the server could not be reached and one that says the name did
        /// not resolve, the port refused the connection, or something answered that is not
        /// Jellyfin. Failing to diagnose a failure must not itself raise anything.
        /// </summary>
        private async Task LogConnectionDiagnosticAsync()
        {
            if (_jellyfin is null) return;

            // Its own deadline. A server that is dropping connections would otherwise hold this
            // for the client's full timeout, long after anybody stopped caring.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(6));

            try
            {
                var report = await _jellyfin.TestConnectionAsync(deadline.Token);
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"connection test: {report}"));
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // The window is closing.
            }
            catch (OperationCanceledException)
            {
                AppLog.Write("jellyfin.log", "connection test: no answer within six seconds.");
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"connection test failed: {ex.Message}"));
            }
        }

        /// <summary>
        /// Every keystroke, and nothing more than a keystroke. Deliberately not <c>async void</c>:
        /// the work is handed to <see cref="_searchLoop"/>, which debounces it, cancels whatever
        /// it supersedes and routes any failure to <see cref="ReportLibraryFailure"/>. An
        /// exception escaping an <c>async void</c> handler ends the process rather than the
        /// search, and this handler used to run the whole library query inline.
        /// </summary>
        private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            var query = (sender as TextBox)?.Text?.Trim();

            // Discarded on purpose: the returned task never faults, and nothing here waits for a
            // search that the next keystroke may well throw away.
            _ = _searchLoop.PostAsync(string.IsNullOrWhiteSpace(query) ? null : query);
        }

        private void GenreChip_Click(object? sender, RoutedEventArgs e)
        {
            // The genre comes off the chip's data context. It used to be read back out of the
            // button's own Content as a string, which quietly stops working the moment a chip
            // holds anything but text — and it now holds a name and a count in two faces.
            if (sender is ToggleButton tb && tb.DataContext is GenreChip chip)
            {
                SelectedGenre = chip.Name;

                // Rebuilt rather than ticked in place: the chips carry their own selected state,
                // so this is also what makes the right one appear selected on the first render.
                BuildGenres();

                if (string.Equals(SelectedGenre, LibraryGrouping.AllGenres, StringComparison.OrdinalIgnoreCase))
                {
                    RebuildGroups();
                    ShowAllGenres();
                }
                else
                {
                    ShowGenre(SelectedGenre);
                }
            }
        }

        /// <summary>
        /// Settings is the setup screen again. Anything saved is applied to the running window
        /// rather than at the next launch, which is the whole reason the configuration is read
        /// through <see cref="ApplyConfig"/> instead of being captured in the constructor.
        /// </summary>
        private async void Settings_Click(object? sender, RoutedEventArgs e)
        {
            var saved = await SetupWindow.ShowDialogAsync(this);
            if (saved is null) return;

            ApplyConfig();

            // A server that has just been switched off leaves a cached library behind in SQLite.
            // Reloading with no client drops it from view, which is what turning it off meant.
            LoadRemoteCache();
            await _searchLoop.RefreshAsync();

            if (_jellyfin is not null)
                await SyncJellyfinAsync(announceFailure: true);
        }

        // ==============================================================
        //  the update banner
        // ==============================================================

        /// <summary>
        /// Asks GitHub whether there is a newer release and, if there is one worth mentioning,
        /// raises the banner.
        ///
        /// Runs on the UI thread and stays there: the request is awaited, so every line that
        /// touches a control is a continuation on the same thread rather than a marshalled post.
        /// Nothing here is allowed to matter — an update check that could take the window down
        /// with it would be a far worse fault than one that never ran.
        /// </summary>
        private async Task CheckForUpdateAsync()
        {
            try
            {
                using var service = new UpdateService();

                var update = await service.CheckAsync(_cts.Token);
                if (update is null) return;

                // Asked after the request rather than before it. The file records one version and
                // this is the only place that knows which version was actually found, so reading it
                // first would mean loading it on every launch to answer a question that usually
                // never gets asked.
                if (!UpdatePrompt.ShouldShow(update, UpdateState.Load().SkippedVersion))
                {
                    AppLog.Write("update.log", $"{update.Version} is available and was dismissed earlier; saying nothing.");
                    return;
                }

                _update = update;
                UpdateHeadline.Text = UpdatePrompt.Headline(update);
                UpdateDetail.Text = UpdatePrompt.Detail(update, service.RunningVersion);
                UpdateActionButton.Content = UpdatePrompt.ActionText(update);
                UpdateBanner.IsVisible = true;

                // The one line that makes "why was I never told about 0.12.0?" answerable. A check
                // that finds nothing writes nothing, so this file stays empty on an up-to-date
                // install rather than growing a line per launch.
                AppLog.Write(
                    "update.log",
                    $"{service.RunningVersion} is behind {update.Version}; offering {update.Asset?.Name ?? "the downloads page"}.");
            }
            catch (OperationCanceledException)
            {
                // The window closed while the check was in flight.
            }
            catch (Exception ex)
            {
                AppLog.Write("update.log", $"update check failed: {ex}");
            }
        }

        /// <summary>
        /// The banner's one action, which is four depending on where things stand: stop a running
        /// download, open one that has already landed, fetch the build, or — when there is nothing
        /// this app can fetch — open the downloads page.
        ///
        /// One button rather than four, because only one of them is ever the sensible thing to do
        /// and a row of disabled controls says less than a single live one.
        /// </summary>
        private async void UpdateAction_Click(object? sender, RoutedEventArgs e)
        {
            if (_updateCts is not null)
            {
                _updateCts.Cancel();
                return;
            }

            // Checked against the disk and not just the field: the archive can be moved or thrown
            // away between the download finishing and somebody pressing this, and re-fetching is a
            // better answer than opening a path that no longer exists.
            if (_updateDownloadPath is string ready && File.Exists(ready))
            {
                OpenDownloadedUpdate(ready);
                return;
            }

            var update = _update;
            if (update is null) return;

            if (_updateFetchFailed || update.Asset is not UpdateAsset asset)
            {
                await OpenWebAsync(UpdateFeed.DownloadsPageUrl);
                return;
            }

            await DownloadUpdateAsync(update, asset);
        }

        /// <summary>
        /// Fetches the build, then opens it. Opening it is as far as this goes: the running app
        /// cannot replace itself — on macOS it is a signed bundle that would invalidate its own
        /// signature, on Windows a folder of files it holds open — so the honest end of this
        /// journey is the archive, in front of the user, in whatever their machine opens it with.
        /// </summary>
        private async Task DownloadUpdateAsync(AvailableUpdate update, UpdateAsset asset)
        {
            _updateCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

            UpdateActionButton.Content = "Cancel";
            UpdateLaterButton.IsEnabled = false;
            UpdateProgressBar.IsVisible = true;
            UpdateProgressBar.IsIndeterminate = true;
            UpdateProgressBar.Value = 0;

            var progress = new Progress<UpdateProgress>(report =>
            {
                UpdateDetail.Text = UpdatePrompt.Downloading(report);

                // A server that sends no length leaves the bar sweeping rather than sitting at
                // zero, which reads as stalled.
                UpdateProgressBar.IsIndeterminate = report.Fraction is null;
                if (report.Fraction is double fraction) UpdateProgressBar.Value = fraction;
            });

            try
            {
                using var downloader = new UpdateDownloader();

                var path = await downloader.DownloadAsync(
                    asset, PlatformPaths.DefaultUpdateFolder, progress, _updateCts.Token);

                _updateDownloadPath = path;
                _updateFetchFailed = false;

                UpdateDetail.Text = UpdatePrompt.Downloaded(path);
                UpdateActionButton.Content = UpdatePrompt.OpenAgainAction;

                OpenDownloadedUpdate(path);
            }
            catch (OperationCanceledException)
            {
                // Only ever the user's own Cancel here, or the window closing, and in the second
                // case there is nobody to tell.
                if (!_cts.IsCancellationRequested)
                {
                    UpdateDetail.Text = UpdatePrompt.DownloadStopped;
                    UpdateActionButton.Content = UpdatePrompt.ActionText(update);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("update.log", $"could not fetch {asset.Name}: {ex}");

                _updateFetchFailed = true;
                UpdateDetail.Text = UpdatePrompt.DownloadFailed(ex is UpdateException ? ex.Message : null);
                UpdateActionButton.Content = UpdatePrompt.WebsiteAction;
            }
            finally
            {
                _updateCts.Dispose();
                _updateCts = null;

                UpdateProgressBar.IsVisible = false;
                UpdateLaterButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Hands the archive to the operating system. A failure here is not a failed update — the
        /// file is on the disk and the message says where — so it says so rather than pretending
        /// the download was wasted.
        /// </summary>
        private void OpenDownloadedUpdate(string path)
        {
            try
            {
                FileLauncher.Open(path);
            }
            catch (Exception ex)
            {
                AppLog.Write("update.log", $"could not open {path}: {ex.Message}");
                UpdateDetail.Text = $"The update is at {path}, but it could not be opened from here.";
            }
        }

        /// <summary>
        /// Dismisses the banner and remembers the version, so this release is not announced again
        /// on every launch until it is installed. A newer one still gets through.
        /// </summary>
        private void UpdateLater_Click(object? sender, RoutedEventArgs e)
        {
            UpdateBanner.IsVisible = false;
            UpdateState.SaveSkipped(_update?.Version);
        }

        /// <summary>
        /// The release notes, which are the only thing that answers "why should I?" — the first
        /// question any update prompt raises and the one it is least able to answer itself.
        /// </summary>
        private async void UpdateNotes_Click(object? sender, RoutedEventArgs e) =>
            await OpenWebAsync(_update?.Page ?? UpdateFeed.ReleasesPageUrl);

        private async Task OpenWebAsync(string url)
        {
            try
            {
                FileLauncher.OpenUrl(url);
            }
            catch (Exception ex)
            {
                AppLog.Write("update.log", $"could not open {url}: {ex.Message}");

                // The address is put on screen rather than only in a log, because a machine with
                // no working default browser still has one somewhere the user can paste into.
                await MessageBoxWindow.ShowAsync(
                    this,
                    "UrDatabase",
                    $"Could not open your browser.{Environment.NewLine}{Environment.NewLine}{url}");
            }
        }

        /// <summary>
        /// Opens the details screen over the library, and puts the library back when it closes.
        /// </summary>
        /// <remarks>
        /// The library is hidden rather than merely covered. Every layer of the details screen
        /// above its own background is semi-transparent, so a visible library composited straight
        /// through the backdrop — shelves, genre row and counts were all legible through the dark
        /// half of the screen. Hiding it also takes its buttons out of the tab order, which were
        /// otherwise still reachable, and still clickable, behind a screen covering them.
        /// </remarks>
        private async Task ShowDetailsAsync(MovieDetailsVm vm)
        {
            LibraryRoot.IsVisible = false;

            try
            {
                await DetailsView.ShowAsync(vm, _dbPath, _config, LoadImdbRatingAsync, _jellyfin, _cts.Token);

                // A downloaded film is a row the library behind this screen does not have yet: it
                // would still be shown as living only on the server until something reloaded it.
                // A renamed one is a row whose name, sort position and genre shelf have all moved.
                if (DetailsView.DownloadedSomething || DetailsView.RenamedSomething)
                    await _searchLoop.RefreshAsync();
            }
            finally
            {
                LibraryRoot.IsVisible = true;
            }
        }

        private void ShowSearch()
        {
            SearchPanel.IsVisible = true;
            GroupPanel.IsVisible = false;
            SingleGenrePanel.IsVisible = false;
            EmptyPanel.IsVisible = false;
        }

        /// <summary>
        /// The grouped view, or the invitation to fill the library when there is nothing to
        /// group. A fresh install used to land on a blank page here, which is indistinguishable
        /// from a scan that silently failed.
        /// </summary>
        private void ShowAllGenres()
        {
            var empty = VisibleMovies.Count == 0;

            SearchPanel.IsVisible = false;
            GroupPanel.IsVisible = !empty;
            SingleGenrePanel.IsVisible = false;
            EmptyPanel.IsVisible = empty;
        }

        private void ShowSingleGenre()
        {
            SearchPanel.IsVisible = false;
            GroupPanel.IsVisible = false;
            SingleGenrePanel.IsVisible = true;
            EmptyPanel.IsVisible = false;
        }

        private async void MovieCard_Click(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed) return;
            if ((sender as Control)?.DataContext is not UiMovie m) return;

            // A series is not a film with episodes attached: it opens a different screen, and it
            // is never sent to TMDB, which would answer about a film of the same name.
            if (m.IsSeries)
            {
                await ShowSeriesDetailsAsync(m);
                return;
            }

            if (m.IsRemote)
            {
                await ShowRemoteDetailsAsync(m);
                return;
            }

            try
            {
                using var tmdb = new TmdbService(
                    apiKey: _config.TmdbApiKey ?? "",
                    posterCacheDir: _config.PosterCacheDir ?? "",
                    imageSize: _config.TmdbImageSize ?? "w780",
                    downloadPosters: false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(12));

                // Asked by id when the catalogue knows which film this is, and only searched by
                // title when it does not. Searching every time is what made a corrected match
                // temporary: the answer was thrown away and the same wrong guess re-derived on the
                // next open.
                var storedTmdbId = ReadStoredTmdbId(m.Id);
                var details = storedTmdbId is int knownId
                    ? await tmdb.GetDetailsByIdAsync(knownId, cts.Token)
                    : await tmdb.GetDetailsByTitleAsync(m.Title, m.Year, cts.Token);

                List<string> cast = new();
                List<string> crew = new();

                if (details?.Id is int tmdbId)
                {
                    var credits = await tmdb.GetCreditsByIdAsync(tmdbId, cts.Token);
                    cast = CreditLine.Cast(credits);
                    crew = CreditLine.Crew(credits);
                }

                var vm = new MovieDetailsVm
                {
                    LocalId = m.Id,
                    Title = m.Title,
                    Year = m.Year,
                    TmdbId = details?.Id ?? storedTmdbId,

                    // The server's artwork when the catalogue has none of its own, which is what
                    // the card is already showing.
                    PosterPath = m.DisplayPosterPath,
                    Overview = details?.Overview ?? "",
                    Runtime = details?.Runtime,
                    ImdbId = details?.ImdbId,
                    Genres = details is null ? m.Genres ?? "" : CreditLine.Genres(details),
                    BackdropUrl = string.IsNullOrWhiteSpace(details?.BackdropPath) ? null
                                  : tmdb.BuildImageUrl(details!.BackdropPath!),
                    TmdbConfigured = !string.IsNullOrWhiteSpace(_config.TmdbApiKey),

                    // Opened from disk, and the server has a copy too. Said on the facts row, in
                    // place of the badge the card carries.
                    IsOnServer = m.IsOnServer
                };
                vm.TopCast = cast;
                vm.KeyCrew = crew;

                // Anything TMDB did not answer, and the server can. Before the IMDb lookup below,
                // because the id it is keyed on may be one of the gaps the server just filled.
                if (m.IsOnServer) FillFromServer(vm, m.RemoteId);

                vm.ImdbRating = await LoadImdbRatingAsync(vm.ImdbId, m.Id, cts.Token);

                // Both halves of the merge matter here: main's play-target resolution decides
                // which file Play opens and how sure the app is of it, and the details screen it
                // was handed to is now a view inside this window rather than a dialog over it.
                var target = FindPlayTargetForMovie(m);
                vm.FilePath = target.FilePath;
                vm.FileMatch = target.Kind;

                await ShowDetailsAsync(vm);

                // The film may have been re-identified while the details screen was up, and the
                // card behind it is still showing the poster that was wrong. Only when the screen
                // actually changed it: the poster handed to it may have been the server's, and
                // writing that into the local column would leave the card claiming artwork this
                // machine does not have and stop the loader ever fetching any.
                if (!string.Equals(vm.PosterPath, m.DisplayPosterPath, StringComparison.Ordinal))
                    m.PosterPath = vm.PosterPath;
            }
            catch (Exception ex)
            {
                await MessageBoxWindow.ShowAsync(this, "UrDatabase", $"Could not load details:{Environment.NewLine}{ex.Message}");
            }
        }

        /// <summary>
        /// Lets the server describe a film this machine also has, wherever nothing else could.
        /// </summary>
        /// <remarks>
        /// A film in both places is one card, opened as a local film. Without this the fold would
        /// have cost the user the server's description of it — the only description there is on an
        /// install with no TMDB key, which is a supported install.
        /// </remarks>
        private void FillFromServer(MovieDetailsVm vm, string? remoteId)
        {
            if (string.IsNullOrWhiteSpace(remoteId)) return;
            if (!_remoteById.TryGetValue(remoteId, out var film)) return;

            ServerDetails.FillGaps(vm, film, id => _jellyfin?.BuildBackdropUrl(id));
        }

        /// <summary>
        /// Opens a server film. Everything on the page comes from the cache, so the window opens
        /// instantly and opens at all with the server down; the network is needed only for the
        /// play URL, and failing to get one is reported as "cannot play right now" rather than as
        /// an error.
        /// </summary>
        /// <remarks>
        /// Also opens a catalogued film whose own copy has gone and which the server still has.
        /// Such a card keeps its catalogue identity here — the row id, and the title and year the
        /// catalogue holds rather than the server's spelling of them. That matters beyond
        /// tidiness: a download is named from the title on this screen and registered by parsing
        /// that name back, so taking the server's title for a film the catalogue already has under
        /// another one would file the download as a second film beside the first.
        /// </remarks>
        private async Task ShowRemoteDetailsAsync(UiMovie m)
        {
            if (_jellyfin is null || string.IsNullOrWhiteSpace(m.RemoteId)) return;
            if (!_remoteById.TryGetValue(m.RemoteId, out var film)) return;

            try
            {
                var vm = new MovieDetailsVm
                {
                    // Zero for a film that only ever came from the server, which is what every
                    // consumer of this already treats as "no local row".
                    LocalId = m.Id,
                    Title = m.Title,
                    Year = m.Year,
                    Genres = film.Genres,
                    Overview = film.Overview,
                    Runtime = film.RuntimeMinutes,
                    CommunityRating = film.CommunityRating,
                    ImdbId = film.ImdbId,

                    // What the card is already showing: this machine's artwork when the catalogue
                    // has any, and the server's otherwise. Taking the local column alone opened a
                    // blank screen for a degraded film the catalogue had never fetched a poster
                    // for, which is most of them.
                    PosterPath = m.DisplayPosterPath,
                    BackdropUrl = _jellyfin.BuildBackdropUrl(film.ItemId),
                    IsRemote = true,
                    RemoteId = film.ItemId,
                    DownloadFolder = _config.DownloadFolder,
                    DatabasePath = _config.DatabasePath,

                    // The server has described its own cast and crew since the sync that cached
                    // it. Nothing used to ask for them, so every film from a server showed an
                    // empty list as though it genuinely had none.
                    TopCast = film.Cast.ToList(),
                    KeyCrew = film.Crew.ToList()
                };

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(12));

                try
                {
                    await _jellyfin.ConnectAsync(cts.Token);
                    vm.StreamUrl = _jellyfin.BuildStreamUrl(film.ItemId);
                }
                catch (JellyfinException ex)
                {
                    // Leaves StreamUrl null, which the details window explains for itself.
                    AppLog.Write("jellyfin.log", JellyfinClient.Redact($"no stream url: {ex.Message}"));
                }

                // The IMDb id came from Jellyfin's own metadata, so this is a real IMDb rating and
                // not the community number beside it. Keyed to the catalogue row when there is
                // one — a film whose copy has gone still has a row that owns the rating — and to
                // nothing at all for a film that only ever came from the server.
                vm.ImdbRating = await LoadImdbRatingAsync(vm.ImdbId, vm.LocalId > 0 ? vm.LocalId : null, cts.Token);

                await ShowDetailsAsync(vm);
            }
            catch (OperationCanceledException)
            {
                // The window is closing.
            }
            catch (Exception ex)
            {
                await MessageBoxWindow.ShowAsync(this, "UrDatabase", $"Could not load details:{Environment.NewLine}{ex.Message}");
            }
        }

        /// <summary>
        /// Opens a television series. Everything on the page comes from the cache, so it opens
        /// instantly and opens at all with the server down; the episodes are asked for afterwards
        /// and the screen says which of the two it is showing.
        /// </summary>
        private async Task ShowSeriesDetailsAsync(UiMovie m)
        {
            if (string.IsNullOrWhiteSpace(m.RemoteId)) return;
            if (!_remoteSeriesById.TryGetValue(m.RemoteId, out var show)) return;

            try
            {
                var vm = new SeriesDetailsVm
                {
                    RemoteId = show.ItemId,
                    Title = show.Title,
                    Year = show.Year,
                    Genres = show.Genres,
                    Overview = show.Overview,
                    CommunityRating = show.CommunityRating,
                    ImdbId = show.ImdbId,
                    PosterPath = m.DisplayPosterPath,
                    BackdropUrl = _jellyfin?.BuildBackdropUrl(show.ItemId),
                    SeasonCount = show.SeasonCount,
                    EpisodeCount = show.EpisodeCount,
                    TopCast = show.Cast.ToList(),
                    KeyCrew = show.Crew.ToList()
                };

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(12));

                // The IMDb id came from Jellyfin's own metadata, so this is a real IMDb rating and
                // not the community number beside it. No local movie row owns it.
                vm.ImdbRating = await LoadImdbRatingAsync(vm.ImdbId, null, cts.Token);

                LibraryRoot.IsVisible = false;

                try
                {
                    await SeriesView.ShowAsync(vm, _series, _jellyfin);
                }
                finally
                {
                    LibraryRoot.IsVisible = true;
                }
            }
            catch (OperationCanceledException)
            {
                // The window is closing.
            }
            catch (Exception ex)
            {
                await MessageBoxWindow.ShowAsync(this, "UrDatabase", $"Could not load details:{Environment.NewLine}{ex.Message}");
            }
        }

        /// <summary>
        /// IMDb ratings come from OMDb, matched on the IMDb id TMDB or Jellyfin reports. Entirely
        /// optional: no id, no key or no network simply means no rating, never a substitute from
        /// another source.
        /// </summary>
        private async Task<double?> LoadImdbRatingAsync(string? imdbId, long? movieId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(imdbId)) return null;

            try
            {
                using var conn = Database.Open(_dbPath);
                return await _ratings.GetRatingAsync(conn, imdbId, movieId, ct);
            }
            catch (Exception ex)
            {
                AppLog.Write("omdb.log", $"rating lookup failed for {imdbId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Which TMDB film the catalogue says this is, or null when nothing has said yet. A
        /// failure to read is the same answer as "nothing recorded": the film still opens, it is
        /// just identified by title again as it always used to be.
        /// </summary>
        private int? ReadStoredTmdbId(long movieId)
        {
            if (movieId <= 0) return null;

            try
            {
                using var conn = Database.Open(_dbPath);
                return MovieMatch.ReadTmdbId(conn, movieId);
            }
            catch (Exception ex)
            {
                AppLog.Write("app.log", $"could not read the tmdb match for movie {movieId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// What Play will open for a local film, and on what evidence. The catalogue's own
        /// <c>files.movie_id</c> link decides it; a filename is only ever a suggestion the user
        /// gets to confirm. This used to load every path in the table and return the first name
        /// containing the title, which is how a film called <em>It</em> played
        /// <c>Spirited Away.mkv</c>.
        ///
        /// A failure to read the database is not worth a dialog: the details window says the film
        /// has no file linked, which is the same thing from where the user is standing.
        /// </summary>
        private PlayTarget FindPlayTargetForMovie(UiMovie m)
        {
            try
            {
                using var conn = Database.Open(_dbPath);
                return PlayTargetResolver.Resolve(conn, m.Id, m.Title, m.Year);
            }
            catch (Exception ex)
            {
                AppLog.Write("app.log", $"could not resolve a file for movie {m.Id}: {ex.Message}");
                return PlayTarget.None;
            }
        }
    }
}
