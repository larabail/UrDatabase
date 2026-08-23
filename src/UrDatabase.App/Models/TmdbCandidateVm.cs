using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using UrDatabase.Services;

namespace UrDatabase.Models
{
    /// <summary>
    /// One row in the "Wrong film?" picker: a TMDB result as a person needs to see it in order to
    /// recognise it. The poster does most of the work, so the row carries a bitmap that is filled
    /// in once it has been fetched rather than blocking the list on the network.
    /// </summary>
    public sealed class TmdbCandidateVm : INotifyPropertyChanged
    {
        public int TmdbId { get; init; }

        public string Title { get; init; } = "";

        /// <summary>
        /// The year, or a sentence saying there isn't one. Blank would read as a rendering fault
        /// next to rows that have one, and the absence is genuinely useful: a result TMDB cannot
        /// date is one to be more careful about choosing.
        /// </summary>
        public string YearLabel { get; init; } = "";

        /// <summary>
        /// The original title, shown only when it differs from <see cref="Title"/>. It is the line
        /// that resolves the case this whole feature exists for — a film catalogued under its own
        /// language's name and listed by TMDB under a translation of it.
        /// </summary>
        public string? OriginalTitle { get; init; }

        public bool HasOriginalTitle => !string.IsNullOrWhiteSpace(OriginalTitle);

        public string Overview { get; init; } = "";

        /// <summary>The full artwork URL, or null when TMDB has no poster for this result.</summary>
        public string? PosterUrl { get; init; }

        /// <summary>TMDB's own relative poster path, which is what gets stored for the film.</summary>
        public string? PosterPath { get; init; }

        private Bitmap? _poster;
        public Bitmap? Poster
        {
            get => _poster;
            set { if (_poster != value) { _poster = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Builds a row from a search result. Separate from the window so the wording can be
        /// asserted on without a UI thread.
        /// </summary>
        /// <param name="imageUrl">
        /// Turns TMDB's relative poster path into a URL. Passed in rather than built here so the
        /// configured image size is respected and no test needs a network.
        /// </param>
        public static TmdbCandidateVm From(TmdbMatch.Candidate candidate, System.Func<string, string> imageUrl)
        {
            var title = string.IsNullOrWhiteSpace(candidate.Title) ? candidate.OriginalTitle : candidate.Title;
            var original = string.IsNullOrWhiteSpace(candidate.OriginalTitle) ||
                           string.Equals(candidate.OriginalTitle, title, System.StringComparison.Ordinal)
                ? null
                : candidate.OriginalTitle;

            return new TmdbCandidateVm
            {
                TmdbId = candidate.Id,
                Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title!,
                OriginalTitle = original,
                YearLabel = candidate.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Year unknown",
                Overview = string.IsNullOrWhiteSpace(candidate.Overview)
                    ? "TMDB has no plot summary for this one."
                    : candidate.Overview!,
                PosterPath = candidate.PosterPath,
                PosterUrl = string.IsNullOrWhiteSpace(candidate.PosterPath) ? null : imageUrl(candidate.PosterPath!)
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
