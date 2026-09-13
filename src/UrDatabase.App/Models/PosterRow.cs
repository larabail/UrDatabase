using System.Collections.ObjectModel;

namespace UrDatabase.Models
{
    /// <summary>
    /// One line of cards in a grid view.
    /// </summary>
    /// <remarks>
    /// It exists so the grid can be a list. Avalonia only virtualises a stack, so the single-genre
    /// and search views wrap their cards into rows here — see <see cref="Services.PosterGrid"/> —
    /// and hand the rows to a vertical <c>VirtualizingStackPanel</c> instead of handing every film
    /// to a <c>WrapPanel</c> that would measure all of them.
    ///
    /// A row and not a genre: it carries no name, no count and no heading, because it is a unit of
    /// layout rather than a unit of meaning. <see cref="GenreGroup"/> is the one with a heading,
    /// and nothing should ever be grouped by which line it happened to land on.
    /// </remarks>
    public class PosterRow
    {
        public ObservableCollection<UiMovie> Items { get; set; } = new();
    }
}
