namespace UrDatabase.Services
{
    /// <summary>A rating and review to write together, as the film page decided them.</summary>
    public sealed record RatingWrite(int? Stars, string Review);

    /// <summary>
    /// What an edit on the film page should write, if anything.
    /// </summary>
    /// <remarks>
    /// Every write saves the stars and the review together, so whatever is on screen is what
    /// lands on disk. Until the film's stored journal has been shown, what is on screen is the
    /// blank placeholder — no stars, an empty review box — and saving it would replace the
    /// owner's real review with nothing. So nothing is written until the journal for this film
    /// has loaded, and a film whose journal could not be read stays read-only rather than
    /// guessing. The page disables the controls as well; this is the rule they enforce, kept
    /// here where a test can reach it.
    /// </remarks>
    public static class FilmJournalEdits
    {
        /// <summary>Whether the rating and review can be changed at all.</summary>
        public static bool CanEdit(bool loaded) => loaded;

        /// <summary>The write a star click makes, or null when it must make none.</summary>
        public static RatingWrite? StarClicked(bool loaded, int? currentStars, int clicked, string? reviewInBox)
        {
            if (!CanEdit(loaded)) return null;
            return new RatingWrite(FilmJournalStore.Toggle(currentStars, clicked), Tidy(reviewInBox));
        }

        /// <summary>
        /// The write leaving the review box makes: none before the journal has loaded, and none
        /// when the text is what was last loaded or saved.
        /// </summary>
        public static RatingWrite? ReviewLeft(bool loaded, int? currentStars, string? savedReview, string? reviewInBox)
        {
            if (!CanEdit(loaded)) return null;

            var review = Tidy(reviewInBox);
            return string.Equals(review, savedReview ?? "", System.StringComparison.Ordinal)
                ? null
                : new RatingWrite(currentStars, review);
        }

        private static string Tidy(string? review) => (review ?? "").Trim();
    }
}
