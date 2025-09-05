using System;
using System.Collections.Generic;
using System.ComponentModel;
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
