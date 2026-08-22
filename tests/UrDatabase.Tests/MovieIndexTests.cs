using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// These rules are what make a re-scan idempotent, so they are asserted directly rather than
    /// only through a database.
    /// </summary>
    public class MovieIndexTests
    {
        [Theory]
        [InlineData("The Matrix", "the matrix")]
        [InlineData("THE MATRIX", "the  MATRIX")]
        [InlineData("Spider-Man", "Spider Man")]
        [InlineData("Ocean's Eleven", "Oceans Eleven")]
        [InlineData("Amélie", "Amelie")]
        [InlineData("Fast & Furious", "Fast and Furious")]
        [InlineData("WALL·E", "WALL E")]
        public void Two_spellings_of_one_title_normalise_to_the_same_thing(string left, string right)
        {
            Assert.Equal(MovieIndex.NormalizeTitle(left), MovieIndex.NormalizeTitle(right));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("!!!")]
        public void A_title_with_nothing_in_it_normalises_to_empty(string? title)
        {
            Assert.Equal("", MovieIndex.NormalizeTitle(title));
        }

        [Fact]
        public void Different_films_do_not_normalise_together()
        {
            Assert.NotEqual(MovieIndex.NormalizeTitle("The Thing"), MovieIndex.NormalizeTitle("Thing"));
            Assert.NotEqual(MovieIndex.NormalizeTitle("Alien"), MovieIndex.NormalizeTitle("Aliens"));
        }

        [Fact]
        public void The_key_separates_two_films_that_share_a_title()
        {
            Assert.NotEqual(MovieIndex.BuildKey("The Thing", 1982), MovieIndex.BuildKey("The Thing", 2011));
            Assert.NotEqual(MovieIndex.BuildKey("The Thing", 1982), MovieIndex.BuildKey("The Thing", null));
            Assert.Equal(MovieIndex.BuildKey("the.matrix", 1999), MovieIndex.BuildKey("The Matrix", 1999));
        }

        [Fact]
        public void A_year_less_name_is_treated_as_the_same_film_as_one_that_has_a_year()
        {
            Assert.True(MovieIndex.AreSameMovie(new ParsedMedia("The Matrix", null), new ParsedMedia("The Matrix", 1999)));
            Assert.True(MovieIndex.AreSameMovie(new ParsedMedia("The Matrix", 1999), new ParsedMedia("the matrix", 1999)));
        }

        [Fact]
        public void Two_films_with_the_same_title_and_different_years_are_not_the_same_film()
        {
            Assert.False(MovieIndex.AreSameMovie(new ParsedMedia("The Thing", 1982), new ParsedMedia("The Thing", 2011)));
            Assert.False(MovieIndex.AreSameMovie(new ParsedMedia("Heat", 1995), new ParsedMedia("Ronin", 1998)));
            Assert.False(MovieIndex.AreSameMovie(new ParsedMedia("", 1999), new ParsedMedia("", 1999)));
        }

        [Fact]
        public void An_exact_title_and_year_resolves_to_the_row_that_already_exists()
        {
            var index = new MovieIndex();
            index.Add(7, "The Matrix", 1999);

            Assert.True(index.TryResolve(new ParsedMedia("the.matrix", 1999), out var id, out var newYear));
            Assert.Equal(7, id);
            Assert.False(newYear);
        }

        [Fact]
        public void A_file_with_no_year_joins_the_movie_that_has_one()
        {
            var index = new MovieIndex();
            index.Add(7, "The Matrix", 1999);

            Assert.True(index.TryResolve(new ParsedMedia("The Matrix", null), out var id, out var newYear));
            Assert.Equal(7, id);
            Assert.False(newYear);
        }

        [Fact]
        public void A_file_with_a_year_fills_in_a_movie_that_has_none()
        {
            var index = new MovieIndex();
            index.Add(7, "The Matrix", null);

            Assert.True(index.TryResolve(new ParsedMedia("The Matrix", 1999), out var id, out var newYear));
            Assert.Equal(7, id);
            Assert.True(newYear);
        }

        [Fact]
        public void Once_a_year_is_known_the_next_file_is_not_asked_to_supply_one_again()
        {
            var index = new MovieIndex();
            index.Add(7, "The Matrix", null);
            index.TryResolve(new ParsedMedia("The Matrix", 1999), out _, out _);
            index.SetYear(7, "The Matrix", 1999);

            Assert.True(index.TryResolve(new ParsedMedia("The Matrix", 1999), out var id, out var newYear));
            Assert.Equal(7, id);
            Assert.False(newYear);

            // A different year is a different film, not a correction.
            Assert.False(index.TryResolve(new ParsedMedia("The Matrix", 2003), out _, out _));
        }

        [Fact]
        public void A_film_the_index_has_never_seen_does_not_resolve()
        {
            var index = new MovieIndex();
            index.Add(7, "The Matrix", 1999);

            Assert.False(index.TryResolve(new ParsedMedia("Heat", 1995), out _, out _));
            Assert.False(index.TryResolve(new ParsedMedia("The Thing", 1982), out _, out _));
            Assert.False(index.TryResolve(new ParsedMedia("", null), out _, out _));
        }

        [Fact]
        public void The_oldest_row_wins_when_two_rows_share_a_title()
        {
            var index = new MovieIndex();
            index.Add(3, "The Thing", 1982);
            index.Add(9, "The Thing", 2011);

            Assert.True(index.TryResolve(new ParsedMedia("The Thing", null), out var id, out _));
            Assert.Equal(3, id);
            Assert.Equal(2, index.Count);
        }

        [Fact]
        public void A_row_with_no_usable_title_is_ignored_rather_than_matching_everything()
        {
            var index = new MovieIndex();
            index.Add(1, "   ", 1999);

            Assert.Equal(0, index.Count);
            Assert.False(index.TryResolve(new ParsedMedia("Anything", 1999), out _, out _));
        }

        [Theory]
        [InlineData("+++")]
        [InlineData("_")]
        [InlineData("...")]
        public void A_title_that_is_only_punctuation_is_still_indexed(string title)
        {
            // It normalises to nothing, so keying on the normalised form alone made the row
            // invisible to the index and every scan inserted another copy of the same film.
            var index = new MovieIndex();
            index.Add(4, title, null);

            Assert.Equal(1, index.Count);
            Assert.True(index.TryResolve(new ParsedMedia(title, null), out var id, out _));
            Assert.Equal(4, id);
            Assert.False(index.TryResolve(new ParsedMedia("Something Else", null), out _, out _));
        }
    }
}
