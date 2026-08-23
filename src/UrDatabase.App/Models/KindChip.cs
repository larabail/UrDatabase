using UrDatabase.Services;

namespace UrDatabase.Models
{
    /// <summary>
    /// One control in the kind row: films, television, or both, and how many are there.
    /// </summary>
    /// <remarks>
    /// A sibling of <see cref="SourceChip"/> in every respect, because the two rows answer
    /// questions of the same shape — what is being looked at, and where it is — and a user should
    /// not have to learn two idioms to use them.
    /// </remarks>
    public class KindChip
    {
        public LibraryKind Kind { get; set; }

        public string Name => LibraryFilter.Label(Kind);

        public int Count { get; set; }

        /// <summary>Whether this is the kind being shown. Bound, for the same reason as GenreChip.</summary>
        public bool IsSelected { get; set; }
    }
}
