using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

        private List<UiMovie> _allMovies = new();

        private PosterAutoLoader? _posterLoader;
        private readonly System.Threading.CancellationTokenSource _cts = new();


        public MainWindow()
        {
            InitializeComponent();

            try { _config = AppConfig.Load(); } catch { _config = new AppConfig(); }
            _dbPath = _config.DatabasePath;

            _posterLoader = new PosterAutoLoader(_config, _dbPath, maxConcurrency: 4);
            this.Closed += (_, __) => { _cts.Cancel(); _posterLoader?.Dispose(); };

            DataContext = this;

            LoadMovies();
            BuildGenres();
            RebuildGroups();
            ShowAllGenres();
        }

        private void LoadMovies(string? query = null)
        {
            _allMovies.Clear();
            if (!File.Exists(_dbPath)) return;

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
            var hasPosters = _allMovies.Count(x => !string.IsNullOrWhiteSpace(x.PosterPath));
            Title = $"UrDatabase — Posters present: {hasPosters}/{_allMovies.Count}";
            WarmPosters(_allMovies);
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

        private void WarmPosters(System.Collections.Generic.IEnumerable<UiMovie> movies)
        {
            if (_posterLoader is null) return;
            foreach (var m in movies)
            {
                if (!string.IsNullOrWhiteSpace(m.PosterPath)) continue;
                _ = _posterLoader.EnsurePosterAsync(
                        movieId: m.Id,
                        title: m.Title,
                        year: m.Year,
                        onFetched: path => Dispatcher.Invoke(() => m.PosterPath = path),
                        ct: _cts.Token);
            }
        }


        private void RebuildGroups()
        {
            VisibleGroups.Clear();

            // Which genre buckets do we want to show?
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
                    Items = new System.Collections.ObjectModel.ObservableCollection<UiMovie>(items)
                });
            }

            // Warm posters for all visible groups (once)
            foreach (var group in VisibleGroups)
                WarmPosters(group.Items);
        }

        private void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var conn = Database.Open(_dbPath);
                var scanner = new ScanService();
                var progress = new Progress<string>(msg => { /* you can show status somewhere */ });
                scanner.ScanAsync(conn, _config.WatchFolders ?? Array.Empty<string>(), progress).ContinueWith(_ =>
                {
                    // no UI update required here for now
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Scan failed: {ex.Message}", "UrDatabase", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var q = (sender as System.Windows.Controls.TextBox)?.Text?.Trim();

            if (string.IsNullOrWhiteSpace(q))
            {
                // not searching → grouped view
                LoadMovies(null);
                BuildGenres();
                RebuildGroups();
                SetSearching(false);
                return;
            }

            // searching → flat view
            // searching → flat view
            LoadMovies(q); // fills _allMovies from DB
            FlatResults.Clear();

            // ensure no duplicates (by Id)
            foreach (var m in _allMovies
                    .GroupBy(x => x.Id)
                    .Select(g => g.First())
                    .OrderByDescending(x => x.Year ?? 0)
                    .ThenBy(x => x.Title))
            {
                FlatResults.Add(m);
            }

            // warm posters for the flat list once
            WarmPosters(FlatResults);
            ShowSearch();
        }

        private void GenreChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton tb && tb.Content is string genreLabel)
            {
                // Update the selected genre value
                SelectedGenre = genreLabel;

                // --- Toggle visual state: set only this chip to checked ---
                // Find the ItemsControl that hosts the chips
                var itemsControl = FindAncestor<ItemsControl>(tb);
                if (itemsControl != null)
                {
                    foreach (var container in FindVisualChildren<ContentPresenter>(itemsControl))
                    {
                        var btn = FindDescendant<ToggleButton>(container);
                        if (btn != null)
                            btn.IsChecked = object.Equals(btn.Content as string, SelectedGenre);
                    }
                }

                // --- Switch content panel and (re)populate data ---
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

        // ---------- visual tree helpers ----------
        private static TAncestor? FindAncestor<TAncestor>(DependencyObject? child) where TAncestor : DependencyObject
        {
            while (child != null)
            {
                child = System.Windows.Media.VisualTreeHelper.GetParent(child);
                if (child is TAncestor a) return a;
            }
            return null;
        }

        private static IEnumerable<TChild> FindVisualChildren<TChild>(DependencyObject depObj) where TChild : DependencyObject
        {
            if (depObj == null) yield break;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
                if (child is TChild c) yield return c;
                foreach (var c2 in FindVisualChildren<TChild>(child))
                    yield return c2;
            }
        }

        private static TDesc? FindDescendant<TDesc>(DependencyObject root) where TDesc : DependencyObject
        {
            foreach (var c in FindVisualChildren<TDesc>(root))
                return c;
            return null;
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Settings coming soon.", "UrDatabase");
        }

        public ObservableCollection<UiMovie> FlatResults { get; } = new();

        private void SetSearching(bool searching)
        {
            GroupPanel.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
            SearchPanel.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowSearch()
        {
            SearchPanel.Visibility = Visibility.Visible;
            GroupPanel.Visibility = Visibility.Collapsed;
            SingleGenrePanel.Visibility = Visibility.Collapsed;
        }

        private void ShowAllGenres()
        {
            SearchPanel.Visibility = Visibility.Collapsed;
            GroupPanel.Visibility = Visibility.Visible;
            SingleGenrePanel.Visibility = Visibility.Collapsed;
        }

        private void ShowSingleGenre()
        {
            SearchPanel.Visibility = Visibility.Collapsed;
            GroupPanel.Visibility = Visibility.Collapsed;
            SingleGenrePanel.Visibility = Visibility.Visible;
        }
    }
}
