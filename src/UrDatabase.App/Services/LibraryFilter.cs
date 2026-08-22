using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Which half of the library is being looked at.
    /// </summary>
    /// <remarks>
    /// A genre row cannot answer "what is actually on this disk". Genre and location are two
    /// different questions, and folding them into one row made local films unreachable in
    /// practice: a scanned film has no genre until something enriches it, so all of them land in
    /// the Uncategorised bucket, which sorts last — behind every genre a server library brought
    /// with it. On a library of four hundred server films and three local ones, the three were
    /// the twenty-first chip in a scrolling row and the last shelf on the page.
    ///
    /// Filtering by source also survives those films getting genres later, which reordering the
    /// buckets would not: once a scanned film is enriched it scatters into the genres and becomes
    /// just as hard to pick out, for the opposite reason.
    /// </remarks>
    public enum LibrarySource
    {
        Everywhere = 0,

        /// <summary>Films with a file on this machine. The ones that play on a train.</summary>
        ThisComputer = 1,

        /// <summary>Films on a Jellyfin server, which need it reachable to play.</summary>
        Server = 2
    }

    /// <summary>
    /// Narrows the library to one source, and counts what each holds.
    /// </summary>
    public static class LibraryFilter
    {
        public static IReadOnlyList<UiMovie> Apply(IEnumerable<UiMovie>? movies, LibrarySource source)
        {
            if (movies is null) return new List<UiMovie>();

            return source switch
            {
                LibrarySource.ThisComputer => movies.Where(m => !m.IsRemote).ToList(),
                LibrarySource.Server => movies.Where(m => m.IsRemote).ToList(),
                _ => movies.ToList()
            };
        }

        /// <summary>
        /// How many films a source holds. Counted on <see cref="UiMovie.Key"/>, because every
        /// server film carries local id 0 and counting on the id alone reports the whole remote
        /// library as one film.
        /// </summary>
        public static int Count(IEnumerable<UiMovie>? movies, LibrarySource source)
            => Apply(movies, source).Select(m => m.Key).Distinct(System.StringComparer.Ordinal).Count();

        /// <summary>The name on the control.</summary>
        public static string Label(LibrarySource source) => source switch
        {
            LibrarySource.ThisComputer => "On this computer",
            LibrarySource.Server => "On the server",
            _ => "Everywhere"
        };

        /// <summary>
        /// Which sources are worth offering. A source that holds nothing is not shown: an install
        /// with no Jellyfin server should not carry a permanent, empty "On the server" control,
        /// and the whole row is pointless when everything comes from one place — which is the
        /// commonest case and the one that must not grow new furniture.
        /// </summary>
        public static IReadOnlyList<LibrarySource> Available(IEnumerable<UiMovie>? movies)
        {
            var materialised = movies as IReadOnlyCollection<UiMovie> ?? movies?.ToList();
            if (materialised is null || materialised.Count == 0) return new List<LibrarySource>();

            var local = Count(materialised, LibrarySource.ThisComputer);
            var server = Count(materialised, LibrarySource.Server);

            if (local == 0 || server == 0) return new List<LibrarySource>();

            return new List<LibrarySource>
            {
                LibrarySource.Everywhere,
                LibrarySource.ThisComputer,
                LibrarySource.Server
            };
        }
    }
}
