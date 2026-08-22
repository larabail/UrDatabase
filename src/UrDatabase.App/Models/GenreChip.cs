namespace UrDatabase.Models
{
    /// <summary>
    /// One entry in the genre row across the top of the library.
    /// </summary>
    /// <remarks>
    /// A model rather than a bare string, for two reasons. The row shows a count beside each
    /// genre, and a count cannot be carried by the string that is also the genre's identity.
    /// And the click handler used to recover which genre had been clicked by reading the
    /// button's own <c>Content</c> back out as a string — which quietly stops working the moment
    /// the button contains anything but text, such as a genre and a count in two different
    /// faces. The handler now reads this off the button's data context instead.
    /// </remarks>
    public class GenreChip
    {
        public string Name { get; set; } = "";

        public int Count { get; set; }

        /// <summary>
        /// Whether this is the genre being shown.
        /// </summary>
        /// <remarks>
        /// Bound, rather than set by walking the visual tree for ToggleButtons and ticking the
        /// right one. That walk cannot find a button that has not been realised yet, so on the
        /// very first render — before anybody has clicked anything — no chip appeared selected at
        /// all, and the row silently disagreed with the shelves below it.
        /// </remarks>
        public bool IsSelected { get; set; }
    }
}
