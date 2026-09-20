namespace UrDatabase.Services
{
    public readonly record struct ShelfNavigation(
        double LeftOffset, double RightOffset, bool CanScrollLeft, bool CanScrollRight)
    {
        public static ShelfNavigation Calculate(double offset, double extent, double viewport)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(extent);
            ArgumentOutOfRangeException.ThrowIfNegative(viewport);
            if (!double.IsFinite(offset)) throw new ArgumentOutOfRangeException(nameof(offset));
            if (!double.IsFinite(extent)) throw new ArgumentOutOfRangeException(nameof(extent));
            if (!double.IsFinite(viewport)) throw new ArgumentOutOfRangeException(nameof(viewport));

            var maximum = Math.Max(0, extent - viewport);
            var current = Math.Clamp(offset, 0, maximum);
            // Leave overlap so a card cut by the old edge is fully visible on the next page.
            var page = viewport * 0.8;

            return new ShelfNavigation(
                Math.Max(0, current - page),
                Math.Min(maximum, current + page),
                viewport > 0 && current > 0,
                viewport > 0 && current < maximum);
        }
    }
}
