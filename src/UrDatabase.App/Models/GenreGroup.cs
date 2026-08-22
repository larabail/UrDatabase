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

        /// <summary>The count as it is printed beside the heading: <c>"12 FILMS"</c>.</summary>
        public string CountLabel => Services.LibraryGrouping.CountLabel(Count);

        public ObservableCollection<UiMovie> Items { get; set; } = new();
    }
}
