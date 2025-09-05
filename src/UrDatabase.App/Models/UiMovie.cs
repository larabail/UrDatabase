using System;
using System.Collections.Generic;
using System.Linq;

namespace UrDatabase.Models
{
    public class UiMovie
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public int? Year { get; set; }
        public string? Genres { get; set; }
        public string? PosterPath { get; set; }

        public IEnumerable<string> GenresList =>
            (Genres ?? "")
            .Replace('|', ',')
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(g => g.Trim())
            .Where(g => g.Length > 0);

        public bool HasGenre(string g) =>
            !string.IsNullOrWhiteSpace(g) &&
            GenresList.Any(x => string.Equals(x, g, StringComparison.OrdinalIgnoreCase));
    }
}
