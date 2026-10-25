using System.Collections.Generic;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// When the library page catches up with what has arrived in the background — genres, and the
    /// length, rating and people the filter row asks about.
    /// </summary>
    /// <remarks>
    /// Out of the window so the rule can be tested, because the way it went wrong was entirely in
    /// the rule: it only ever redrew a page that had shelves on it. A page the filter row had
    /// emptied has none, so straight after a scan "Over 2 hours" showed nothing and went on
    /// showing nothing while the very films it was waiting for had their running times filled in
    /// underneath it.
    /// </remarks>
    public static class LibraryRegroup
    {
        /// <summary>
        /// Whether to redraw the grouped page now.
        /// </summary>
        /// <param name="stale">Something has arrived since the page was last drawn.</param>
        /// <param name="due">Long enough has passed since the last redraw that another will read as progress rather than flicker.</param>
        /// <param name="searching">Search results are up. Those are left alone: redrawing would throw away what somebody is reading.</param>
        /// <param name="shelvesShown">The grouped shelves are what is on screen.</param>
        /// <param name="emptiedByFilters">The page is empty only because of the filter row — the case the shelves-only rule missed.</param>
        public static bool ShouldRedraw(bool stale, bool due, bool searching, bool shelvesShown, bool emptiedByFilters)
        {
            if (!stale || !due || searching) return false;

            return shelvesShown || emptiedByFilters;
        }

        /// <summary>
        /// Whether a library that has films in it shows none of them because of the filter row,
        /// as opposed to having nothing to show at all.
        /// </summary>
        public static bool EmptiedByFilters(LibraryRefinement refinement, IReadOnlyCollection<UiMovie> unrefined) =>
            refinement.IsActive && unrefined.Count > 0 && refinement.Apply(unrefined).Count == 0;
    }
}
