using System.Collections.Generic;
using System.Globalization;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// The small line under a card's title: <c>"2025 · 2h 15m · ★ 7.6"</c> for a film,
    /// <c>"2011 · 3 seasons · ★ 8.4"</c> for a series, and where in its programme an episode sits.
    /// </summary>
    /// <remarks>
    /// Each part is printed only when it is known, and the separators are put between the parts
    /// that are left, so a film with no runtime reads <c>"2025 · ★ 7.6"</c> rather than
    /// <c>"2025 ·  · ★ 7.6"</c>.
    ///
    /// The star is IMDb's and nothing else's. A card also often knows a TMDB average or a Jellyfin
    /// community rating, and those are deliberately left off the line rather than given a star of
    /// their own: at 10.5px a filled star and a hollow one are the same mark, and this app has
    /// already shipped one bug from printing one service's rating as another's. The details screen
    /// names every rating by its source, and the library's rating filter still uses whichever
    /// number the card has.
    ///
    /// A series gets no runtime, because a programme has episodes of a length rather than a length
    /// of its own. An episode keeps its place in the programme and nothing else: the card has room
    /// for that and how much is left, and the rest is on its tooltip.
    /// </remarks>
    public static class CardMeta
    {
        public const string Separator = " · ";

        /// <summary>The line for <paramref name="card"/>, or empty when it knows nothing worth saying.</summary>
        public static string Line(UiMovie? card)
        {
            if (card is null) return "";

            if (card.IsEpisode)
            {
                // The name only when the server numbered nothing at all, which is the one case
                // where it is the only thing that distinguishes this card from its siblings.
                var label = card.EpisodeLabel;
                return label.Length > 0 ? label : (card.EpisodeTitle ?? "").Trim();
            }

            var parts = new List<string>(3);

            if (card.Year is int year) parts.Add(year.ToString(CultureInfo.InvariantCulture));

            parts.Add(card.IsSeries ? Seasons(card.SeasonCount) : Runtime(card.RuntimeMinutes));
            parts.Add(Rating(card.ImdbRating));

            return string.Join(Separator, parts.FindAll(p => p.Length > 0));
        }

        /// <summary>
        /// A running time as the card prints it: <c>"2h 15m"</c>, <c>"2h"</c>, <c>"45m"</c>.
        /// Empty when nothing has said how long the film is.
        /// </summary>
        public static string Runtime(int? minutes)
        {
            if (minutes is not int total || total <= 0) return "";

            var hours = total / 60;
            var rest = total % 60;

            if (hours == 0) return $"{rest.ToString(CultureInfo.InvariantCulture)}m";
            if (rest == 0) return $"{hours.ToString(CultureInfo.InvariantCulture)}h";

            return $"{hours.ToString(CultureInfo.InvariantCulture)}h {rest.ToString(CultureInfo.InvariantCulture)}m";
        }

        /// <summary>
        /// An IMDb rating as the card prints it: <c>"★ 7.6"</c>. Empty when there is none, or when
        /// the number is not one IMDb could have given.
        /// </summary>
        /// <remarks>
        /// Takes only the IMDb rating, by name, so a caller cannot hand it a TMDB average and have
        /// the card star it as IMDb's.
        /// </remarks>
        public static string Rating(double? imdbRating) =>
            imdbRating is double r && r > 0 && r <= 10
                ? $"★ {r.ToString("0.0", CultureInfo.InvariantCulture)}"
                : "";

        /// <summary><c>"1 season"</c>, <c>"3 seasons"</c>, or empty when the count is unknown.</summary>
        public static string Seasons(int? count) =>
            count is int n && n > 0
                ? n == 1 ? "1 season" : $"{n.ToString(CultureInfo.InvariantCulture)} seasons"
                : "";
    }
}
