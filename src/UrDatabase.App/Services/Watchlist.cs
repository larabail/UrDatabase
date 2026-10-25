using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>One thing the owner has said they want to watch, as it is stored.</summary>
    public sealed record WatchlistEntry(string ItemKey, DateTime AddedAt);

    /// <summary>
    /// Decides what is on the watchlist and what the Watchlist shelf shows, in what order.
    /// </summary>
    /// <remarks>
    /// Pure, and out of the window, for the same reason <see cref="ResumeRow"/> is: the rules are
    /// claims that can be wrong, and the interesting one is identity.
    ///
    /// An entry is stored under the key the card had when it was added — <see cref="UiMovie.Key"/>,
    /// so <c>local:12</c> for a catalogued film and <c>jellyfin:&lt;item id&gt;</c> for one that only
    /// the server holds. A film does not keep one key for life, though. A server film that is
    /// downloaded becomes a catalogued film with the server's id beside it, and its card is then
    /// keyed <c>local:</c>. Matching on the card's own key alone would quietly drop it off the
    /// watchlist at exactly the moment somebody fetched it to watch. So a card answers to every
    /// key it could have been added under, and removing one removes all of them.
    ///
    /// An episode is never on the watchlist. It only exists in the Continue watching row, which
    /// is already a list of things somebody means to carry on with.
    /// </remarks>
    public static class Watchlist
    {
        /// <summary>The shelf heading. Not a genre, and never offered as a chip.</summary>
        public const string Heading = "Watchlist";

        /// <summary>
        /// What the button on the details screen says: what will happen when it is pressed, or
        /// that it already has.
        /// </summary>
        public static string ButtonLabel(bool onWatchlist) => onWatchlist ? "\u2713 On Watchlist" : "+ Watchlist";

        /// <summary>Whether a card is something that can be put on the watchlist at all.</summary>
        /// <remarks>
        /// A card with no stable identity is refused rather than stored under a key that names
        /// nothing: a local row with no id, or a server card with no item id. So is a programme on
        /// this disk, for now: the shelf and the store behind it resolve a local key to a film row,
        /// and a series id would name whichever film happened to share its number.
        /// </remarks>
        public static bool CanHold(UiMovie? card)
        {
            if (card is null || card.IsEpisode) return false;

            return card.Source == MovieSource.Jellyfin
                ? !string.IsNullOrWhiteSpace(card.RemoteId)
                : card.IsFilm && card.Id > 0;
        }

        /// <summary>
        /// Every key a card could have been added under, the one to store first.
        /// </summary>
        public static IReadOnlyList<string> KeysFor(UiMovie? card)
        {
            if (!CanHold(card)) return Array.Empty<string>();

            var keys = new List<string> { card!.Key };

            // A catalogued film the server also holds: it may have been added as a server film,
            // before this machine had a copy. See the remarks on the class.
            if (card.Source == MovieSource.Local && card.IsFilm && card.RemoteId is { Length: > 0 } remote)
                keys.Add(FilmKey(remote));

            return keys;
        }

        /// <summary>
        /// The same, for a film open on the details screen, which knows its catalogue row and its
        /// server id but is not a card. The first key is the one a card for it would carry.
        /// </summary>
        public static IReadOnlyList<string> KeysFor(long localId, string? remoteId)
        {
            var keys = new List<string>();

            if (localId > 0) keys.Add(LocalKey(localId));
            if (!string.IsNullOrWhiteSpace(remoteId)) keys.Add(FilmKey(remoteId));

            return keys;
        }

        /// <summary>Whether any of these keys is on the watchlist.</summary>
        public static bool Contains(IEnumerable<WatchlistEntry>? entries, IEnumerable<string>? keys)
        {
            if (entries is null || keys is null) return false;

            var held = Index(entries);
            return keys.Any(k => !string.IsNullOrWhiteSpace(k) && held.ContainsKey(k.Trim()));
        }

        /// <summary>Whether a card is on the watchlist under any of its keys.</summary>
        public static bool Contains(IEnumerable<WatchlistEntry>? entries, UiMovie? card)
            => Contains(entries, KeysFor(card));

        /// <summary>
        /// The Watchlist shelf: every card in <paramref name="library"/> that is on the list, most
        /// recently added first.
        /// </summary>
        /// <remarks>
        /// Built from what is on screen, as Continue watching is, so narrowing the wall to films or
        /// to one place narrows this shelf too. An entry the library cannot place — a film since
        /// deleted, a server since disconnected — is simply not shown, and is kept: the film may
        /// come back, and a watchlist that forgot things whenever the server was down would not be
        /// one anybody could trust.
        ///
        /// Newest first because the shelf scrolls sideways and shows a handful of posters before
        /// it has to be scrolled; the thing somebody added last night is the likeliest one they
        /// came looking for.
        /// </remarks>
        public static IReadOnlyList<UiMovie> Build(IEnumerable<UiMovie>? library, IEnumerable<WatchlistEntry>? entries)
        {
            if (library is null || entries is null) return Array.Empty<UiMovie>();

            var held = Index(entries);
            if (held.Count == 0) return Array.Empty<UiMovie>();

            var shelf = new List<(UiMovie Card, DateTime AddedAt)>();
            var used = new HashSet<string>(StringComparer.Ordinal);

            foreach (var card in library)
            {
                DateTime? added = null;

                foreach (var key in KeysFor(card))
                {
                    if (held.TryGetValue(key, out var at) && (added is null || at > added))
                        added = at;
                }

                if (added is null || !used.Add(card.Key)) continue;
                shelf.Add((card, added.Value));
            }

            return shelf
                .OrderByDescending(x => x.AddedAt)
                .ThenBy(x => x.Card.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.Card)
                .ToList();
        }

        /// <summary>
        /// Entries held under a server key that a catalogued film now answers to, paired with the
        /// catalogue key they should be moved to.
        /// </summary>
        /// <remarks>
        /// The link from <c>local:N</c> to <c>jellyfin:X</c> is the card's
        /// <see cref="UiMovie.RemoteId"/>, and that is worked out by matching on every load and
        /// never stored. It goes away when the server is disconnected, when the item is
        /// re-identified there, or when a correction renames the film here. An entry left under
        /// the server key then drops off the shelf without a word, and taking the film off the list
        /// while the match is broken would delete only <c>local:N</c> and leave the old entry to come
        /// back later. So once a film has a row of its own, its entry is moved onto that row while
        /// the link still holds.
        /// </remarks>
        public static IReadOnlyList<(string From, string To)> Adoptions(
            IEnumerable<UiMovie>? library,
            IEnumerable<WatchlistEntry>? entries)
        {
            if (library is null || entries is null) return Array.Empty<(string, string)>();

            var held = Index(entries);
            if (held.Count == 0) return Array.Empty<(string, string)>();

            var moves = new List<(string From, string To)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var card in library)
            {
                if (card is null || card.Source != MovieSource.Local || !card.IsFilm || card.Id <= 0) continue;
                if (card.RemoteId is not { Length: > 0 } remote) continue;

                var from = FilmKey(remote);
                if (!held.ContainsKey(from) || !seen.Add(from)) continue;

                moves.Add((from, LocalKey(card.Id)));
            }

            return moves;
        }

        /// <summary>The key a catalogued film's card carries.</summary>
        public static string LocalKey(long localId) => $"local:{localId.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>
        /// The key a server film's card carries. Jellyfin ids are compared case-insensitively
        /// everywhere else in the app, so they are here too.
        /// </summary>
        public static string FilmKey(string remoteId) => $"jellyfin:{remoteId.Trim()}";

        private static Dictionary<string, DateTime> Index(IEnumerable<WatchlistEntry> entries)
        {
            var held = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.ItemKey)) continue;

                var key = entry.ItemKey.Trim();
                if (!held.TryGetValue(key, out var at) || entry.AddedAt > at) held[key] = entry.AddedAt;
            }

            return held;
        }
    }
}
