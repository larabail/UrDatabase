using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>What the TMDB picker is choosing: a film, or a programme on this disk.</summary>
    public enum TmdbMatchKind
    {
        Film,
        Series
    }

    /// <summary>
    /// Everything the TMDB picker says that depends on what it is choosing, and which of TMDB's
    /// searches it runs. Out of the window so a test can hold the two modes apart without a UI
    /// thread: a series picker that said "film" anywhere would be the bug, and a series picker
    /// that searched the film catalogue would answer "Fargo" with the 1996 film.
    /// </summary>
    public sealed class TmdbMatchWording
    {
        public static TmdbMatchWording Film { get; } = new(
            TmdbMatchKind.Film,
            WindowTitle: "Which film is this?",
            Explanation: "UrDatabase matched this film to TMDB by its title. Search for the right one and choose it, and the poster, plot, cast and crew will all come from that film instead.",
            Watermark: "Film title",
            ChooseLabel: "Use this film",
            PickPrompt: "Pick the one that is this film.");

        public static TmdbMatchWording Series { get; } = new(
            TmdbMatchKind.Series,
            WindowTitle: "Which programme is this?",
            Explanation: "UrDatabase looks programmes up on TMDB by the name their folders give them, and leaves one alone when it cannot be sure. Search for the right one and choose it, and the name, poster, plot, cast and episode names will all come from that programme instead. The files are not touched.",
            Watermark: "Programme name",
            ChooseLabel: "Use this programme",
            PickPrompt: "Pick the one that is this programme.");

        private TmdbMatchWording(
            TmdbMatchKind kind,
            string WindowTitle,
            string Explanation,
            string Watermark,
            string ChooseLabel,
            string PickPrompt)
        {
            Kind = kind;
            this.WindowTitle = WindowTitle;
            this.Explanation = Explanation;
            this.Watermark = Watermark;
            this.ChooseLabel = ChooseLabel;
            this.PickPrompt = PickPrompt;
        }

        public TmdbMatchKind Kind { get; }
        public string WindowTitle { get; }
        public string Explanation { get; }
        public string Watermark { get; }
        public string ChooseLabel { get; }
        public string PickPrompt { get; }

        public static TmdbMatchWording For(TmdbMatchKind kind) => kind == TmdbMatchKind.Series ? Series : Film;

        /// <summary>What the status line says once a search has answered.</summary>
        public string Results(int count, string query) =>
            count == 0
                ? $"TMDB has nothing for “{query.Trim()}”. Try the {(Kind == TmdbMatchKind.Series ? "name" : "title")} in its own language, or without a subtitle."
                : $"{count} result{(count == 1 ? "" : "s")}. {PickPrompt}";

        /// <summary>
        /// The search this picker runs: TMDB's film catalogue or its television one, both
        /// unfiltered — the rules that stop the app choosing for itself must not hide the result
        /// somebody came here to pick.
        /// </summary>
        public Task<IReadOnlyList<TmdbMatch.Candidate>> SearchAsync(
            TmdbService tmdb, string query, int? year, CancellationToken ct) =>
            Kind == TmdbMatchKind.Series
                ? tmdb.SearchTvAsync(query, year, ct)
                : tmdb.SearchAsync(query, year, ct);
    }
}
