using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>
    /// Looks up an IMDb rating by IMDb id. Behind an interface so the cache and the UI can be
    /// tested without OMDb or any network access.
    /// </summary>
    public interface IImdbRatingLookup
    {
        /// <summary>False when the lookup cannot work at all, in which case no call is attempted.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Returns the rating, or null when there is none. Implementations must not throw for
        /// upstream failures: the rating is an optional enhancement.
        /// </summary>
        Task<double?> LookupAsync(string imdbId, CancellationToken ct = default);

        /// <summary>
        /// The same lookup, saying why there is no rating when there is none. The default reads
        /// every null as "no rating", which is all a lookup that cannot tell the difference can
        /// claim; <see cref="OmdbService"/> overrides it to tell a rate limit from an answer.
        /// </summary>
        async Task<ImdbLookupResult> LookupResultAsync(string imdbId, CancellationToken ct = default) =>
            ImdbLookupResult.Of(await LookupAsync(imdbId, ct).ConfigureAwait(false));
    }
}
