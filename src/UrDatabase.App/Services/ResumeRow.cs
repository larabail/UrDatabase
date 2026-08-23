using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Decides what belongs in the Continue watching row, in what order, and how far through each
    /// film it says you are.
    /// </summary>
    /// <remarks>
    /// Pure, and out of the window, for the same reason <see cref="LibraryGrouping"/> is: this is
    /// the first row a person sees and every rule in it is a claim that can be wrong. A film with
    /// no position must not appear. A resume entry for something the movie library does not hold —
    /// a television episode, a film in a library this app was not pointed at — has no card to be
    /// and must be dropped rather than invented. And the row has to be built from what is already
    /// on screen, so a film held both here and on the server appears once, badged, exactly as it
    /// does on every shelf below.
    ///
    /// It matches on the Jellyfin item id, never on a title. The library has already done the work
    /// of deciding which local card is which server film, and redoing it here on the name would
    /// disagree with it for precisely the films that are hardest to match.
    /// </remarks>
    public static class ResumeRow
    {
        /// <summary>The shelf heading. Not a genre, and never offered as a chip.</summary>
        public const string Heading = "Continue watching";

        /// <summary>
        /// The row, in the server's own order.
        /// </summary>
        /// <remarks>
        /// Every card in <paramref name="library"/> has its progress cleared first, so this is
        /// idempotent and a film that has since been finished loses its mark rather than keeping
        /// one from a previous build. That matters because the window rebuilds its shelves from
        /// the same card objects when the source row is clicked, without reloading anything.
        /// </remarks>
        public static IReadOnlyList<UiMovie> Build(
            IEnumerable<UiMovie>? library,
            IEnumerable<JellyfinResumeItem>? resume)
        {
            var cards = library as IReadOnlyList<UiMovie> ?? library?.ToList() ?? (IReadOnlyList<UiMovie>)Array.Empty<UiMovie>();

            foreach (var card in cards)
            {
                if (card is null) continue;
                card.ResumeFraction = null;
                card.ResumeNote = null;
            }

            if (resume is null) return Array.Empty<UiMovie>();

            // First card wins for an id. Merge already guarantees one card per film, but a list
            // the window has not deduplicated must not produce the same film twice in the row.
            var byRemoteId = new Dictionary<string, UiMovie>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in cards)
            {
                if (card?.RemoteId is not { Length: > 0 } id) continue;
                byRemoteId.TryAdd(id, card);
            }

            var row = new List<UiMovie>();
            var used = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in resume.Where(Qualifies).OrderBy(e => e.SortOrder))
            {
                if (!byRemoteId.TryGetValue(entry.ItemId.Trim(), out var card)) continue;
                if (!used.Add(card.Key)) continue;

                card.ResumeFraction = PlaybackPosition.Fraction(
                    entry.PositionTicks,
                    entry.RuntimeTicks,
                    entry.PlayedPercentage);

                card.ResumeNote = PlaybackPosition.Describe(
                    entry.PositionTicks,
                    entry.RuntimeTicks,
                    entry.PlayedPercentage);

                row.Add(card);
            }

            return row;
        }

        /// <summary>
        /// Whether an entry describes a film somebody is part way through.
        /// </summary>
        /// <remarks>
        /// A position of zero is a film that was opened and closed, or one the server listed for
        /// its own reasons; showing it under "Continue watching" would be inviting somebody to
        /// carry on with something they never started. A second is the floor rather than a tick,
        /// because a player that has just been pointed at a stream reports a position long before
        /// anybody has watched anything.
        /// </remarks>
        public static bool Qualifies(JellyfinResumeItem? entry) =>
            entry is not null &&
            !string.IsNullOrWhiteSpace(entry.ItemId) &&
            entry.PositionTicks >= PlaybackPosition.MinimumMeaningfulTicks;
    }
}
