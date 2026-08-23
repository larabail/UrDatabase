using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

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
        /// own, which is all the evidence there is.
        /// </param>
        public static Candidate? ChooseBest(IReadOnlyList<Candidate>? results, string? title, int? year)
        {
            if (results is null || results.Count == 0) return null;

            var wanted = MovieIndex.NormalizeTitle(title);
            if (wanted.Length == 0) return null;

            Candidate? best = null;
            var bestRank = Rejected;

            foreach (var candidate in results)
            {
                if (candidate is null) continue;

                var rank = Rank(candidate, wanted, year);

                // Strictly greater, so a tie leaves the earlier result standing. Everything that
                // reaches the winning rank has the same normalised title and a year that agrees,
                // which makes a tie two records of one film rather than two films; TMDB orders by
                // popularity, so its first is the one people mean. This is the one place the rules
                // differ from MovieFileMatcher, which discards ties — there a tie meant opening the
                // wrong film, and here it means the same film's other poster, which a person can
                // now change.
                if (rank > bestRank)
                {
                    best = candidate;
                    bestRank = rank;
                }
            }

            return bestRank == Rejected ? null : best;
        }

        private static int Rank(Candidate candidate, string wantedTitle, int? wantedYear)
        {
            if (!TitlesAgree(candidate, wantedTitle)) return Rejected;

            var candidateYear = candidate.Year;
            if (wantedYear is null || candidateYear is null) return TitleOnly;

            var distance = Math.Abs(candidateYear.Value - wantedYear.Value);
            if (distance == 0) return TitleAndYear;
            if (distance <= YearTolerance) return TitleAndNearYear;

            // Not merely a worse answer: Dune (2021) is not a poor match for Dune (1984), it is
            // the other film.
            return Rejected;
        }

        private static bool TitlesAgree(Candidate candidate, string wantedTitle) =>
            string.Equals(MovieIndex.NormalizeTitle(candidate.Title), wantedTitle, StringComparison.Ordinal) ||
            string.Equals(MovieIndex.NormalizeTitle(candidate.OriginalTitle), wantedTitle, StringComparison.Ordinal);

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
