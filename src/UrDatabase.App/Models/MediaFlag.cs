namespace UrDatabase.Models
{
    /// <summary>
    /// One small badge on the facts row: <c>4K</c>, <c>HDR10</c>, <c>ATMOS 7.1</c>, <c>EN</c>.
    /// </summary>
    public sealed class MediaFlag
    {
        /// <summary>What is printed on the badge. Short by construction — this is a chip, not a sentence.</summary>
        public string Text { get; set; } = "";

        /// <summary>
        /// What the badge means, spelled out for the tooltip. A two-letter language code is not
        /// self-explanatory to everybody, and neither is "DDP".
        /// </summary>
        public string Tip { get; set; } = "";

        public MediaFlagKind Kind { get; set; } = MediaFlagKind.Picture;

        /// <summary>
        /// A small label printed before this badge, marking the start of a group. Empty on every
        /// badge but the first of its group.
        /// </summary>
        /// <remarks>
        /// Without it the row ends "EN FR ES DE EN FR" — four languages the film can be heard in
        /// followed by two it can be read in, rendered as six chips that differ only by a fill.
        /// That reads as the app having printed the same thing twice, which is the one impression
        /// a row of facts must not give. The label is the cheapest fix that also survives being
        /// looked at by somebody who cannot see the difference between the two fills.
        /// </remarks>
        public string GroupLabel { get; set; } = "";

        public bool HasGroupLabel => GroupLabel.Length > 0;

        /// <summary>
        /// Exposed as flags so the view switches a style class directly rather than going through
        /// a converter, matching how <see cref="DetailFact"/> already does it.
        /// </summary>
        public bool IsLanguage => Kind == MediaFlagKind.Language;

        public bool IsSubtitle => Kind == MediaFlagKind.Subtitle;
    }

    public enum MediaFlagKind
    {
        /// <summary>Resolution, dynamic range, video codec, source, size.</summary>
        Picture,

        /// <summary>Codec, channel layout, Atmos.</summary>
        Sound,

        /// <summary>A language the film can be heard in.</summary>
        Language,

        /// <summary>A language it can be read in.</summary>
        Subtitle
    }
}
