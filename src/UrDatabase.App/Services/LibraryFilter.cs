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

        /// <summary>
        /// Films with a file on this machine. The ones that play on a train, which is why the
        /// control that selects them is called "Offline" rather than named after the place: a
        /// film can be here <em>and</em> on the server, so a label that reads as "not the server"
        /// would be wrong about every film that is in both.
        /// </summary>
        ThisComputer = 1,

        /// <summary>Films a Jellyfin server holds, whether or not this machine holds them too.</summary>
        Server = 2
    }

    /// <summary>
    /// Narrows the library to one source, and counts what each holds.
    /// </summary>
    public static class LibraryFilter
    {
        /// <summary>
        /// The films at one source. A film held in both places answers to both controls: it is
        /// genuinely on the server and it genuinely plays offline, and leaving it out of either
        /// list would make that list a lie about what is there.
        /// </summary>
        public static IReadOnlyList<UiMovie> Apply(IEnumerable<UiMovie>? movies, LibrarySource source)
        {
            if (movies is null) return new List<UiMovie>();

            return source switch
            {
                LibrarySource.ThisComputer => movies.Where(m => m.IsOnThisComputer).ToList(),
                LibrarySource.Server => movies.Where(m => m.IsOnServer).ToList(),
                _ => movies.ToList()
            };
        }

        /// <summary>
        /// How many films a source holds. Counted on <see cref="UiMovie.Key"/>, because every
        /// server film carries local id 0 and counting on the id alone reports the whole remote
        /// library as one film.
        ///
        /// The counts do not add up to the total, and should not: a film in both places is one
        /// film and is counted by both controls.
        /// </summary>
        public static int Count(IEnumerable<UiMovie>? movies, LibrarySource source)
            => Apply(movies, source).Select(m => m.Key).Distinct(System.StringComparer.Ordinal).Count();

        /// <summary>The name on the control.</summary>
        public static string Label(LibrarySource source) => source switch
        {
            // The same word the badge on a card uses, deliberately: see UiMovie.OfflineTag.
            LibrarySource.ThisComputer => UiMovie.OfflineTag,
            LibrarySource.Server => "On the server",
            _ => "Everywhere"
        };

        /// <summary>
        /// Which sources are worth offering. A source that holds nothing is not shown: an install
        /// with no Jellyfin server should not carry a permanent, empty "On the server" control,
        /// and the whole row is pointless when everything comes from one place — which is the
        /// commonest case and the one that must not grow new furniture.
        ///
        /// Nor is it offered when every film is in both places, which is what a fully synced
        /// library looks like now that those are one card. Three controls that each select the
        /// same films are worse than no row at all: they invite a click that appears to do
        /// nothing.
        /// </summary>
        public static IReadOnlyList<LibrarySource> Available(IEnumerable<UiMovie>? movies)
        {
            var materialised = movies as IReadOnlyCollection<UiMovie> ?? movies?.ToList();
            if (materialised is null || materialised.Count == 0) return new List<LibrarySource>();

            var everywhere = Count(materialised, LibrarySource.Everywhere);
            var local = Count(materialised, LibrarySource.ThisComputer);
            var server = Count(materialised, LibrarySource.Server);

            if (local == 0 || server == 0) return new List<LibrarySource>();
            if (local == everywhere && server == everywhere) return new List<LibrarySource>();

            return new List<LibrarySource>
            {
                LibrarySource.Everywhere,
                LibrarySource.ThisComputer,
                LibrarySource.Server
            };
        }
    }
}
