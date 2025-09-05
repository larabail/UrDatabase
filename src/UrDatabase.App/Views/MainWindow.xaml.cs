using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Services;

namespace UrDatabase.Views
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<MovieRow> Movies { get; } = new();
        private AppConfig _config = new AppConfig();
        private string _dbPath = "";

        public MainWindow()
        {
            InitializeComponent();
            MovieList.ItemsSource = Movies;

            try
            {
                _config = AppConfig.Load(); // safe load: returns defaults on error
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Config error: {ex.Message}", "UrDatabase", MessageBoxButton.OK, MessageBoxImage.Warning);
                _config = new AppConfig();
            }

            _dbPath = _config.DatabasePath;

            if (File.Exists(_dbPath))
            {
                LoadMovies(_dbPath, "");
                StatusText.Text = "Ready.";
            }
            else
            {
                StatusText.Text = $"Database not found at: {_dbPath}";
            }

            SearchBox.TextChanged += (s, e) =>
            {
                Movies.Clear();
                var q = SearchBox.Text?.Trim();
                LoadMovies(_dbPath, q);
            };

            ScanButton.Click += async (s, e) => await RunScanAsync();
        }

        private void LoadMovies(string dbPath, string? query)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
                conn.Open();
                string sql;
                object param;
                if (string.IsNullOrWhiteSpace(query))
                {
                    sql = "SELECT id AS Id, title AS Title, year AS Year, genres AS Genres " +
                        "FROM movies ORDER BY COALESCE(year,0) DESC, title";
                    param = new { };
                }
                else
                {
                    sql = @"
SELECT m.id   AS Id,
       m.title AS Title,
       m.year  AS Year,
       m.genres AS Genres
FROM movies_fts f
JOIN movies m ON m.id = f.rowid
WHERE movies_fts MATCH @q
ORDER BY rank";
                    param = new { q = query };
                }

                foreach (var row in conn.Query<MovieRow>(sql, param))
                    Movies.Add(row);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load movies: {ex.Message}", "UrDatabase", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task RunScanAsync()
        {
            StatusText.Text = "Scanning...";
            try
            {
                using var conn = Database.Open(_dbPath);
                var scanner = new ScanService();
                var progress = new Progress<string>(msg => StatusText.Text = msg);
                var folders = _config.WatchFolders ?? Array.Empty<string>();
                var count = await scanner.ScanAsync(conn, folders, progress);
                StatusText.Text = $"Scan complete. Updated {count} file entries.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Scan failed: {ex.Message}";
            }
        }
    }

    public class MovieRow
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public int? Year { get; set; }     // nullable is important because many rows may have NULL
        public string? Genres { get; set; }
    }
}
