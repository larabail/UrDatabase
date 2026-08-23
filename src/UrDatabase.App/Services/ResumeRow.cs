using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Decides what belongs in the Continue watching row, in what order, and how far through each
    /// thing it says you are.
    /// </summary>
    /// <remarks>
    /// Pure, and out of the window, for the same reason <see cref="LibraryGrouping"/> is: this is
    /// the first row a person sees and every rule in it is a claim that can be wrong. Something
    /// with no position must not appear. An entry the library cannot place — a film in a library
    /// this app was not pointed at, an episode of a programme it has never heard of — has no card
    /// to be and must be dropped rather than invented. Something the owner has dismissed must stay
    /// out until the server says they have watched more of it. And the row has to be built from
    /// what is already on screen, so a film held both here and on the server appears once, badged,
    /// exactly as it does on every shelf below.
    ///
    /// It matches on the Jellyfin item id, never on a title. The library has already done the work
    /// of deciding which local card is which server film, and redoing it here on the name would
    /// disagree with it for precisely the films that are hardest to match.
    ///
    /// A film and an episode are resolved differently, and have to be. A film is an existing card,
    /// found by its own id and marked in place, so the same object appears in the row and on the
    /// Drama shelf carrying one progress mark. An episode has no card anywhere — the library wall
    /// lists programmes, not episodes — so one is built here from what the server said, borrowing
    /// its programme's poster so that it sits beside the films as a sibling rather than as a 16:9
    /// still among 2:3 plates.
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
        ///
        /// A programme never takes a mark from an episode of it. "Half way through" is a fact
        /// about one episode, and a rule under a series poster would be claiming something about
        /// a hundred hours of television on the strength of twenty minutes.
        /// </remarks>
        /// <param name="dismissals">
        /// What the owner has taken out of the row. Applied before anything else, so a dismissed
        /// entry is not merely hidden — it never becomes a card, and never marks one.
        /// </param>
        public static IReadOnlyList<UiMovie> Build(
            IEnumerable<UiMovie>? library,
            IEnumerable<JellyfinResumeItem>? resume,
            IEnumerable<ResumeDismissal>? dismissals = null)
        {
            var cards = library as IReadOnlyList<UiMovie> ?? library?.ToList() ?? (IReadOnlyList<UiMovie>)Array.Empty<UiMovie>();

            foreach (var card in cards)
            {
                if (card is null) continue;
                card.ResumeFraction = null;
                card.ResumeNote = null;
                card.ResumePositionTicks = 0;
            }

            if (resume is null) return Array.Empty<UiMovie>();

            // First card wins for an id. Merge already guarantees one card per film, but a list
            // the window has not deduplicated must not produce the same film twice in the row.
            // Programmes are indexed apart from films: an episode resolves through its show, and
            // looking one up in a single map would let a series answer for a film id.
            var byRemoteId = new Dictionary<string, UiMovie>(StringComparer.OrdinalIgnoreCase);
            var seriesByRemoteId = new Dictionary<string, UiMovie>(StringComparer.OrdinalIgnoreCase);

            foreach (var card in cards)
            {
                if (card?.RemoteId is not { Length: > 0 } id) continue;

                if (card.IsSeries) seriesByRemoteId.TryAdd(id, card);
                else byRemoteId.TryAdd(id, card);
            }

            var row = new List<UiMovie>();
            var used = new HashSet<string>(StringComparer.Ordinal);

            var showing = ResumeDismissals.Apply(resume, dismissals);

            foreach (var entry in showing.Where(Qualifies).OrderBy(e => e.SortOrder))
            {
                var card = entry.IsEpisode
                    ? BuildEpisodeCard(entry, seriesByRemoteId)
                    : byRemoteId.TryGetValue(entry.ItemId.Trim(), out var film) ? film : null;

                if (card is null) continue;
                if (!used.Add(card.Key)) continue;

                card.ResumeFraction = PlaybackPosition.Fraction(
                    entry.PositionTicks,
                    entry.RuntimeTicks,
                    entry.PlayedPercentage);

                card.ResumeNote = PlaybackPosition.Describe(
                    entry.PositionTicks,
                    entry.RuntimeTicks,
                    entry.PlayedPercentage);

                // The seek target, kept exact. Everything above is for drawing and reading.
                card.ResumePositionTicks = entry.PositionTicks;

                row.Add(card);
            }

            return row;
        }

        /// <summary>
        /// One episode as a card, or null when the library cannot place it.
        /// </summary>
        /// <remarks>
        /// Resolved through the programme rather than through the episode, because the programme
        /// is the thing the library actually holds: its card carries the poster, and its presence
        /// is what says this episode belongs to a library the viewer is currently looking at. So
        /// narrowing the wall to films, or to one genre, takes the episodes out of the row along
        /// with everything else, instead of leaving the first shelf describing a library nothing
        /// else on the page is showing.
        ///
        /// An episode of a programme the cache has never seen — television that was never synced,
        /// a library this app was not pointed at — is dropped, exactly as an unplaceable film is.
        /// Rendering it from the resume entry alone would put a card on the first shelf that
        /// nothing else in the app can open.
        ///
        /// The card is built fresh on every call and belongs to nothing else, so unlike a film's
        /// it has no previous mark to be cleared off it.
        /// </remarks>
        private static UiMovie? BuildEpisodeCard(
            JellyfinResumeItem entry,
            IReadOnlyDictionary<string, UiMovie> seriesByRemoteId)
        {
            if (string.IsNullOrWhiteSpace(entry.SeriesId)) return null;
            if (!seriesByRemoteId.TryGetValue(entry.SeriesId.Trim(), out var series)) return null;

            // The cached programme's name, and the resume entry's only as a fallback: the card and
            // the shelf below it should not be able to disagree about what a show is called.
            var title = !string.IsNullOrWhiteSpace(series.Title) ? series.Title : entry.SeriesName;
            if (string.IsNullOrWhiteSpace(title)) return null;

            return new UiMovie
            {
                Id = 0,
                Source = MovieSource.Jellyfin,
                Kind = MediaKind.Episode,

                // The episode's own id: what streams, and what a playback report is about. The
                // programme's is kept beside it, because clicking the card opens the show.
                RemoteId = entry.ItemId.Trim(),
                SeriesId = series.RemoteId,

                Title = title,
                SeasonNumber = entry.SeasonNumber,
                EpisodeNumber = entry.EpisodeNumber,
                EpisodeTitle = entry.Name,

                // Borrowed rather than fetched. It is the programme's poster, already on screen
                // below, and asking the server for the episode's own still would be a second
                // request for a worse picture in the wrong shape. In RemotePosterPath rather than
                // PosterPath because nothing local owns it — the same distinction a server film
                // makes.
                RemotePosterPath = series.DisplayPosterPath,

                // Deliberately no year and no genres. A card prints one meta line, and for an
                // episode that line is where in its programme it sits.
                Year = null,
                Genres = null
            };
        }

        /// <summary>
        /// Whether an entry describes something somebody is part way through.
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
