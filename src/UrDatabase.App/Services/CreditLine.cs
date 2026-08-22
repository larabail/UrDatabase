using System;
using System.Collections.Generic;
using System.Linq;

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

        /// <summary>
        /// How many actors are worth listing. TMDB orders its cast by billing, so the tail is
        /// uncredited extras and the row would scroll forever.
        /// </summary>
        public const int MaxCast = 10;

        /// <summary>Directors and writers are capped separately; a film with nine writers is a list, not a fact.</summary>
        public const int MaxPerCrewRole = 3;

        /// <summary>
        /// Builds the cast lines from a TMDB credits response — <c>"Name (Character)"</c>, or just
        /// the name when TMDB has no character for the part.
        /// </summary>
        /// <remarks>
        /// The building used to live in the main window's code-behind, where it could not be
        /// tested and could not be reused. Correcting a wrong TMDB match needs the same lines
        /// built a second time, and two copies of this would have drifted.
        /// </remarks>
        public static List<string> Cast(TmdbService.TmdbCredits? credits)
        {
            var lines = new List<string>();
            if (credits is null) return lines;

            foreach (var member in credits.Cast.Take(MaxCast))
            {
                if (string.IsNullOrWhiteSpace(member.Name)) continue;

                lines.Add(string.IsNullOrWhiteSpace(member.Character)
                    ? member.Name
                    : $"{member.Name} ({member.Character})");
            }

            return lines;
        }

        /// <summary>Directors first, then writers, each labelled with the job it did.</summary>
        public static List<string> Crew(TmdbService.TmdbCredits? credits)
        {
            var lines = new List<string>();
            if (credits is null) return lines;

            foreach (var director in credits.Crew
                         .Where(x => string.Equals(x.Job, "Director", StringComparison.OrdinalIgnoreCase))
                         .Take(MaxPerCrewRole))
            {
                if (!string.IsNullOrWhiteSpace(director.Name)) lines.Add($"Director: {director.Name}");
            }

            // "Writer", "Screenplay" and "Story" are separate TMDB jobs and a film often has more
            // than one of them, so this matches on the word rather than on the exact job.
            foreach (var writer in credits.Crew
                         .Where(x => x.Job != null && x.Job.Contains("Writer", StringComparison.OrdinalIgnoreCase))
                         .Take(MaxPerCrewRole))
            {
                if (!string.IsNullOrWhiteSpace(writer.Name)) lines.Add($"Writer: {writer.Name}");
            }

            return lines;
        }

        /// <summary>The comma separated genre list a TMDB record describes, or an empty string.</summary>
        public static string Genres(TmdbService.TmdbDetails? details) =>
            details?.Genres is null
                ? ""
                : string.Join(", ", details.Genres.Select(g => g.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
    }
}
