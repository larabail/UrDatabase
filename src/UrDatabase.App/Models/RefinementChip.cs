using UrDatabase.Services;

namespace UrDatabase.Models
{
    /// <summary>
    /// One control in the length row of the filters. A sibling of <see cref="KindChip"/>, drawn
    /// the same way, so the filters read as more of the same row rather than a second idiom.
    /// </summary>
    public class RuntimeChip
    {
        public RuntimeRange Range { get; set; }

        public string Name => LibraryRefinement.Label(Range);

        public bool IsSelected { get; set; }
    }

    /// <summary>One control in the rating row of the filters: a floor out of ten, or none.</summary>
    public class RatingChip
    {
        public double? Minimum { get; set; }

        public string Name => LibraryRefinement.RatingLabel(Minimum);

        public bool IsSelected { get; set; }
    }
}
