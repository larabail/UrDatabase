using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Somewhere to ask what the Academy made of a film. Separated from
    /// <see cref="OscarsService"/>'s cache the way <see cref="IImdbRatingLookup"/> is separated
    /// from <see cref="ImdbRatingService"/>, so the caching rules can be tested against a fake
    /// that never touches the network.
    /// </summary>
    public interface IOscarsLookup
    {
        /// <summary>False when no key is configured, in which case no request is ever made.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Every nomination the archive holds under this title, at any ceremony, or null when the
        /// question could not be answered — no key, no network, a rate limit, a bad response.
        /// </summary>
        /// <remarks>
        /// The distinction is the whole point of the return type. An empty list means the Academy
        /// never nominated this film, which is a real answer and worth remembering forever. Null
        /// means nobody knows yet. Collapsing the two would let one rate-limited afternoon record
        /// "no awards" against a hundred films permanently, and nothing would ever ask again.
        ///
        /// Deciding which of the results belong to the film in front of the user is
        /// <see cref="OscarMatch"/>'s job, not this one's — a lookup reports what the archive says
        /// and nothing more.
        /// </remarks>
        Task<IReadOnlyList<OscarNomination>?> LookupAsync(string title, CancellationToken ct = default);
    }
}
