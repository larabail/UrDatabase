using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// What the series screen shows for a programme on this disk: the card it was opened from,
    /// with whatever TMDB has said about it laid over the top, and the sentences that stand in
    /// for anything still unknown.
    /// </summary>
    /// <remarks>
    /// Out of the view so the wording can be tested, and because the wording is a rule: with no
    /// TMDB key a local programme must read exactly as it did before programmes could be looked
    /// up, and with one it must say which of "not looked up", "not confirmed" and "TMDB has
    /// nothing" is the case — three states a screen built inline would blur into one.
    /// </remarks>
    public static class LocalSeriesPage
    {
        public const string ServerAttribution =
            "Metadata and artwork supplied by your Jellyfin server. IMDb rating retrieved from the OMDb API; neither IMDb nor OMDb endorses this application.";

        public const string FilesAttribution =
            "Found on this computer: names and episode numbers come from the files and the folders they are in.";

        public const string TmdbAttribution =
            "Found on this computer: episode numbers come from the files. Description and artwork from TMDB. This product uses the TMDB API but is not endorsed or certified by TMDB. IMDb rating retrieved from the OMDb API; neither IMDb nor OMDb endorses this application.";

        /// <summary>
        /// The screen's model for a local programme. The card supplies what the library already
        /// showed — title, year, counts, the poster it is wearing — and the catalogue row supplies
        /// TMDB's description when there is one.
        /// </summary>
        public static SeriesDetailsVm Build(UiMovie card, LocalSeriesDetails? details, bool tmdbConfigured)
        {
            var vm = new SeriesDetailsVm
            {
                LocalId = card.Id,
                Title = card.Title,
                Year = card.Year,
                Genres = card.Genres ?? "",
                PosterPath = card.DisplayPosterPath,
                SeasonCount = card.SeasonCount,
                EpisodeCount = card.EpisodeCount,
                TmdbConfigured = tmdbConfigured
            };

            if (details is null) return vm;

            if (string.IsNullOrWhiteSpace(vm.Genres) && !string.IsNullOrWhiteSpace(details.Genres))
                vm.Genres = details.Genres!;
            if (string.IsNullOrWhiteSpace(vm.PosterPath) && PosterAutoLoader.HasArtwork(details.PosterPath))
                vm.PosterPath = details.PosterPath;

            if (!details.Described) return vm;

            vm.TmdbId = details.TmdbId;
            vm.Overview = details.Overview ?? "";
            vm.ImdbId = details.ImdbId;
            vm.BackdropUrl = LocalSeriesLookup.BackdropUrl(details.BackdropPath);
            vm.TopCast = details.Cast.ToList();
            vm.KeyCrew = details.Crew.ToList();

            return vm;
        }

        /// <summary>Who to credit, under the title.</summary>
        public static string Attribution(SeriesDetailsVm vm) =>
            !vm.IsLocal ? ServerAttribution
            : vm.IsIdentified ? TmdbAttribution
            : FilesAttribution;

        /// <summary>What stands in for a summary the programme does not have.</summary>
        public static string MissingOverview(SeriesDetailsVm vm) =>
            !vm.IsLocal ? "The server has no summary for this programme."
            : vm.IsIdentified ? "TMDB has no summary for this programme."
            : vm.TmdbConfigured ? "No summary yet: TMDB has not confirmed which programme this is."
            : "No summary yet: this programme was found on this computer and has not been looked up.";

        /// <summary>What stands in for a cast list, on a local programme. A server programme keeps its own wording.</summary>
        public static string MissingCast(SeriesDetailsVm vm) =>
            vm.IsIdentified ? "TMDB lists no cast for this programme."
            : vm.TmdbConfigured ? "Not known until TMDB has confirmed which programme this is."
            : "Not known yet for a programme found on this computer.";
    }
}
