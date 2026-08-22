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
        public string? SelectedGenre { get; set; } = "All";
        public ObservableCollection<GenreGroup> VisibleGroups { get; } = new();
        public ObservableCollection<UiMovie> FlatResults { get; } = new();

        private List<UiMovie> _allMovies = new();

        private PosterAutoLoader? _posterLoader;
        private readonly ImdbRatingService _ratings;
        private readonly CancellationTokenSource _cts = new();

        public MainWindow()
        {
            InitializeComponent();

            try { _config = AppConfig.Load(); } catch { _config = new AppConfig(); }
            _dbPath = _config.DatabasePath;

            // Constructed eagerly but idle: no request is made until a movie is opened, and none
            // at all when no OMDb key is available.
            _ratings = new ImdbRatingService(new OmdbService(_config.OmdbApiKey), ownsLookup: true);

            _posterLoader = new PosterAutoLoader(_config, _dbPath, maxConcurrency: 4);
            Closed += (_, __) => { _cts.Cancel(); _posterLoader?.Dispose(); _ratings.Dispose(); };

            DataContext = this;

            LoadMovies();
            BuildGenres();
            RebuildGroups();
            ShowAllGenres();
        }

        private void LoadMovies(string? query = null)
        {
            _allMovies.Clear();
            if (!File.Exists(_dbPath))
            {
                SetStatus($"No library yet. Expected a database at {_dbPath}.");
                return;
            }

            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
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

                _allMovies = conn.Query<UiMovie>(sql, param).ToList();
            }
            catch (Exception ex)
            {
                // A database from an older build may lack the tables this query needs.
                // Report it instead of taking the window down.
                AppLog.Write("startup.log", $"LoadMovies failed: {ex}");
                SetStatus($"Could not read the library: {ex.Message}");
                _allMovies = new List<UiMovie>();
                return;
            }

            var hasPosters = _allMovies.Count(x => !string.IsNullOrWhiteSpace(x.PosterPath));
            SetStatus($"Posters present: {hasPosters}/{_allMovies.Count}");
            WarmPosters(_allMovies);
        }

        private void SetStatus(string message)
        {
            Title = $"UrDatabase — {message}";
            if (StatusText is not null) StatusText.Text = message;
        }

        private void BuildGenres()
        {
            Genres.Clear();
            Genres.Add("All");

            var all = _allMovies
                .SelectMany(m => m.GenresList)
                .Select(g => g.Trim())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g);

            foreach (var g in all)
                Genres.Add(g);
        }

        private void WarmPosters(IEnumerable<UiMovie> movies)
        {
            if (_posterLoader is null) return;
            foreach (var m in movies)
            {
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
            if (string.Equals(SelectedGenre, "All", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(SelectedGenre))
                buckets = Genres.Where(x => !string.Equals(x, "All", StringComparison.OrdinalIgnoreCase));
            else
                buckets = new[] { SelectedGenre! };

            foreach (var genre in buckets)
            {
                var items = _allMovies
                    .Where(m => m.HasGenre(genre))
                    .OrderByDescending(m => m.Year ?? 0)
                    .ThenBy(m => m.Title)
                    .ToList();

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

        private async void ScanButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                using var conn = Database.Open(_dbPath);
                var scanner = new ScanService();
                var progress = new Progress<string>(msg => Dispatcher.UIThread.Post(() => SetStatus(msg)));

                var updated = await scanner.ScanAsync(conn, _config.WatchFolders ?? Array.Empty<string>(), progress, _cts.Token);

                LoadMovies();
                BuildGenres();
                RebuildGroups();
                ShowAllGenres();
                SetStatus($"Scan complete. {updated} file entries updated.");
            }
            catch (Exception ex)
            {
                await MessageBoxWindow.ShowAsync(this, "UrDatabase", $"Scan failed: {ex.Message}");
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

            foreach (var m in _allMovies
                    .GroupBy(x => x.Id)
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

                if (string.Equals(SelectedGenre, "All", StringComparison.OrdinalIgnoreCase))
                {
                    RebuildGroups();
                    ShowAllGenres();
                }
                else
                {
                    SingleGenreItems.Clear();
                    foreach (var m in _allMovies
                                .Where(m => m.HasGenre(SelectedGenre))
                                .OrderByDescending(m => m.Year ?? 0)
                                .ThenBy(m => m.Title))
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
                $"{Environment.NewLine}IMDb ratings: {(_ratings.IsConfigured ? "available" : "unavailable")}";

            await MessageBoxWindow.ShowAsync(this, "Settings", message);
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
        /// IMDb ratings come from OMDb, matched on the IMDb id TMDB reports. Entirely optional: no
        /// id, no key or no network simply means no rating, never a substitute from another source.
        /// </summary>
        private async Task<double?> LoadImdbRatingAsync(string? imdbId, long movieId, CancellationToken ct)
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
