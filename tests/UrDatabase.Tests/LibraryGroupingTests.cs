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

        /// <summary>
        /// The genre row shows a count beside each genre, because a row without them tells you a
        /// library has a Western bucket but not whether it holds two films or two hundred.
        /// </summary>
        [Fact]
        public void Every_genre_chip_carries_how_many_films_are_behind_it()
        {
            var movies = new[]
            {
                new UiMovie { Id = 1, Title = "Heat", Genres = "Crime, Drama" },
                new UiMovie { Id = 2, Title = "Sicario", Genres = "Crime" },
                new UiMovie { Id = 3, Title = "Alien", Genres = "Horror" },
            };

            var chips = LibraryGrouping.BuildGenreChips(movies);

            Assert.Equal(2, chips.Single(c => c.Name == "Crime").Count);
            Assert.Equal(1, chips.Single(c => c.Name == "Drama").Count);
            Assert.Equal(1, chips.Single(c => c.Name == "Horror").Count);
        }

        /// <summary>
        /// A film with three genres sits on three shelves, so adding the buckets up reports a
        /// library several times larger than it is. "All" has to count films, not shelf places.
        /// </summary>
        [Fact]
        public void All_counts_films_and_not_the_sum_of_the_buckets()
        {
            var movies = new[]
            {
                new UiMovie { Id = 1, Title = "Heat", Genres = "Crime, Drama, Thriller" },
                new UiMovie { Id = 2, Title = "Alien", Genres = "Horror, Science Fiction" },
            };

            var all = LibraryGrouping.BuildGenreChips(movies).Single(c => c.Name == LibraryGrouping.AllGenres);

            Assert.Equal(2, all.Count);
        }

        /// <summary>
        /// Every server film carries local id 0, so counting on the id alone collapses the whole
        /// remote library into a single film.
        /// </summary>
        [Fact]
        public void Server_films_are_counted_individually_despite_sharing_a_local_id()
        {
            var movies = new[]
            {
                new UiMovie { Title = "Ran", Source = MovieSource.Jellyfin, RemoteId = "a", Genres = "Drama" },
                new UiMovie { Title = "Solaris", Source = MovieSource.Jellyfin, RemoteId = "b", Genres = "Drama" },
                new UiMovie { Title = "Heat", Id = 4, Genres = "Crime" },
            };

            var chips = LibraryGrouping.BuildGenreChips(movies);

            Assert.Equal(3, chips.Single(c => c.Name == LibraryGrouping.AllGenres).Count);
            Assert.Equal(2, chips.Single(c => c.Name == "Drama").Count);
        }

        [Fact]
        public void The_chip_row_leads_with_All_and_ends_with_Uncategorised()
        {
            var movies = new[] { Movie("Heat", "Crime"), Movie("Unknown Film") };

            var chips = LibraryGrouping.BuildGenreChips(movies);

            Assert.Equal(LibraryGrouping.AllGenres, chips[0].Name);
            Assert.Equal(LibraryGrouping.Uncategorised, chips[^1].Name);
            Assert.Equal(1, chips[^1].Count);
        }

        [Fact]
        public void An_empty_library_still_offers_the_All_chip_reading_zero()
        {
            foreach (var chips in new[] { LibraryGrouping.BuildGenreChips(null), LibraryGrouping.BuildGenreChips(new UiMovie[0]) })
            {
                var only = Assert.Single(chips);
                Assert.Equal(LibraryGrouping.AllGenres, only.Name);
                Assert.Equal(0, only.Count);
            }
        }

        /// <summary>
        /// "1 films" is the sort of thing that survives review for years, so the singular is
        /// decided once, here, rather than in a format string in the view.
        /// </summary>
        [Theory]
        [InlineData(0, "0 FILMS")]
        [InlineData(1, "1 FILM")]
        [InlineData(2, "2 FILMS")]
        [InlineData(140, "140 FILMS")]
        public void The_count_beside_a_shelf_heading_gets_its_plural_right(int count, string expected)
        {
            Assert.Equal(expected, LibraryGrouping.CountLabel(count));
        }

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
