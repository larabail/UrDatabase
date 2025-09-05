using System;
using System.IO;
using System.Text.Json;

namespace UrDatabase.Services
{
    public class AppConfig
    {
        public bool DownloadPosters { get; set; } = false;   // false = use TMDb URL; true = cache to disk
        public string TmdbImageSize { get; set; } = "w342";  // common sizes: w185, w342, w500, original

        public string DatabasePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UrDatabase", "movies.db");
        public string[] WatchFolders { get; set; } = Array.Empty<string>();
        public string TmdbApiKey { get; set; } = "";
        public string PosterCacheDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UrDatabase", "posters");

        public static AppConfig Load(string? path = null)
        {
            try
            {
                var defaultPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                var file = path ?? defaultPath;
                if (!File.Exists(file)) return new AppConfig();

                var json = File.ReadAllText(file);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppConfig();

                cfg.DatabasePath = Expand(cfg.DatabasePath);
                cfg.PosterCacheDir = Expand(cfg.PosterCacheDir);
                if (cfg.WatchFolders != null)
                {
                    for (int i = 0; i < cfg.WatchFolders.Length; i++)
                        cfg.WatchFolders[i] = Expand(cfg.WatchFolders[i]);
                }
                return cfg;
            }
            catch
            {
                // fallback to defaults if json is invalid
                return new AppConfig();
            }
        }

        private static string Expand(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            return Environment.ExpandEnvironmentVariables(input);
        }
    }
}
