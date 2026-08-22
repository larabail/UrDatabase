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
    }
}
