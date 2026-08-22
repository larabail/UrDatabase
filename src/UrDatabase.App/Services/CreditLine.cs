using System;

namespace UrDatabase.Services
{
    /// <summary>
    /// Takes the credit lines apart again so the details screen can set each half differently.
    ///
    /// Cast and crew arrive as single strings — <c>"Keir Dullea (Dave Bowman)"</c> and
    /// <c>"Director: Stanley Kubrick"</c> — because that is what a row of chips needed. The
    /// details screen sets a cast member's name over their character, and a crew job as a small
    /// tracked label before the name, and neither is possible while the two halves are glued
    /// together with punctuation.
    ///
    /// Parsing a string the app itself formatted is not ideal. It is, however, confined to this
    /// one class and covered by tests, which is a good deal better than the view guessing at the
    /// position of a bracket.
    /// </summary>
    public static class CreditLine
    {
        /// <summary>
        /// Splits <c>"Name (Character)"</c> into its two halves. A line with no character — which
        /// is what TMDB gives for an uncredited part — comes back with an empty character rather
        /// than an empty name, so the view never prints a role with nobody playing it.
        /// </summary>
        public static (string Name, string Character) SplitCast(string? line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0) return ("", "");

            // The opening bracket is found from the left, not the right: a character can contain
            // brackets of its own — "Dave Bowman (voice (uncredited))" — and searching from the
            // right splits that one in the wrong place.
            if (text.EndsWith(')'))
            {
                var open = text.IndexOf('(');
                if (open > 0)
                {
                    var name = text[..open].TrimEnd();
                    var character = text[(open + 1)..^1].Trim();

                    if (name.Length > 0) return (name, character);
                }
            }

            return (text, "");
        }

        /// <summary>
        /// Splits <c>"Director: Stanley Kubrick"</c> into the job and the person. A line with no
        /// job comes back as a name with an empty job, for the same reason.
        /// </summary>
        public static (string Job, string Name) SplitCrew(string? line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0) return ("", "");

            var colon = text.IndexOf(':');
            if (colon > 0)
            {
                var job = text[..colon].TrimEnd();
                var name = text[(colon + 1)..].TrimStart();

                if (name.Length > 0) return (job, name);
            }

            return ("", text);
        }
    }
}
