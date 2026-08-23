using System.Globalization;

namespace UrDatabase.Services
{
    /// <summary>
    /// What a card shows when it has no artwork to show.
    /// </summary>
    /// <remarks>
    /// A service rather than three lines inside <c>PosterCard</c>, because the rule is not "no
    /// poster path, no poster". A card can be given a perfectly good URL and still end up here:
    /// a poster deleted out of the cache, a URL that 404s, or a whole Jellyfin server that cannot
    /// be reached — and that last one is now the common case rather than the exotic one, because
    /// every television card in the library is a server card.
    ///
    /// The bug this was extracted from was exactly that distinction. The plate was filled in only
    /// on the branch where it was already visible, so a card whose artwork failed to arrive
    /// revealed a plate that had never been written to, and a library browsed away from its
    /// server showed a wall of cards all reading "Untitled".
    /// </remarks>
    public static class PosterPlate
    {
        /// <summary>The caption for a card with no artwork.</summary>
        public const string NoTitle = "Untitled";

        /// <summary>
        /// Whether the plate is what the card should be showing.
        /// </summary>
        /// <param name="sourcePath">The artwork the card was given, if any.</param>
        /// <param name="hasArtwork">
        /// Whether that artwork actually decoded. False both before a fetch finishes and after
        /// one that came back with nothing.
        /// </param>
        public static bool ShouldShow(string? sourcePath, bool hasArtwork)
            => string.IsNullOrWhiteSpace(sourcePath) || !hasArtwork;

        /// <summary>
        /// What to print on it. A title is all the app knows at that point, and a card that
        /// admits what it knows is more use than an empty rectangle.
        /// </summary>
        public static string Caption(string? title)
            => string.IsNullOrWhiteSpace(title) ? NoTitle : title.Trim();

        /// <summary>The year under it, or empty when there is none to print.</summary>
        public static string YearLabel(int? year)
            => year?.ToString(CultureInfo.InvariantCulture) ?? "";
    }
}
