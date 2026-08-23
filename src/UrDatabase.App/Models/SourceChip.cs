using UrDatabase.Services;

namespace UrDatabase.Models
{
    /// <summary>
    /// One control in the source row: a place films can be, and how many are there.
    /// </summary>
    public class SourceChip
    {
        public LibrarySource Source { get; set; }

        public string Name => LibraryFilter.Label(Source);

        public int Count { get; set; }

        /// <summary>Whether this is the place being shown. Bound, for the same reason as GenreChip.</summary>
        public bool IsSelected { get; set; }

        /// <summary>
        /// Marks the control that selects films needing the server. It carries the same colour as
        /// the badge on those films and the button that syncs them, so one colour answers one
        /// question throughout: will this play when the laptop is away from home.
        /// </summary>
        public bool IsServer => Source == LibrarySource.Server;
    }
}
