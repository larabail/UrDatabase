using System;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Fills the gaps in a film's details from the copy a server holds.
    ///
    /// Needed because a film in both places is now one card, opened as a local film: it plays from
    /// disk, links to a file and can have its TMDB match corrected. Left at that, folding the two
    /// cards together would have <em>lost</em> something — the server describes its own films
    /// completely, and that description used to be a click away on the second card. On an install
    /// with no TMDB key it is the only description there is, and the whole point of the Jellyfin
    /// support is that such an install works.
    ///
    /// Only gaps are filled. Anything the catalogue or TMDB already answered wins, because that is
    /// the half a person can correct: <b>Wrong film?</b> rewrites the local match, and a server's
    /// overview quietly overwriting it would make the correction look like it had not taken.
    /// </summary>
    public static class ServerDetails
    {
        /// <param name="backdropUrl">
        /// Builds a backdrop URL from an item id. Supplied by the caller because it needs the
        /// server address, which belongs to configuration rather than to the film.
        /// </param>
        public static void FillGaps(MovieDetailsVm? vm, JellyfinMovie? server, Func<string, string?>? backdropUrl = null)
        {
            if (vm is null || server is null) return;

            if (string.IsNullOrWhiteSpace(vm.Overview)) vm.Overview = server.Overview ?? "";
            if (string.IsNullOrWhiteSpace(vm.Genres)) vm.Genres = server.Genres ?? "";
            if (string.IsNullOrWhiteSpace(vm.ImdbId)) vm.ImdbId = server.ImdbId;
            if (vm.Runtime is null || vm.Runtime <= 0) vm.Runtime = server.RuntimeMinutes;
            if (vm.TopCast.Count == 0) vm.TopCast = server.Cast.ToList();
            if (vm.KeyCrew.Count == 0) vm.KeyCrew = server.Crew.ToList();

            if (string.IsNullOrWhiteSpace(vm.BackdropUrl) && !string.IsNullOrWhiteSpace(server.ItemId))
                vm.BackdropUrl = backdropUrl?.Invoke(server.ItemId);

            // Not a gap being filled: Jellyfin's community rating is a number nothing else in the
            // app produces, and it is printed under Jellyfin's own name beside the IMDb one rather
            // than standing in for it.
            vm.CommunityRating ??= server.CommunityRating;

            // Media info is deliberately NOT filled from the server, even when the local file's
            // name says nothing. The badges describe the copy Play will open, and for a film in
            // both places that is the one on this disk. The server's measurement is of its own
            // file, which may well be the 4K remux where this disk holds a 1080p web rip — and a
            // row of badges that describes a copy the user is not about to watch is worse than no
            // badges at all.
        }
    }
}
