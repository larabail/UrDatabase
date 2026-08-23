using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Decides which of the nominations filed under a title actually belong to the film on screen.
    /// </summary>
    /// <remarks>
    /// The archive is searched by name, and a name is not a film. There are four films called
    /// "A Star Is Born" and three of them were nominated; showing 1937's awards on 2018's copy
    /// would be worse than showing none, because it is wrong in a way that looks authoritative.
    ///
    /// So the release year decides. A film released in a calendar year is eligible at the
    /// ceremony held early the following year, which makes the ceremony normally the release year
    /// plus one. The window is wider than that in both directions on purpose:
    ///
    /// <list type="bullet">
    ///   <item><description>The early ceremonies did not follow the rule. The first covered
    ///     1927 and 1928 and was held in 1929; the sixth covered seventeen months. A ceremony in
    ///     the same year as the film is therefore possible and has to be allowed.</description></item>
    ///   <item><description>The international feature award runs a year behind, because a country
    ///     submits a film after its own release. Two and three year gaps are ordinary
    ///     there.</description></item>
    ///   <item><description>Catalogue years disagree with the Academy's by one all the time —
    ///     a festival premiere in December against a general release in January.</description></item>
    /// </list>
    ///
    /// Three years is wide enough for all of that and still narrower than the gap between any two
    /// remakes sharing a title, which is the failure this exists to prevent.
    ///
    /// A film whose year the catalogue does not know gets a different rule: its results are
    /// accepted only if they all fall within one three-year span, which means the archive holds
    /// exactly one film by that name. If they span more, the app cannot tell which film it is
    /// holding and says nothing rather than guessing.
    /// </remarks>
    public static class OscarMatch
    {
        /// <summary>How many years after a film's release its ceremony may fall.</summary>
        internal const int LatestGap = 3;

        /// <summary>
        /// The nominations that belong to this film, in the archive's own order, or an empty set
        /// when none can be attributed to it with confidence.
        /// </summary>
        public static OscarHonours For(IEnumerable<OscarNomination>? candidates, int? releaseYear)
        {
            var all = candidates?.Where(n => n is not null).ToList() ?? new List<OscarNomination>();
            if (all.Count == 0) return OscarHonours.None;

            if (releaseYear is int year)
            {
                var window = all
                    .Where(n => n.Ceremony >= year && n.Ceremony <= year + LatestGap)
                    .ToList();

                return window.Count == 0 ? OscarHonours.None : new OscarHonours { Nominations = window };
            }

            // No year to check against. One film's worth of ceremonies is acceptable; two films'
            // is not, and there is no way to tell them apart from here.
            var years = all.Select(n => n.Ceremony).ToList();
            if (years.Max() - years.Min() > LatestGap) return OscarHonours.None;

            return new OscarHonours { Nominations = all };
        }

        /// <summary>
        /// The line above the list: "1 win · 4 nominations". Written out in words rather than as
        /// numerals with symbols, because "2/5" beside a poster reads as a rating.
        /// </summary>
        public static string Summary(OscarHonours? honours)
        {
            if (honours is null || !honours.Any) return "";

            var nominations = Plural(honours.Total, "nomination");

            return honours.Wins == 0
                ? nominations
                : $"{Plural(honours.Wins, "win")} · {nominations}";
        }

        private static string Plural(int count, string noun) =>
            count == 1 ? $"1 {noun}" : $"{count} {noun}s";

        /// <summary>
        /// How a nomination reads on one line. The film's own name is dropped from the detail —
        /// it is the title at the top of the screen, and repeating it on nine consecutive rows
        /// leaves no room for the names that are actually new information.
        /// </summary>
        public static string Line(OscarNomination nomination, string filmTitle)
        {
            if (nomination is null) return "";

            var nominee = (nomination.Nominee ?? "").Trim();
            var detail = (nomination.Detail ?? "").Trim();

            if (Matches(nominee, filmTitle)) return detail;
            if (Matches(detail, filmTitle)) return nominee;

            return nominee.Length > 0 ? nominee : detail;
        }

        private static bool Matches(string value, string title) =>
            value.Length > 0 && string.Equals(value, (title ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// How many awards are listed under the poster before the rest become a count. The column
        /// is 250 pixels wide and starts below a 375-pixel poster; a film with fourteen
        /// nominations would otherwise run off the bottom of the window.
        /// </summary>
        public const int MaxRows = 7;

        /// <summary>
        /// The awards as they are printed, wins first.
        /// </summary>
        /// <remarks>
        /// The ordering is the part that matters. Sinners took ten nominations at the 2026
        /// ceremony and won three of them, and the archive lists them by category — so a list cut
        /// at seven in the archive's own order can drop a win and leave the panel claiming the
        /// film merely competed. Wins are therefore always printed, and it is the nominations
        /// that get counted instead.
        /// </remarks>
        public static IReadOnlyList<AwardRow> Rows(OscarHonours? honours, string? filmTitle, int max = MaxRows)
        {
            if (honours is null || !honours.Any) return Array.Empty<AwardRow>();

            return honours.Nominations
                .OrderByDescending(n => n.Won)
                .Take(Math.Max(max, 0))
                .Select(n => new AwardRow
                {
                    Category = Shorten(n.Category),
                    Detail = Line(n, filmTitle ?? ""),
                    Won = n.Won
                })
                .ToList();
        }

        /// <summary>
        /// The line under a truncated list, or empty when nothing was left out. Says "more
        /// nominations" rather than "more awards" because everything omitted is one, by
        /// construction — <see cref="Rows"/> never drops a win.
        /// </summary>
        public static string MoreNotice(OscarHonours? honours, int max = MaxRows)
        {
            if (honours is null || !honours.Any) return "";

            var hidden = honours.Total - Math.Max(max, 0);
            if (hidden <= 0) return "";

            return hidden == 1 ? "and 1 more nomination" : $"and {hidden} more nominations";
        }

        /// <summary>
        /// Trims the Academy's own phrasing down to what fits a 250 pixel column, without
        /// changing which award it is. Every substitution here is a name the Academy itself has
        /// used for the same award in another year — "Best Achievement in Film Editing" and "Best
        /// Film Editing", "Best Music Written for Motion Pictures (Original Song)" and "Best
        /// Original Song" — so nothing is invented and nothing is merged. The long forms are house
        /// style rather than information, and they wrap to three lines each.
        /// </summary>
        internal static string Shorten(string? category)
        {
            var value = (category ?? "").Trim();
            if (value.Length == 0) return "";

            value = value.Replace("Best Achievement in ", "Best ", StringComparison.Ordinal);
            value = value.Replace("Best Performance by an ", "Best ", StringComparison.Ordinal);
            value = value.Replace("Best Performance by a ", "Best ", StringComparison.Ordinal);

            // Ordered longest first: "Best Motion Picture of the Year" has to be recognised whole
            // before anything else gets at "Motion Picture".
            value = value switch
            {
                "Best Motion Picture of the Year" => "Best Picture",
                "Best Music Written for Motion Pictures (Original Song)" => "Best Original Song",
                "Best Music Written for Motion Pictures (Original Score)" => "Best Original Score",
                "Best Music Written for Motion Pictures (Original Musical or Comedy Score)"
                    => "Best Original Score",
                "Best Writing, Screenplay Based on Material Previously Produced or Published"
                    => "Best Adapted Screenplay",
                "Best Writing, Screenplay Written Directly for the Screen" => "Best Original Screenplay",
                "Best Foreign Language Film of the Year" => "Best International Feature",
                "Best International Feature Film of the Year" => "Best International Feature",
                _ => value
            };

            return value;
        }
    }
}
