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
    }
}
