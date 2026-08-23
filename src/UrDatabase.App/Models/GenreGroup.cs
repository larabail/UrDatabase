using System.Collections.ObjectModel;

namespace UrDatabase.Models
{
    /// <summary>
    /// One genre's shelf in the library.
    /// </summary>
    /// <remarks>
    /// <see cref="Name"/> is the bare genre and <see cref="Count"/> is a number, rather than the
    /// single string <c>"Drama (12 items)"</c> this used to be. The heading sets the two in
    /// different faces — the genre in the display serif, the count in mono beside a rule — and a
    /// view cannot do that to a string that has already been glued together. It also made the
    /// name unusable for anything else: matching the selected genre against a heading meant
    /// matching against its own punctuation.
    /// </remarks>
    public class GenreGroup
    {
        public string Name { get; set; } = "";

        public int Count { get; set; }

        /// <summary>
        /// The count as it is printed beside the heading: <c>"12 FILMS"</c>, or
        /// <c>"12 FILMS · 3 SERIES"</c> on a shelf that holds both.
        /// </summary>
        /// <remarks>
        /// Read off the items rather than off <see cref="Count"/>, because the number alone cannot
        /// say what it counted, and a shelf of eight films and four programmes headed "12 FILMS"
        /// is exactly the way mixing the two on one shelf becomes dishonest.
        /// </remarks>
        public string CountLabel => Services.LibraryGrouping.CountLabel(Items);

        public ObservableCollection<UiMovie> Items { get; set; } = new();
    }
}
