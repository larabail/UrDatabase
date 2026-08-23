using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// One thing the owner has taken out of their own Continue watching row, and where it was when
    /// they did.
    /// </summary>
    /// <remarks>
    /// The position is the whole of the recurrence rule. A dismissal is not a promise never to
    /// show something again — it is "I am not part way through this any more", and that claim
    /// stops being true the moment the server records more of it being watched.
    /// </remarks>
    public sealed record ResumeDismissal(string ItemId, long PositionTicks);

    /// <summary>
    /// Whether a dismissal still applies, given what the server now says.
    /// </summary>
    /// <remarks>
    /// Pure, and out of both the window and the database, because this is the rule most likely to
    /// be got wrong and the one whose being wrong is hardest to notice: a dismissal that never
    /// expires is a blacklist the owner cannot see, and one that expires too eagerly makes the
    /// gesture look broken.
    ///
    /// The rule the owner chose: a dismissal lasts exactly as long as the position it was made at.
    /// Abandon a film at 22 minutes and it stays out of the row forever, because nothing will ever
    /// move it. Dismiss one here and then watch ten more minutes of it on the television, and the
    /// server reports a different position, the dismissal is stale, and the film comes back —
    /// which is right, because you have plainly not abandoned it.
    ///
    /// Compared exactly rather than with a tolerance. Ticks come back byte-identical for an item
    /// nobody has touched, so any difference at all is the server having recorded something, and a
    /// "near enough" window would only add a band in which a real viewing is ignored.
    /// </remarks>
    public static class ResumeDismissals
    {
        /// <summary>
        /// Whether <paramref name="dismissal"/> hides <paramref name="entry"/>.
        /// </summary>
        /// <remarks>
        /// False for a mismatched id, so this can be asked of any pair without the caller having
        /// to check first.
        /// </remarks>
        public static bool Hides(ResumeDismissal? dismissal, JellyfinResumeItem? entry)
        {
            if (dismissal is null || entry is null) return false;
            if (string.IsNullOrWhiteSpace(dismissal.ItemId) || string.IsNullOrWhiteSpace(entry.ItemId)) return false;

            if (!string.Equals(dismissal.ItemId.Trim(), entry.ItemId.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            return dismissal.PositionTicks == entry.PositionTicks;
        }

        /// <summary>
        /// The row with everything dismissed taken out of it, in the order it arrived.
        /// </summary>
        /// <remarks>
        /// Keyed on the item id alone, so dismissing one episode says nothing about the rest of
        /// its programme. That is deliberate: the next episode is a different thing to be part way
        /// through, and hiding a whole series because one episode was abandoned would silently
        /// make the gesture far larger than it looks.
        /// </remarks>
        public static IReadOnlyList<JellyfinResumeItem> Apply(
            IEnumerable<JellyfinResumeItem>? resume,
            IEnumerable<ResumeDismissal>? dismissals)
        {
            var entries = resume?.Where(e => e is not null).ToList() ?? new List<JellyfinResumeItem>();
            if (entries.Count == 0) return Array.Empty<JellyfinResumeItem>();

            var byId = Index(dismissals);
            if (byId.Count == 0) return entries;

            return entries
                .Where(entry => !(byId.TryGetValue((entry.ItemId ?? "").Trim(), out var dismissal) && Hides(dismissal, entry)))
                .ToList();
        }

        /// <summary>
        /// The dismissals that have stopped meaning anything, given what the server just said.
        /// </summary>
        /// <remarks>
        /// Two ways to become stale. The position moved, which is the recurrence rule above; or
        /// the item left the resume list altogether — it was finished, or reset, or removed — and
        /// a dismissal for something that is not in the row cannot hide anything.
        ///
        /// Only ever asked with a list the server actually answered with. Pruning against a fetch
        /// that failed would forget every dismissal the first time somebody opened the app on a
        /// train.
        /// </remarks>
        public static IReadOnlyList<ResumeDismissal> Stale(
            IEnumerable<ResumeDismissal>? dismissals,
            IEnumerable<JellyfinResumeItem>? resume)
        {
            var held = dismissals?.Where(d => d is not null && !string.IsNullOrWhiteSpace(d.ItemId)).ToList()
                       ?? new List<ResumeDismissal>();

            if (held.Count == 0) return Array.Empty<ResumeDismissal>();

            var byId = new Dictionary<string, JellyfinResumeItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in resume ?? Array.Empty<JellyfinResumeItem>())
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.ItemId)) continue;
                byId.TryAdd(entry.ItemId.Trim(), entry);
            }

            return held
                .Where(d => !byId.TryGetValue(d.ItemId.Trim(), out var entry) || !Hides(d, entry))
                .ToList();
        }

        private static Dictionary<string, ResumeDismissal> Index(IEnumerable<ResumeDismissal>? dismissals)
        {
            var byId = new Dictionary<string, ResumeDismissal>(StringComparer.OrdinalIgnoreCase);

            foreach (var dismissal in dismissals ?? Array.Empty<ResumeDismissal>())
            {
                if (dismissal is null || string.IsNullOrWhiteSpace(dismissal.ItemId)) continue;
                byId[dismissal.ItemId.Trim()] = dismissal;
            }

            return byId;
        }
    }
}
