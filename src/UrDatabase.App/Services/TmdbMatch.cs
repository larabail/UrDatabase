using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UrDatabase.Services
{
    /// <summary>
    /// Decides which TMDB search result, if any, is the film we asked about.
    ///
    /// The app used to take <c>results[0]</c> and ask nothing further. TMDB's search is a
    /// relevance ranking, not an identification: it always returns its best guess, and its best
    /// guess for a short title is very often a longer film that contains it. <em>El Drama</em>
    /// returned <em>El Sabor del Drama</em>, whose poster was then written to the catalogue as if
    /// it were the film's own — and, because the poster column was only ever filled when empty,
    /// stayed there.
    ///
    /// So the rules here are the same shape as <see cref="MovieFileMatcher"/>'s: the title has to
    /// agree exactly once normalised, the year has to corroborate rather than contradict, and a
    /// result that satisfies neither is refused. No poster is a smaller wrong than another film's
    /// poster, because an empty frame invites the fix and a confident wrong one does not.
    /// </summary>
    public static class TmdbMatch
    {
        /// <summary>
        /// One TMDB search result, reduced to the fields that identify a film. Kept separate from
        /// the service's JSON DTOs so the rules can be tested without a response to deserialise.
        /// </summary>
        public sealed class Candidate
        {
            [JsonPropertyName("id")] public int Id { get; init; }

            /// <summary>The title in the requested language, which is what TMDB sorts and shows.</summary>
            [JsonPropertyName("title")] public string? Title { get; init; }

            /// <summary>
            /// The title in the language the film was made in. Checked as well as
            /// <see cref="Title"/> because a library names its files either way round: a Spanish
            /// film catalogued as <em>El Drama</em> is <em>The Drama</em> to TMDB's English
            /// search, and matching on the localised title alone would refuse its own film.
            /// </summary>
            [JsonPropertyName("original_title")] public string? OriginalTitle { get; init; }

            [JsonPropertyName("poster_path")] public string? PosterPath { get; init; }

            /// <summary>TMDB's <c>release_date</c>, <c>yyyy-MM-dd</c>. Often absent, sometimes empty.</summary>
            [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; }

            [JsonPropertyName("overview")] public string? Overview { get; init; }

            /// <summary>
            /// TMDB's genre ids for the film, which a search result carries and a details request
            /// would otherwise have to be made for.
            /// </summary>
            /// <remarks>
            /// Ids and not names: <c>/search/movie</c> gives only the numbers, and the names come
            /// from a list that is the same for every film and is fetched once per run of the app
            /// — see <see cref="TmdbService.GenreNamesAsync"/>. That is what keeps genres free. The
            /// alternative, asking <c>/movie/{id}</c> per film for the spelled-out names, would
            /// double the number of requests a library makes, and on a few thousand films that is
            /// the difference between filling the column in and being rate limited while trying.
            /// </remarks>
            [JsonPropertyName("genre_ids")] public List<int>? GenreIds { get; init; }

            /// <summary>The release year, when the date is present and parses. Null otherwise.</summary>
            [JsonIgnore] public int? Year => ParseYear(ReleaseDate);
        }

        // Ordered worst to best, as in MovieFileMatcher.
        private const int Rejected = 0;
        private const int TitleOnly = 1;
        private const int TitleAndNearYear = 2;
        private const int TitleAndYear = 3;

        /// <summary>
        /// How far a candidate's year may sit from the catalogued one and still count as the same
        /// film. A release crosses new year somewhere — a festival run in December is a general
        /// release in January, and the two dates land either side of it — so demanding the exact
        /// year would refuse films over a difference that means nothing. Two years apart is a
        /// different film or a remake, and is refused.
        /// </summary>
        private const int YearTolerance = 1;

        /// <summary>
        /// The result that is this film, or null when none of them is.
        /// </summary>
        /// <param name="results">TMDB's results, in the order TMDB returned them.</param>
        /// <param name="title">The catalogued title, however it is spelt.</param>
        /// <param name="year">
        /// The catalogued year, when there is one. Without it a title match is accepted on its
        /// own, unless matching titles point to different release years.
        /// </param>
        public static Candidate? ChooseBest(IReadOnlyList<Candidate>? results, string? title, int? year)
        {
            if (results is null || results.Count == 0) return null;

            var wanted = NormalizeTitle(title);
            if (wanted.Length == 0) return null;

            Candidate? best = null;
            var bestRank = Rejected;
            var ambiguous = false;

            foreach (var candidate in results)
            {
                if (candidate is null) continue;

                var rank = Rank(candidate, wanted, year);

                if (rank > bestRank)
                {
                    best = candidate;
                    bestRank = rank;
                    ambiguous = false;
                }
                else if (rank != Rejected && rank == bestRank && best is not null
                    && candidate.Id != best.Id && candidate.Year != best.Year)
                    ambiguous = true;
            }

            return bestRank == Rejected || ambiguous ? null : best;
        }

        private static int Rank(Candidate candidate, string wantedTitle, int? wantedYear)
        {
            if (candidate.Id <= 0) return Rejected;

            var candidateYear = candidate.Year;
            var titlesAgree = TitlesAgree(candidate, wantedTitle);
            if (wantedYear is null || candidateYear is null) return titlesAgree ? TitleOnly : Rejected;

            var distance = Math.Abs(candidateYear.Value - wantedYear.Value);
            if (!titlesAgree && !(distance <= YearTolerance && InitialsAgree(candidate, wantedTitle)))
                return Rejected;
            if (distance == 0) return TitleAndYear;
            if (distance <= YearTolerance) return TitleAndNearYear;

            // Not merely a worse answer: Dune (2021) is not a poor match for Dune (1984), it is
            // the other film.
            return Rejected;
        }

        private static bool TitlesAgree(Candidate candidate, string wantedTitle) =>
            string.Equals(NormalizeTitle(candidate.Title), wantedTitle, StringComparison.Ordinal) ||
            string.Equals(NormalizeTitle(candidate.OriginalTitle), wantedTitle, StringComparison.Ordinal);

        internal static string NormalizeTitle(string? title)
        {
            var reordered = Regex.Replace(title ?? "", @"^(.+),\s*(the|an|a)\s*$", "$2 $1",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return MovieIndex.NormalizeTitle(reordered);
        }

        // Only join runs of initials, not arbitrary word boundaries ("Therapist" != "The Rapist").
        private static string JoinInitials(string title) =>
            Regex.Replace(title, @"(?<!\S)(?:\p{L} ){1,}\p{L}(?!\S)", match => match.Value.Replace(" ", ""));

        private static bool InitialsAgree(Candidate candidate, string wantedTitle)
        {
            var wanted = JoinInitials(wantedTitle);
            return wanted == JoinInitials(NormalizeTitle(candidate.Title))
                || wanted == JoinInitials(NormalizeTitle(candidate.OriginalTitle));
        }

        /// <summary>
        /// The year out of a TMDB release date. TMDB sends <c>yyyy-MM-dd</c>, but also sends an
        /// empty string for a film with no known date, so this reads the leading year rather than
        /// parsing a whole date and never throws for a caller who only wants a number.
        /// </summary>
        public static int? ParseYear(string? releaseDate)
        {
            if (string.IsNullOrWhiteSpace(releaseDate)) return null;

            var text = releaseDate.Trim();
            if (text.Length < 4) return null;

            return int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year > 1800
                ? year
                : null;
        }
    }
}
