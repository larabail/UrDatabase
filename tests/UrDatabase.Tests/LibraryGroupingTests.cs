using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// A freshly scanned library has no genres — those only arrive with TMDB enrichment, which
    /// needs a key. Grouping strictly by genre rendered that library as a blank page, which looks
    /// exactly like a scan that did nothing.
    /// </summary>
    public class LibraryGroupingTests
    {
        private static UiMovie Movie(string title, string? genres = null, int? year = null) =>
            new() { Title = title, Genres = genres, Year = year };

        [Fact]
        public void The_genre_list_starts_with_All_and_lists_what_the_library_actually_has()
        {
            var movies = new[]
            {
                Movie("Heat", "Crime, Drama"),
                Movie("Alien", "Horror|Science Fiction"),
            };

            var genres = LibraryGrouping.BuildGenreList(movies);

            Assert.Equal("All", genres[0]);
            Assert.Equal(new[] { "All", "Crime", "Drama", "Horror", "Science Fiction" }, genres);
        }

        [Fact]
        public void A_film_with_no_genre_gets_a_bucket_of_its_own()
        {
            var movies = new[] { Movie("Heat", "Crime"), Movie("The Matrix") };

            var genres = LibraryGrouping.BuildGenreList(movies);

            Assert.Contains(LibraryGrouping.Uncategorised, genres);
            Assert.Equal(LibraryGrouping.Uncategorised, genres[^1]);
        }

        [Fact]
        public void A_library_where_everything_has_a_genre_has_no_uncategorised_bucket()
        {
            var genres = LibraryGrouping.BuildGenreList(new[] { Movie("Heat", "Crime") });

            Assert.DoesNotContain(LibraryGrouping.Uncategorised, genres);
        }

        [Fact]
        public void A_freshly_scanned_library_is_still_reachable_from_the_chips()
        {
            // Exactly the state a scan leaves behind on a build with no TMDB key.
            var movies = new[] { Movie("The Matrix", year: 1999), Movie("Heat", year: 1995) };

            var genres = LibraryGrouping.BuildGenreList(movies);
            var items = LibraryGrouping.ItemsForGenre(movies, LibraryGrouping.Uncategorised);

            Assert.Equal(new[] { "All", LibraryGrouping.Uncategorised }, genres);
            Assert.Equal(2, items.Count);
        }

        [Fact]
        public void An_empty_library_still_offers_the_All_chip()
        {
            Assert.Equal(new[] { "All" }, LibraryGrouping.BuildGenreList(System.Array.Empty<UiMovie>()));
            Assert.Equal(new[] { "All" }, LibraryGrouping.BuildGenreList(null));
        }

        [Fact]
        public void A_bucket_holds_only_the_films_in_it_and_matches_case_insensitively()
        {
            var movies = new[]
            {
                Movie("Heat", "Crime, Drama"),
                Movie("Alien", "Horror"),
                Movie("The Matrix"),
            };

            Assert.Equal(new[] { "Heat" }, LibraryGrouping.ItemsForGenre(movies, "crime").Select(m => m.Title));
            Assert.Equal(new[] { "The Matrix" }, LibraryGrouping.ItemsForGenre(movies, LibraryGrouping.Uncategorised).Select(m => m.Title));
            Assert.Empty(LibraryGrouping.ItemsForGenre(movies, "Western"));
        }

        [Fact]
        public void A_bucket_is_ordered_newest_first_then_by_title()
        {
            var movies = new[]
            {
                Movie("Older", "Drama", 1990),
                Movie("Newer B", "Drama", 2020),
                Movie("Newer A", "Drama", 2020),
                Movie("Undated", "Drama"),
            };

            var titles = LibraryGrouping.ItemsForGenre(movies, "Drama").Select(m => m.Title);

            Assert.Equal(new[] { "Newer A", "Newer B", "Older", "Undated" }, titles);
        }

        [Fact]
        public void Nothing_in_yields_an_empty_bucket_rather_than_an_exception()
        {
            Assert.Empty(LibraryGrouping.ItemsForGenre(null, "Drama"));
            Assert.Empty(LibraryGrouping.ItemsForGenre(new[] { Movie("Heat", "Crime") }, null));
            Assert.Empty(LibraryGrouping.ItemsForGenre(new[] { Movie("Heat", "Crime") }, "  "));
        }
    }
}
