using System.Linq;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class MovieFileMatcherTests
    {
        [Fact]
        public void Returns_null_when_there_are_no_files()
        {
            Assert.Null(MovieFileMatcher.FindBestMatch(Enumerable.Empty<string>(), "The Movie"));
        }

        [Fact]
        public void Returns_null_for_a_blank_title()
        {
            Assert.Null(MovieFileMatcher.FindBestMatch(new[] { "/movies/The Movie.mkv" }, "  "));
        }

        [Fact]
        public void Finds_a_file_whose_name_contains_the_title()
        {
            var files = new[] { "/movies/Other.mkv", "/movies/The Movie (1999) 1080p.mkv" };

            Assert.Equal("/movies/The Movie (1999) 1080p.mkv", MovieFileMatcher.FindBestMatch(files, "The Movie"));
        }

        [Fact]
        public void Matching_ignores_case_so_it_works_on_case_sensitive_filesystems()
        {
            var files = new[] { "/movies/THE MOVIE.mkv" };

            Assert.Equal("/movies/THE MOVIE.mkv", MovieFileMatcher.FindBestMatch(files, "the movie"));
        }

        [Fact]
        public void An_exact_name_beats_a_partial_one()
        {
            var files = new[] { "/movies/The Movie Returns.mkv", "/movies/The Movie.mkv" };

            Assert.Equal("/movies/The Movie.mkv", MovieFileMatcher.FindBestMatch(files, "The Movie"));
        }

        [Fact]
        public void Works_with_windows_paths()
        {
            var files = new[] { @"D:\Movies\The Movie (1999).mkv" };

            Assert.Equal(@"D:\Movies\The Movie (1999).mkv", MovieFileMatcher.FindBestMatch(files, "The Movie"));
        }

        [Fact]
        public void Ignores_blank_entries()
        {
            var files = new[] { "", "   ", "/movies/The Movie.mkv" };

            Assert.Equal("/movies/The Movie.mkv", MovieFileMatcher.FindBestMatch(files, "The Movie"));
        }

        [Fact]
        public void Returns_null_when_nothing_matches()
        {
            var files = new[] { "/movies/Something Else.mkv" };

            Assert.Null(MovieFileMatcher.FindBestMatch(files, "The Movie"));
        }

        /// <summary>
        /// "It" is inside "Spir<b>it</b>ed", and a raw substring search cannot tell the
        /// difference. This is the failure the Play button shipped: a two letter title opening
        /// somebody else's film.
        /// </summary>
        [Fact]
        public void A_title_hiding_inside_another_word_is_not_a_match()
        {
            var files = new[] { "/movies/Spirited Away.mkv", "/movies/Whiplash.mkv" };

            Assert.Null(MovieFileMatcher.FindBestMatch(files, "It"));
            Assert.Null(MovieFileMatcher.FindBestMatch(files, "Up"));
            Assert.Null(MovieFileMatcher.FindBestMatch(files, "Her"));
            Assert.Null(MovieFileMatcher.FindBestMatch(files, "Ash"));
        }

        /// <summary>
        /// A short title is a real title, but it is not evidence on its own: it turns up at a
        /// word boundary inside longer names constantly. Without a year to corroborate it, there
        /// is nothing here worth guessing on.
        /// </summary>
        [Fact]
        public void A_short_title_needs_a_year_before_it_will_match_loosely()
        {
            var files = new[] { "/movies/It Follows (2014).mkv" };

            Assert.Null(MovieFileMatcher.FindBestMatch(files, "It"));
            Assert.Null(MovieFileMatcher.FindBestMatch(files, "It", 2017));
        }

        [Fact]
        public void A_short_title_still_matches_a_file_named_after_it()
        {
            var files = new[] { "/movies/It Follows (2014).mkv", "/movies/It (2017).mkv" };

            Assert.Equal("/movies/It (2017).mkv", MovieFileMatcher.FindBestMatch(files, "It", 2017));
        }

        [Fact]
        public void An_exact_stem_matches_a_short_title_without_any_year()
        {
            var files = new[] { "/movies/Up.mkv" };

            Assert.Equal("/movies/Up.mkv", MovieFileMatcher.FindBestMatch(files, "Up"));
        }

        /// <summary>A remake and its original are two films, and the year is what separates them.</summary>
        [Fact]
        public void The_year_picks_between_a_remake_and_its_original()
        {
            var files = new[] { "/movies/Dune (1984).mkv", "/movies/Dune (2021) 2160p.mkv" };

            Assert.Equal("/movies/Dune (1984).mkv", MovieFileMatcher.FindBestMatch(files, "Dune", 1984));
            Assert.Equal("/movies/Dune (2021) 2160p.mkv", MovieFileMatcher.FindBestMatch(files, "Dune", 2021));
        }

        [Fact]
        public void A_file_naming_a_different_year_is_not_this_film()
        {
            var files = new[] { "/movies/Dune (1984).mkv" };

            Assert.Null(MovieFileMatcher.FindBestMatch(files, "Dune", 2021));
        }

        /// <summary>
        /// Without a year the two prints are indistinguishable, and picking whichever the
        /// filesystem happened to list first is how a coin flip gets described as a match.
        /// </summary>
        [Fact]
        public void Candidates_that_tie_with_nothing_to_separate_them_match_nothing()
        {
            var files = new[] { "/movies/Dune (1984).mkv", "/movies/Dune (2021).mkv" };

            Assert.Null(MovieFileMatcher.FindBestMatch(files, "Dune"));
        }

        [Fact]
        public void A_long_title_that_ties_is_no_more_of_an_answer_than_a_short_one()
        {
            var files = new[] { "/movies/The Movie (1999).mkv", "/movies/The Movie (2010).mkv" };

            Assert.Null(MovieFileMatcher.FindBestMatch(files, "The Movie"));
            Assert.Equal("/movies/The Movie (2010).mkv", MovieFileMatcher.FindBestMatch(files, "The Movie", 2010));
        }

        [Fact]
        public void The_same_path_listed_twice_is_not_a_tie()
        {
            var files = new[] { "/movies/The Movie (1999).mkv", "/movies/The Movie (1999).mkv" };

            Assert.Equal("/movies/The Movie (1999).mkv", MovieFileMatcher.FindBestMatch(files, "The Movie"));
        }

        [Fact]
        public void A_year_in_the_title_itself_is_not_read_as_the_release_year()
        {
            var files = new[] { "/movies/Blade Runner 2049 (2017) 2160p.mkv" };

            Assert.Equal(
                "/movies/Blade Runner 2049 (2017) 2160p.mkv",
                MovieFileMatcher.FindBestMatch(files, "Blade Runner 2049", 2017));
        }

        [Fact]
        public void Release_noise_between_the_title_and_the_year_does_not_hide_either()
        {
            var files = new[] { "/movies/the.matrix.1999.bluray.x264-GROUP.mkv" };

            Assert.Equal(
                "/movies/the.matrix.1999.bluray.x264-GROUP.mkv",
                MovieFileMatcher.FindBestMatch(files, "The Matrix", 1999));
        }

        [Fact]
        public void Punctuation_in_a_title_does_not_stop_it_matching()
        {
            var files = new[] { "/movies/Oceans Eleven (2001).mkv" };

            Assert.Equal("/movies/Oceans Eleven (2001).mkv", MovieFileMatcher.FindBestMatch(files, "Ocean's Eleven", 2001));
        }

        [Fact]
        public void A_sequel_is_not_offered_for_the_film_it_follows()
        {
            var files = new[] { "/movies/Blade Runner 2049 (2017).mkv" };

            Assert.Null(MovieFileMatcher.FindBestMatch(files, "Blade Runner", 1982));
        }
    }
}
