using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Cuts a flat list of films into rows of cards, so the grid views can be virtualised.
    ///
    /// Avalonia 11 ships exactly one virtualising panel, <c>VirtualizingStackPanel</c>, and no
    /// virtualising equivalent of <c>WrapPanel</c>. The single-genre and search views were built
    /// on <c>WrapPanel</c>, which measures every child it is handed — so selecting a bucket
    /// holding a whole scanned library realised one <c>PosterCard</c> per film, with a gradient
    /// brush apiece, and took the window with it.
    ///
    /// Rather than write a virtualising wrap panel, the wrapping is done here and the rows are
    /// fed to a vertical <c>VirtualizingStackPanel</c>. That is exact rather than approximate:
    /// every card is a fixed <see cref="CardWidth"/> wide with a fixed gutter, so the number that
    /// fits on a line is arithmetic, and the rows this produces are the ones <c>WrapPanel</c>
    /// would have produced. A row holds at most a screen's width of cards and is not virtualised
    /// itself, which is why it does not need to be.
    /// </summary>
    public static class PosterGrid
    {
        /// <summary>The width of a card, as set on the shared template in <c>MainWindow.axaml</c>.</summary>
        public const double CardWidth = 152;

        /// <summary>The gap to the right of a card, also from that template's margin.</summary>
        public const double CardGutter = 14;

        /// <summary>How much horizontal room one card occupies in total.</summary>
        public const double CardStride = CardWidth + CardGutter;

        /// <summary>
        /// How many cards fit across <paramref name="availableWidth"/>, and never fewer than one.
        /// </summary>
        /// <remarks>
        /// A zero or negative width is what a control reports before it has been laid out, and
        /// answering zero there would produce a row per film — precisely the realisation storm
        /// this class exists to prevent. One column is wrong for a single frame and self-corrects;
        /// zero columns would be a division by nothing and an infinite number of rows.
        ///
        /// The final card's gutter is not subtracted. It falls in the margin at the right-hand
        /// edge rather than forcing a card onto the next line, which is what <c>WrapPanel</c> does
        /// with a trailing margin and is why a naive <c>(width + gutter) / stride</c> here would
        /// disagree with it by one column at some widths.
        /// </remarks>
        public static int Columns(double availableWidth)
        {
            if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
                return 1;

            return Math.Max(1, (int)Math.Floor(availableWidth / CardStride));
        }

        /// <summary>
        /// The same films, in the same order, cut into rows of <paramref name="columns"/>.
        /// </summary>
        /// <remarks>
        /// Order is preserved exactly, because the caller has already sorted: the grid is newest
        /// first within a genre, and search results are in rank order. Re-sorting here would
        /// quietly override both.
        /// </remarks>
        public static IReadOnlyList<PosterRow> Chunk(IReadOnlyList<UiMovie>? items, int columns)
        {
            if (items is null || items.Count == 0) return Array.Empty<PosterRow>();

            var width = Math.Max(1, columns);
            var rows = new List<PosterRow>((items.Count + width - 1) / width);

            for (var start = 0; start < items.Count; start += width)
            {
                var row = new ObservableCollection<UiMovie>();

                for (var i = start; i < start + width && i < items.Count; i++)
                    row.Add(items[i]);

                rows.Add(new PosterRow { Items = row });
            }

            return rows;
        }
    }
}
