using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;

namespace UrDatabase.Views
{
    public partial class MainWindow : Window
    {
        private AppConfig _config = new AppConfig();
        private string _dbPath = "";

        public ObservableCollection<string> Genres { get; } = new();
        public string? SelectedGenre { get; set; } = "All";
        public ObservableCollection<GenreGroup> VisibleGroups { get; } = new();

        private List<UiMovie> _allMovies = new();

        public MainWindow()
        {
            InitializeComponent();

            try { _config = AppConfig.Load(); } catch { _config = new AppConfig(); }
            _dbPath = _config.DatabasePath;

            DataContext = this;

            LoadMovies();
            BuildGenres();
            RebuildGroups();
            SetSearching(false);
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


        private void RebuildGroups()
        {
            VisibleGroups.Clear();

            // Which genre buckets do we want to show?
            IEnumerable<string> buckets;
            if (string.Equals(SelectedGenre, "All", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(SelectedGenre))
                buckets = Genres.Where(g => !string.Equals(g, "All", StringComparison.OrdinalIgnoreCase));
            else
                buckets = new[] { SelectedGenre! };

            foreach (var g in buckets)
            {
                var items = _allMovies
                    .Where(m => m.HasGenre(g))
                    // optional sorting within a row:
                    .OrderByDescending(m => m.Year ?? 0)
                    .ThenBy(m => m.Title)
                    .ToList();

                if (items.Count == 0) continue;

                VisibleGroups.Add(new GenreGroup
                {
                    Name = $"{g} ({items.Count} items)",
                    Items = new System.Collections.ObjectModel.ObservableCollection<UiMovie>(items)
                });
            }
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
            LoadMovies(q); // this fills _allMovies from DB
            FlatResults.Clear();

            // ensure no duplicates (by Id)
            foreach (var m in _allMovies
                    .GroupBy(m => m.Id)
                    .Select(g => g.First())
                    .OrderByDescending(m => m.Year ?? 0)
                    .ThenBy(m => m.Title))
            {
                FlatResults.Add(m);
            }

            SetSearching(true);
        }


        private void GenreChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Content is string g)
            {
                SelectedGenre = g;
                RebuildGroups();
            }
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

    }
}
