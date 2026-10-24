using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>How long a film runs, in the bands the length filter offers.</summary>
    public enum RuntimeRange
    {
        Any = 0,

        /// <summary>Under an hour and a half.</summary>
        Short = 1,

        /// <summary>An hour and a half to two hours, both ends included: the ordinary feature.</summary>
        Feature = 2,

        /// <summary>Over two hours.</summary>
        Long = 3
    }

    /// <summary>
    /// The library narrowed by what a film is like rather than what it is filed under: how long it
    /// runs, how well it is rated, and who is in it or directed it.
    /// </summary>
    /// <remarks>
    /// Kept apart from the genre, source and kind rows because those divide the library into
    /// buckets every card belongs to exactly one or two of, and these do not: a film is long and
    /// well rated and has a given actor in it all at once. So these narrow whatever the other rows
    /// are showing, and the genre row counts what is left — which is what lets "dramas over two
    /// hours with Liv Ullmann" be asked with one click on a genre after two here.
    ///
    /// A film whose length or rating nobody has supplied yet does not match a filter on it. Letting
    /// it through would put a film of unknown length in "Under 90 min", which is a claim the app
    /// cannot make; hiding it is honest, and <see cref="Unknown"/> is how the window says so rather
    /// than leaving somebody to wonder where half a freshly scanned library went.
    /// </remarks>
    public sealed record LibraryRefinement
    {
        /// <summary>The shortest a feature is, in minutes. Anything shorter is <see cref="RuntimeRange.Short"/>.</summary>
        public const int FeatureMinimum = 90;

        /// <summary>The longest a feature is, in minutes. Anything longer is <see cref="RuntimeRange.Long"/>.</summary>
        public const int FeatureMaximum = 120;

        /// <summary>Nothing narrowed, which is how the library opens.</summary>
        public static LibraryRefinement None { get; } = new();

        public RuntimeRange Runtime { get; init; } = RuntimeRange.Any;

        /// <summary>The lowest rating out of ten a film may have, or null for any rating.</summary>
        public double? MinimumRating { get; init; }

        /// <summary>
        /// Part of an actor's or a director's name, as typed. Every word has to appear in the same
        /// person's name, so "chris nolan" finds Christopher Nolan and not a film that happens to
        /// star one Chris and one Nolan.
        /// </summary>
        public string Person { get; init; } = "";

        /// <summary>The words of <see cref="Person"/>, without the spacing somebody typed around them.</summary>
        public IReadOnlyList<string> PersonWords =>
            (Person ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>Whether anything is being narrowed at all.</summary>
        public bool IsActive =>
            Runtime != RuntimeRange.Any || MinimumRating is not null || PersonWords.Count > 0;

        /// <summary>Whether a card passes every filter that is set.</summary>
        public bool Matches(UiMovie? movie) => Matches(movie, unknownPasses: false);

        /// <summary>
        /// The same test, optionally treating a length or rating nobody has supplied as a pass.
        /// The difference between the two answers is exactly the films hidden for want of a fact.
        /// </summary>
        internal bool Matches(UiMovie? movie, bool unknownPasses)
        {
            if (movie is null) return false;

            if (Runtime != RuntimeRange.Any)
            {
                if (movie.RuntimeMinutes is int minutes && minutes > 0)
                {
                    if (BandOf(minutes) != Runtime) return false;
                }
                else if (!unknownPasses)
                {
                    return false;
                }
            }

            if (MinimumRating is double floor)
            {
                if (movie.Rating is double rating && rating > 0)
                {
                    if (rating + 1e-9 < floor) return false;
                }
                else if (!unknownPasses)
                {
                    return false;
                }
            }

            var words = PersonWords;
            if (words.Count > 0)
            {
                var people = movie.People.ToList();

                if (people.Count == 0) return unknownPasses;
                if (!people.Any(name => words.All(word => Contains(name, word)))) return false;
            }

            return true;
        }

        /// <summary>Which band a running time falls in.</summary>
        public static RuntimeRange BandOf(int minutes) =>
            minutes < FeatureMinimum ? RuntimeRange.Short
            : minutes > FeatureMaximum ? RuntimeRange.Long
            : RuntimeRange.Feature;

        /// <summary>
        /// Case and accents ignored: somebody typing "almodovar" is looking for Almodóvar, and a
        /// filter that made them find the accent key first would look like it had nobody to find.
        /// </summary>
        private static bool Contains(string haystack, string needle) =>
            CultureInfo.InvariantCulture.CompareInfo.IndexOf(
                haystack, needle, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

        // ---------- the controls ----------

        /// <summary>The length controls, in the order they appear.</summary>
        public static IReadOnlyList<RuntimeRange> RuntimeOptions { get; } = new[]
        {
            RuntimeRange.Any, RuntimeRange.Short, RuntimeRange.Feature, RuntimeRange.Long
        };

        /// <summary>
        /// The rating controls, in the order they appear: any, then the floors worth having. Below
        /// six is not offered — "at least five" excludes almost nothing anybody keeps a copy of.
        /// </summary>
        public static IReadOnlyList<double?> RatingOptions { get; } = new double?[] { null, 6, 7, 8 };

        public static string Label(RuntimeRange range) => range switch
        {
            RuntimeRange.Short => $"Under {FeatureMinimum} min",
            RuntimeRange.Feature => $"{FeatureMinimum}–{FeatureMaximum} min",
            RuntimeRange.Long => "Over 2 hours",
            _ => "Any length"
        };

        public static string RatingLabel(double? minimum) =>
            minimum is double floor
                ? $"{floor.ToString("0.#", CultureInfo.InvariantCulture)}+"
                : "Any rating";

        // ---------- applying it ----------

        /// <summary>The cards that pass, in the order they came.</summary>
        public IReadOnlyList<UiMovie> Apply(IEnumerable<UiMovie>? movies)
        {
            if (movies is null) return new List<UiMovie>();
            if (!IsActive) return movies.ToList();

            return movies.Where(Matches).ToList();
        }

        /// <summary>
        /// How many cards are hidden only because a length, rating or cast this filter needs is
        /// not known for them yet. Counted on <see cref="UiMovie.Key"/>, like every other count in
        /// the library, because every server card carries local id 0.
        /// </summary>
        public int Unknown(IEnumerable<UiMovie>? movies)
        {
            if (movies is null || !IsActive) return 0;

            return movies
                .Where(m => m is not null && !Matches(m) && Matches(m, unknownPasses: true))
                .Select(m => m.Key)
                .Distinct(StringComparer.Ordinal)
                .Count();
        }

        /// <summary>
        /// The line beside the filters that owns up to <see cref="Unknown"/>, or an empty string
        /// when nothing is hidden for that reason.
        /// </summary>
        public static string UnknownNote(int hidden) => hidden switch
        {
            <= 0 => "",
            1 => "1 more not shown: its details are not known yet",
            _ => $"{hidden.ToString(CultureInfo.InvariantCulture)} more not shown: their details are not known yet"
        };
    }
}
