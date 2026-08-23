namespace UrDatabase.Models
{
    /// <summary>
    /// One line in the awards list under the poster: the category, who it was for, and whether it
    /// was won.
    /// </summary>
    public sealed class AwardRow
    {
        /// <summary>The category, as the Academy named it that year.</summary>
        public string Category { get; set; } = "";

        /// <summary>
        /// Who or what the nomination named, with the film's own title removed — it is already
        /// the largest thing on the screen. Empty when the nomination named nothing but the film,
        /// which is how Best Picture arrives.
        /// </summary>
        public string Detail { get; set; } = "";

        public bool Won { get; set; }

        public bool HasDetail => Detail.Length > 0;

        /// <summary>
        /// The whole row, for the tooltip. The names are trimmed on screen — a Best Picture
        /// nomination lists six producers and the column is 250 pixels wide — so the full list
        /// has to be readable somewhere or it may as well not have been fetched.
        /// </summary>
        public string Tip => HasDetail ? $"{Category} — {Detail}" : Category;

        /// <summary>
        /// The glyph in the margin. A win is marked by shape as well as by weight, because a
        /// list distinguished only by ink is a list some readers cannot distinguish at all.
        /// </summary>
        public string Mark => Won ? "★" : "·";
    }
}
