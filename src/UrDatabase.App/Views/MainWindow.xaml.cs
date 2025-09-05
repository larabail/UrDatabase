using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Dapper;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Views
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<MovieRow> Movies { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            MovieList.ItemsSource = Movies;

            // Load on startup if DB exists
            var dbPath = ResolveDbPath();
            if (File.Exists(dbPath))
            {
                LoadMovies(dbPath, "");
            }

            SearchBox.TextChanged += (s, e) =>
            {
                Movies.Clear();
                var q = SearchBox.Text?.Trim();
                LoadMovies(dbPath, q);
            };
        }

        private static string ResolveDbPath()
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UrDatabase");
            Directory.CreateDirectory(baseDir);
            return Path.Combine(baseDir, "movies.db");
        }

        private void LoadMovies(string dbPath, string? query)
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
            conn.Open();
            string sql;
            object param;
            if (string.IsNullOrWhiteSpace(query))
            {
                sql = "SELECT id, title, year, genres FROM movies ORDER BY COALESCE(year,0) DESC, title";
                param = new { };
            }
            else
            {
                sql = @"SELECT m.id, m.title, m.year, m.genres
                        FROM movies_fts f
                        JOIN movies m ON m.id = f.rowid
                        WHERE movies_fts MATCH @q
                        ORDER BY rank";
                param = new { q = query };
            }

            foreach (var row in conn.Query<MovieRow>(sql, param))
                Movies.Add(row);
        }
    }

    public record MovieRow(long Id, string Title, int? Year, string? Genres);
}
