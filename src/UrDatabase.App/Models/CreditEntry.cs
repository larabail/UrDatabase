namespace UrDatabase.Models
{
    /// <summary>
    /// One credit as the details screen sets it: two pieces of text in two different faces.
    ///
    /// Cast is a person over the part they played; crew is a job label before a person. The two
    /// share a shape because the view templates them the same way, and differ only in which half
    /// is which — <see cref="Primary"/> is always the person's name, which is the half worth
    /// setting in the readable face.
    /// </summary>
    public class CreditEntry
    {
        /// <summary>The person. Always present.</summary>
        public string Primary { get; set; } = "";

        /// <summary>The part played, or the job done. Absent for an uncredited part.</summary>
        public string Secondary { get; set; } = "";

        public bool HasSecondary => !string.IsNullOrWhiteSpace(Secondary);
    }
}
