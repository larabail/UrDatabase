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
using Microsoft.Data.Sqlite;
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
        private readonly ImdbRatingService _ratings;

        /// <summary>Null unless a server is configured, which is what keeps this feature off by default.</summary>
        private readonly JellyfinClient? _jellyfin;

        private readonly CancellationTokenSource _cts = new();
        private bool _scanning;
        private bool _syncing;

        public MainWindow()
        {
            InitializeComponent();

            try { _config = AppConfig.Load(); } catch { _config = new AppConfig(); }
            _dbPath = _config.DatabasePath;

            // Constructed eagerly but idle: no request is made until a movie is opened, and none
            // at all when no OMDb key is available.
            _ratings = new ImdbRatingService(new OmdbService(_config.OmdbApiKey), ownsLookup: true);

            // Nothing is constructed, and no database is touched, when no server is configured.
            if (_config.Jellyfin?.IsConfigured == true)
            {
                _jellyfin = new JellyfinClient(
                    _config.Jellyfin,
                    JellyfinDeviceId.Resolve(),
                    version: typeof(MainWindow).Assembly.GetName().Version?.ToString());
            }

            _posterLoader = new PosterAutoLoader(_config, _dbPath, maxConcurrency: 4);
            Closed += (_, __) => { _cts.Cancel(); _posterLoader?.Dispose(); _ratings.Dispose(); _jellyfin?.Dispose(); };

            DataContext = this;

            if (JellyfinButton is not null) JellyfinButton.IsVisible = _jellyfin is not null;

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
            _localMovies = LoadLocalMovies(query);

            var remote = string.IsNullOrWhiteSpace(query)
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
        private List<UiMovie> LoadLocalMovies(string? query)
        {
            if (!File.Exists(_dbPath)) return new List<UiMovie>();

            try
            {
                // Read-only, and deliberately not Cache=Shared: a scan holds a write transaction,
                // and shared cache would fail this query outright instead of reading the last
                // committed snapshot.
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                conn.Open();

                string sql;
                object param;
                if (string.IsNullOrWhiteSpace(query))
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
                    param = new { q = query };
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
            if (_posterLoader is null) return;
            foreach (var m in movies)
            {
                // A server film is already described by the server, artwork included. Sending it
                // to TMDB would ask for an answer the app already has, and would make a Jellyfin
                // library depend on a TMDB key it has no reason to need.
                if (m.IsRemote) continue;
                if (!string.IsNullOrWhiteSpace(m.PosterPath)) continue;

                _ = _posterLoader.EnsurePosterAsync(
                        movieId: m.Id,
                        title: m.Title,
                        year: m.Year,
                        onFetched: path => Dispatcher.UIThread.Post(() => m.PosterPath = path),
                        ct: _cts.Token);
            }
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

        private async void Settings_Click(object? sender, RoutedEventArgs e)
        {
            var message =
                $"Configuration file:{Environment.NewLine}{Path.Combine(AppContext.BaseDirectory, AppConfig.FileName)}" +
                $"{Environment.NewLine}{Environment.NewLine}Database:{Environment.NewLine}{_dbPath}" +
                $"{Environment.NewLine}{Environment.NewLine}Watch folders:{Environment.NewLine}{string.Join(Environment.NewLine, _config.WatchFolders)}" +
                $"{Environment.NewLine}{Environment.NewLine}TMDB key: {(string.IsNullOrWhiteSpace(_config.TmdbApiKey) ? "not configured" : "configured")}" +
                $"{Environment.NewLine}IMDb ratings: {(_ratings.IsConfigured ? "available" : "unavailable")}" +
                $"{Environment.NewLine}{Environment.NewLine}Jellyfin:{Environment.NewLine}{DescribeJellyfin()}";

            await MessageBoxWindow.ShowAsync(this, "Settings", message);
        }

        /// <summary>
        /// What the settings box says about the server. Never the password, the API key or the
        /// session token — only whether one is present and which account it signs in as.
        /// </summary>
        private string DescribeJellyfin()
        {
            if (_jellyfin is null)
                return "not configured (set Jellyfin.ServerUrl in the configuration file)";

            var settings = _config.Jellyfin!;
            var credential = settings.UsesUserAccount
                ? $"signed in as {settings.Username}"
                : "using an API key";

            var player = MediaPlayerLauncher.Find();
            var playerNote = player is null
                ? "no video player installed — install VLC or IINA to play"
                : $"plays in {player.Name}";

            return $"{settings.ServerUrl} ({credential}){Environment.NewLine}" +
                   $"{_remoteMovies.Count} films cached · {playerNote}";
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
                vm.FilePath = FindLocalFileForMovie(m);

                var dlg = new MovieDetailsWindow(vm);
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

        private string? FindLocalFileForMovie(UiMovie m)
        {
            try
            {
                using var conn = Database.Open(_dbPath);
                var files = conn.Query<string>("SELECT file_path FROM files").ToList();
                return MovieFileMatcher.FindBestMatch(files, m.Title);
            }
            catch { return null; }
        }
    }
}
