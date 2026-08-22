using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace UrDatabase.Models
{
    public class UiMovie : INotifyPropertyChanged
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public int? Year { get; set; }
        public string? Genres { get; set; }

        /// <summary>
        /// Local unless it came from a server. Defaults to <see cref="MovieSource.Local"/> so
        /// every row Dapper materialises from the <c>movies</c> table is right without the query
        /// having to say so.
        /// </summary>
        public MovieSource Source { get; set; } = MovieSource.Local;

        /// <summary>The Jellyfin item id, for a remote film. Null for a local one.</summary>
        public string? RemoteId { get; set; }

        public bool IsRemote => Source == MovieSource.Jellyfin;

        /// <summary>The badge shown on the card. Short, because it sits over the poster.</summary>
        public string SourceLabel => IsRemote ? "Server" : "Local";

        /// <summary>
        /// Identity across both sources. Local rows have an autoincrement id and remote ones a
        /// GUID from Jellyfin, so neither alone can deduplicate a mixed list — every remote film
        /// carries id 0 and would collapse into a single entry if grouped by that.
        /// </summary>
        public string Key => IsRemote
            ? $"jellyfin:{RemoteId}"
            : $"local:{Id.ToString(CultureInfo.InvariantCulture)}";

        private string? _posterPath;
        public string? PosterPath
        {
            get => _posterPath;
            set { if (_posterPath != value) { _posterPath = value; OnPropertyChanged(); } }
        }

        public IEnumerable<string> GenresList =>
            (Genres ?? "")
            .Replace('|', ',')
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(g => g.Trim())
            .Where(g => g.Length > 0);

        public bool HasGenre(string g) =>
            !string.IsNullOrWhiteSpace(g) &&
            GenresList.Any(x => string.Equals(x, g, StringComparison.OrdinalIgnoreCase));

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
