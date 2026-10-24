using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Reads a film's length, rating and people out of a TMDB record, and keeps them in the
    /// catalogue's <c>movies</c> row so the library can be filtered on them with no network.
    /// </summary>
    /// <remarks>
    /// A server film has always carried these in <c>jellyfin_movies</c>. A scanned one had nowhere
    /// to keep them — the details screen fetched them, showed them and threw them away — so a
    /// filter on any of them could only ever have found the server's films.
    /// </remarks>
    public static class FilmFactsStore
    {
        /// <summary>
        /// The facts a TMDB record describes. The credits come from the record itself when it was
        /// asked for with them appended, or from a separate credits response when they were not.
        /// </summary>
        /// <remarks>
        /// A runtime or rating of zero is TMDB's way of saying it does not know — a film nobody has
        /// voted on averages 0.0 — so both are recorded as unknown rather than as a film that runs
        /// no time at all, or one rated as badly as a film can be.
        /// </remarks>
        public static FilmFacts From(TmdbService.TmdbDetails? details, TmdbService.TmdbCredits? credits = null)
        {
            if (details is null && credits is null) return FilmFacts.None;

            var people = credits ?? details?.Credits;

            return new FilmFacts(
                details?.Runtime is int minutes && minutes > 0 ? minutes : null,
                NormaliseRating(details?.VoteAverage),
                CreditLine.Cast(people),
                CreditLine.Crew(people));
        }

        /// <summary>A rating on TMDB's and Jellyfin's scale of ten, or null when there is none.</summary>
        public static double? NormaliseRating(double? rating) =>
            rating is double value && !double.IsNaN(value) && value > 0
                ? Math.Round(Math.Min(value, 10d), 1, MidpointRounding.AwayFromZero)
                : null;

        /// <summary>
        /// Replaces what the catalogue knows about a film's length, rating and people, and records
        /// that it has asked.
        /// </summary>
        /// <remarks>
        /// Replaces rather than fills in, unlike the genres. Nothing but TMDB writes these columns,
        /// and they are only ever asked for when the film has none or when somebody has just said
        /// which film this is — at which point whatever is stored describes the wrong film.
        ///
        /// An empty answer is recorded too. A film TMDB holds no runtime for is a fact about that
        /// film, and leaving the timestamp unset would ask the same question on every launch.
        /// </remarks>
        public static Task SaveAsync(
            SqliteConnection conn,
            long movieId,
            FilmFacts facts,
            CancellationToken ct = default) =>
            DatabaseWriteLane.RunAsync(conn, async token =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
UPDATE movies
SET runtime_minutes=@runtime, rating=@rating, cast_list=@cast, crew_list=@crew, facts_fetched_at=@fetched
WHERE id=@id";

                cmd.Parameters.AddWithValue("@id", movieId);
                cmd.Parameters.AddWithValue("@runtime", (object?)facts.RuntimeMinutes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@rating", (object?)facts.Rating ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@cast", NullIfEmpty(JellyfinCache.JoinCredits(facts.Cast)));
                cmd.Parameters.AddWithValue("@crew", NullIfEmpty(JellyfinCache.JoinCredits(facts.Crew)));
                cmd.Parameters.AddWithValue("@fetched", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }, ct);

        private static object NullIfEmpty(string value) => value.Length == 0 ? DBNull.Value : value;
    }
}
