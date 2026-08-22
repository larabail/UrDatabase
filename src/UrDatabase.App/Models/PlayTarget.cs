namespace UrDatabase.Models
{
    /// <summary>How much the app actually knows about the file behind a Play button.</summary>
    public enum PlayTargetKind
    {
        /// <summary>Nothing to play: no file is linked, and nothing on disk is worth offering.</summary>
        None,

        /// <summary>
        /// A file the catalogue says is this film — <c>files.movie_id</c> points at it and it is
        /// still on disk. The only kind that may be opened without asking.
        /// </summary>
        Linked,

        /// <summary>
        /// A file that merely looks like this film, from its name. Offered so a user can confirm
        /// it, never opened on their behalf.
        /// </summary>
        Suggested
    }

    /// <summary>
    /// What the Play button will do, and on what evidence.
    ///
    /// The evidence is carried alongside the path rather than thrown away because it is the whole
    /// difference between the two: a linked file plays, a guess gets a question first. Collapsing
    /// them into a bare string is what let the app open one film while claiming to open another.
    /// </summary>
    public sealed record PlayTarget(PlayTargetKind Kind, string? FilePath)
    {
        public static readonly PlayTarget None = new(PlayTargetKind.None, null);

        public static PlayTarget Linked(string filePath) => new(PlayTargetKind.Linked, filePath);

        public static PlayTarget Suggested(string filePath) => new(PlayTargetKind.Suggested, filePath);

        /// <summary>True when playing this would be acting on a guess.</summary>
        public bool NeedsConfirmation => Kind == PlayTargetKind.Suggested;

        public bool HasFile => !string.IsNullOrWhiteSpace(FilePath);
    }
}
