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
using Avalonia.VisualTree;
using Dapper;
using UrDatabase.Models;
using UrDatabase.Services;

namespace UrDatabase.Views
{
    public partial class MainWindow : Window
    {
        private AppConfig _config = new AppConfig();
        public ObservableCollection<UiMovie> SingleGenreItems { get; } = new();

        private string _dbPath = "";

        public ObservableCollection<string> Genres { get; } = new();
        public string? SelectedGenre { get; set; } = LibraryGrouping.AllGenres;
        public ObservableCollection<GenreGroup> VisibleGroups { get; } = new();
        public ObservableCollection<UiMovie> FlatResults { get; } = new();

        private List<UiMovie> _allMovies = new();
        private List<UiMovie> _localMovies = new();

        /// <summary>The server's whole library as cards, unfiltered. Empty when Jellyfin is off.</summary>
        private List<UiMovie> _remoteMovies = new();

        /// <summary>
        /// The same films keyed by item id, holding the fields a card does not show. Jellyfin
        /// already supplies the overview, runtime, rating and IMDb id, so opening a server film
        /// needs no request at all — which is what makes it work with no TMDB key configured.
        /// </summary>
        private Dictionary<string, JellyfinMovie> _remoteById = new(StringComparer.OrdinalIgnoreCase);

        private PosterAutoLoader? _posterLoader;
        private int _posterFailuresReported;

        /// <summary>Rebuilt whenever the configuration changes; never null once the window exists.</summary>
        private ImdbRatingService _ratings = null!;

        /// <summary>Null unless a server is configured, which is what keeps this feature off by default.</summary>
        private JellyfinClient? _jellyfin;

        private readonly CancellationTokenSource _cts = new();
        private bool _scanning;
        private bool _syncing;

        /// <summary>
        /// Set once the poster loader has been given its chance to finish, so the close that
        /// follows the drain is not deferred a second time.
        /// </summary>
        private bool _postersDrained;

        public MainWindow()
        {
            InitializeComponent();

            ApplyConfig();

            // Runs after the poster drain in OnClosing, and has to: cancelling this first would
            // cut short the very fetches the drain is there to let finish.
            Closed += (_, __) => { _cts.Cancel(); _posterLoader?.Dispose(); _ratings.Dispose(); _jellyfin?.Dispose(); };

            DataContext = this;

            LoadRemoteCache();
            LoadMovies();
            BuildGenres();
            RebuildGroups();
            ShowAllGenres();

            // The window is already painted from the cache by the time this runs, so a slow or
            // absent server delays nothing anybody is looking at.
            if (_jellyfin is not null)
                Dispatcher.UIThread.Post(() => _ = SyncJellyfinAsync(announceFailure: false));
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

            if (_jellyfin is null) return;

            try
            {
                using var conn = Database.Open(_dbPath);
                var cached = JellyfinCache.Load(conn);

                foreach (var movie in cached) _remoteById[movie.ItemId] = movie;

                _remoteMovies = JellyfinLibrary
                    .ToUiMovies(cached, m => _jellyfin.BuildPrimaryImageUrl(m.ItemId, m.ImageTag))
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", $"could not read the cached server library: {ex.Message}");
            }
        }

        private void LoadMovies(string? query = null)
        {
            // Built once so both halves agree on what counts as a search: text with no word in
            // it at all is not one, and showing the whole local library while hiding every
            // server film would look like the server had dropped out.
            var match = FtsQuery.Build(query);

            _localMovies = LoadLocalMovies(match);

            var remote = match is null
                ? (IReadOnlyList<UiMovie>)_remoteMovies
                : JellyfinLibrary.Search(_remoteMovies, query);

            _allMovies = JellyfinLibrary.Merge(_localMovies, remote).ToList();

            SetStatus(LibraryStatus.Describe(
                localCount: _localMovies.Count,
                localWithPosters: _localMovies.Count(x => !string.IsNullOrWhiteSpace(x.PosterPath)),
                remoteCount: remote.Count,
                hasLocalDatabase: File.Exists(_dbPath),
                databasePath: _dbPath));

            WarmPosters(_allMovies);
        }

        /// <summary>
        /// The local half of the library. Returns an empty list rather than throwing for any
        /// reason it might fail, because a server library still has to be browsable when the
        /// local catalogue is missing or was written by an older schema.
        /// </summary>
        /// <param name="match">
        /// An FTS5 MATCH expression from <see cref="FtsQuery.Build"/>, or <c>null</c> to list the
        /// whole catalogue. Never raw text from the search box: FTS5 would read its punctuation
        /// as operators and fail the query.
        /// </param>
        private List<UiMovie> LoadLocalMovies(string? match)
        {
            if (!File.Exists(_dbPath)) return new List<UiMovie>();

            try
            {
                // Not Database.Open: the read path has no business migrating the schema, and this
                // runs on the UI thread on every keystroke in the search box. Database.Connect is
                // still the only way a catalogue connection is built, so this query gets the same
                // busy timeout and the same WAL snapshot as every write it might be racing.
                using var conn = Database.Connect(_dbPath);

                string sql;
                object param;
                if (match is null)
                {
                    sql = "SELECT id AS Id, title AS Title, year AS Year, genres AS Genres, poster_path AS PosterPath FROM movies ORDER BY COALESCE(year,0) DESC, title";
                    param = new { };
                }
                else
                {
                    sql = @"
SELECT m.id AS Id, m.title AS Title, m.year AS Year, m.genres AS Genres, m.poster_path AS PosterPath
FROM movies_fts f
JOIN movies m ON m.id = f.rowid
WHERE movies_fts MATCH @q
ORDER BY rank";
                    param = new { q = match };
                }

                return conn.Query<UiMovie>(sql, param).ToList();
            }
            catch (Exception ex)
            {
                // A database from an older build may lack the tables this query needs.
                // Report it instead of taking the window down.
                AppLog.Write("startup.log", $"LoadMovies failed: {ex}");
                SetStatus($"Could not read the library: {ex.Message}");
                return new List<UiMovie>();
            }
        }

        private void SetStatus(string message)
        {
            Title = $"UrDatabase — {message}";
            if (StatusText is not null) StatusText.Text = message;
        }

        private void BuildGenres()
        {
            Genres.Clear();
            foreach (var genre in LibraryGrouping.BuildGenreList(_allMovies))
                Genres.Add(genre);
        }

        private void WarmPosters(IEnumerable<UiMovie> movies)
        {
            var loader = _posterLoader;
            if (loader is null) return;

            foreach (var m in movies)
            {
                // A server film is already described by the server, artwork included. Sending it
                // to TMDB would ask for an answer the app already has, and would make a Jellyfin
                // library depend on a TMDB key it has no reason to need.
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

            IEnumerable<string> buckets;
            if (string.Equals(SelectedGenre, LibraryGrouping.AllGenres, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(SelectedGenre))
                buckets = Genres.Where(x => !string.Equals(x, LibraryGrouping.AllGenres, StringComparison.OrdinalIgnoreCase));
            else
                buckets = new[] { SelectedGenre! };

            foreach (var genre in buckets)
            {
                var items = LibraryGrouping.ItemsForGenre(_allMovies, genre);
                if (items.Count == 0) continue;

                VisibleGroups.Add(new GenreGroup
                {
                    Name = $"{genre} ({items.Count} items)",
                    Items = new ObservableCollection<UiMovie>(items)
                });
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

            try
            {
                SetStatus("Scanning…");
                var updated = await RunScanAsync(_cts.Token);

                LoadMovies();
                BuildGenres();
                RebuildGroups();
                ShowAllGenres();
                SetStatus($"Scan complete. {updated} file entries updated, {_allMovies.Count} movies in the library.");
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
            }
        }

        /// <summary>
        /// Runs the scan off the UI thread, on a connection that lives exactly as long as the scan.
        /// Enumerating a film folder and writing thousands of rows is synchronous work underneath;
        /// left on the dispatcher it froze the window and no progress message ever painted.
        /// </summary>
        private Task<int> RunScanAsync(CancellationToken ct)
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
                LoadMovies();
                BuildGenres();
                RebuildGroups();
                ShowAllGenres();

                SetStatus($"Jellyfin: {count} {(count == 1 ? "film" : "films")} from the server.");
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
                    ? $"{ex.Message} Showing the {cached} films from the last sync."
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

        private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            var q = (sender as TextBox)?.Text?.Trim();

            if (string.IsNullOrWhiteSpace(q))
            {
                // not searching → grouped view
                LoadMovies(null);
                BuildGenres();
                RebuildGroups();
                ShowAllGenres();
                return;
            }

            // searching → flat view
            LoadMovies(q);
            FlatResults.Clear();

            // Grouped on Key rather than Id: every server film carries id 0, so grouping on the
            // local id would collapse the whole remote half of the results into one card.
            foreach (var m in _allMovies
                    .GroupBy(x => x.Key)
                    .Select(g => g.First())
                    .OrderByDescending(x => x.Year ?? 0)
                    .ThenBy(x => x.Title))
            {
                FlatResults.Add(m);
            }

            WarmPosters(FlatResults);
            ShowSearch();
        }

        private void GenreChip_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton tb && tb.Content is string genreLabel)
            {
                SelectedGenre = genreLabel;

                // Only the clicked chip stays checked.
                foreach (var btn in GenreChips.GetVisualDescendants().OfType<ToggleButton>())
                    btn.IsChecked = string.Equals(btn.Content as string, SelectedGenre, StringComparison.Ordinal);

                if (string.Equals(SelectedGenre, LibraryGrouping.AllGenres, StringComparison.OrdinalIgnoreCase))
                {
                    RebuildGroups();
                    ShowAllGenres();
                }
                else
                {
                    SingleGenreItems.Clear();
                    foreach (var m in LibraryGrouping.ItemsForGenre(_allMovies, SelectedGenre))
                        SingleGenreItems.Add(m);

                    WarmPosters(SingleGenreItems);
                    ShowSingleGenre();
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
            LoadMovies();
            BuildGenres();
            RebuildGroups();
            ShowAllGenres();

            if (_jellyfin is not null)
                await SyncJellyfinAsync(announceFailure: true);
        }

        private void ShowSearch()
        {
            SearchPanel.IsVisible = true;
            GroupPanel.IsVisible = false;
            SingleGenrePanel.IsVisible = false;
        }

        private void ShowAllGenres()
        {
            SearchPanel.IsVisible = false;
            GroupPanel.IsVisible = true;
            SingleGenrePanel.IsVisible = false;
        }

        private void ShowSingleGenre()
        {
            SearchPanel.IsVisible = false;
            GroupPanel.IsVisible = false;
            SingleGenrePanel.IsVisible = true;
        }

        private async void MovieCard_Click(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed) return;
            if ((sender as Control)?.DataContext is not UiMovie m) return;

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

                var details = await tmdb.GetDetailsByTitleAsync(m.Title, m.Year, cts.Token);

                List<string> cast = new();
                List<string> crew = new();

                if (details?.Id is int tmdbId)
                {
                    var credits = await tmdb.GetCreditsByIdAsync(tmdbId, cts.Token);
                    if (credits != null)
                    {
                        foreach (var c in credits.Cast.Take(10))
                        {
                            if (!string.IsNullOrWhiteSpace(c.Name))
                                cast.Add(string.IsNullOrWhiteSpace(c.Character) ? c.Name : $"{c.Name} ({c.Character})");
                        }
                        foreach (var d in credits.Crew.Where(x => string.Equals(x.Job, "Director", StringComparison.OrdinalIgnoreCase)).Take(3))
                            crew.Add($"Director: {d.Name}");
                        foreach (var w in credits.Crew.Where(x => x.Job != null && x.Job.Contains("Writer", StringComparison.OrdinalIgnoreCase)).Take(3))
                            crew.Add($"Writer: {w.Name}");
                    }
                }

                var vm = new MovieDetailsVm
                {
                    LocalId = m.Id,
                    Title = m.Title,
                    Year = m.Year,
                    PosterPath = m.PosterPath,
                    Overview = details?.Overview ?? "",
                    Runtime = details?.Runtime,
                    ImdbId = details?.ImdbId,
                    Genres = details is null ? m.Genres ?? "" : string.Join(", ", details.Genres?.Select(g => g.Name) ?? Array.Empty<string>()),
                    BackdropUrl = string.IsNullOrWhiteSpace(details?.BackdropPath) ? null
                                  : tmdb.BuildImageUrl(details!.BackdropPath!)
                };
                vm.TopCast = cast;
                vm.KeyCrew = crew;
                vm.ImdbRating = await LoadImdbRatingAsync(vm.ImdbId, m.Id, cts.Token);

                var target = FindPlayTargetForMovie(m);
                vm.FilePath = target.FilePath;
                vm.FileMatch = target.Kind;

                var dlg = new MovieDetailsWindow(vm, _dbPath);
                await dlg.ShowDialog(this);
            }
            catch (Exception ex)
            {
                await MessageBoxWindow.ShowAsync(this, "UrDatabase", $"Could not load details:{Environment.NewLine}{ex.Message}");
            }
        }

        /// <summary>
        /// Opens a server film. Everything on the page comes from the cache, so the window opens
        /// instantly and opens at all with the server down; the network is needed only for the
        /// play URL, and failing to get one is reported as "cannot play right now" rather than as
        /// an error.
        /// </summary>
        private async Task ShowRemoteDetailsAsync(UiMovie m)
        {
            if (_jellyfin is null || string.IsNullOrWhiteSpace(m.RemoteId)) return;
            if (!_remoteById.TryGetValue(m.RemoteId, out var film)) return;

            try
            {
                var vm = new MovieDetailsVm
                {
                    Title = film.Title,
                    Year = film.Year,
                    Genres = film.Genres,
                    Overview = film.Overview,
                    Runtime = film.RuntimeMinutes,
                    CommunityRating = film.CommunityRating,
                    ImdbId = film.ImdbId,
                    PosterPath = m.PosterPath,
                    BackdropUrl = _jellyfin.BuildBackdropUrl(film.ItemId),
                    IsRemote = true
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
                // not the community number beside it. No local movie row owns it.
                vm.ImdbRating = await LoadImdbRatingAsync(vm.ImdbId, null, cts.Token);

                var dlg = new MovieDetailsWindow(vm);
                await dlg.ShowDialog(this);
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
