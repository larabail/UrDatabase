using System;
using System.Globalization;
using UrDatabase.Converters;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class UiMovieTests
    {
        [Theory]
        [InlineData("Drama, Thriller", new[] { "Drama", "Thriller" })]
        [InlineData("Drama|Thriller", new[] { "Drama", "Thriller" })]
        [InlineData(" Drama , Thriller ", new[] { "Drama", "Thriller" })]
        [InlineData("", new string[0])]
        [InlineData(null, new string[0])]
        public void Splits_genres_on_either_separator(string? genres, string[] expected)
        {
            var movie = new UiMovie { Genres = genres };

            Assert.Equal(expected, movie.GenresList);
        }

        [Fact]
        public void Genre_matching_ignores_case()
        {
            var movie = new UiMovie { Genres = "Science Fiction, Drama" };

            Assert.True(movie.HasGenre("drama"));
            Assert.True(movie.HasGenre("SCIENCE FICTION"));
            Assert.False(movie.HasGenre("Comedy"));
            Assert.False(movie.HasGenre(""));
        }

        [Fact]
        public void Changing_the_poster_path_raises_property_changed()
        {
            // The poster loader assigns this after the UI is already bound.
            var movie = new UiMovie();
            var raised = 0;
            movie.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(UiMovie.PosterPath)) raised++; };

            movie.PosterPath = "https://image.tmdb.org/t/p/w342/a.jpg";
            movie.PosterPath = "https://image.tmdb.org/t/p/w342/a.jpg"; // unchanged, no event

            Assert.Equal(1, raised);
        }
    }

    public class EqualsConverterTests
    {
        [Fact]
        public void Reports_whether_the_value_matches_the_parameter()
        {
            var converter = new EqualsConverter();

            Assert.True((bool)converter.Convert("Drama", typeof(bool), "Drama", CultureInfo.InvariantCulture)!);
            Assert.False((bool)converter.Convert("Drama", typeof(bool), "Comedy", CultureInfo.InvariantCulture)!);
            Assert.False((bool)converter.Convert(null, typeof(bool), "Comedy", CultureInfo.InvariantCulture)!);
            Assert.True((bool)converter.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture)!);
        }

        [Fact]
        public void Converts_back_to_the_parameter_only_when_true()
        {
            var converter = new EqualsConverter();

            Assert.Equal("Drama", converter.ConvertBack(true, typeof(string), "Drama", CultureInfo.InvariantCulture));
            Assert.Equal(Avalonia.Data.BindingOperations.DoNothing, converter.ConvertBack(false, typeof(string), "Drama", CultureInfo.InvariantCulture));
        }
    }

    public class FileLauncherTests
    {
        [Fact]
        public void Uses_the_platform_opener_rather_than_shell_execute_on_unix()
        {
            var psi = FileLauncher.BuildStartInfo("/movies/The Movie.mkv");

            if (OperatingSystem.IsMacOS())
            {
                // UseShellExecute cannot open documents on macOS; `open` can.
                Assert.Equal("open", psi.FileName);
                Assert.Equal("/movies/The Movie.mkv", Assert.Single(psi.ArgumentList));
                Assert.False(psi.UseShellExecute);
            }
            else if (OperatingSystem.IsWindows())
            {
                Assert.Equal("/movies/The Movie.mkv", psi.FileName);
                Assert.True(psi.UseShellExecute);
            }
            else
            {
                Assert.Equal("xdg-open", psi.FileName);
                Assert.Equal("/movies/The Movie.mkv", Assert.Single(psi.ArgumentList));
            }
        }

        [Fact]
        public void Paths_with_spaces_are_passed_as_a_single_argument()
        {
            var psi = FileLauncher.BuildStartInfo("/movies/A Very Long Title (1999).mkv");

            if (!OperatingSystem.IsWindows())
                Assert.Equal("/movies/A Very Long Title (1999).mkv", Assert.Single(psi.ArgumentList));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Rejects_a_blank_path(string path)
        {
            Assert.Throws<ArgumentException>(() => FileLauncher.BuildStartInfo(path));
        }

        [Fact]
        public void Opens_a_web_address_through_the_platform_opener_too()
        {
            var psi = FileLauncher.BuildUrlStartInfo("https://urdatabase-downloads.web.app");

            if (OperatingSystem.IsMacOS())
            {
                Assert.Equal("open", psi.FileName);
                Assert.Equal("https://urdatabase-downloads.web.app", Assert.Single(psi.ArgumentList));
            }
            else if (OperatingSystem.IsWindows())
            {
                // The shell is what knows which browser is the default one.
                Assert.Equal("https://urdatabase-downloads.web.app", psi.FileName);
                Assert.True(psi.UseShellExecute);
            }
            else
            {
                Assert.Equal("xdg-open", psi.FileName);
            }
        }

        [Theory]
        [InlineData("https://github.com/larabail/UrDatabase/releases")]
        [InlineData("http://example.test/page")]
        public void Http_and_https_are_the_only_schemes_that_may_be_opened(string url)
        {
            Assert.True(FileLauncher.IsWebUrl(url));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("github.com/larabail/UrDatabase")]
        [InlineData("javascript:alert(1)")]
        [InlineData("file:///etc/passwd")]
        [InlineData("ms-settings:privacy")]
        public void Anything_else_is_refused_rather_than_handed_to_the_machine(string? url)
        {
            // Every address the app opens comes out of a GitHub API response, and the launcher
            // runs whatever a scheme happens to be registered to.
            Assert.False(FileLauncher.IsWebUrl(url));
            Assert.Throws<ArgumentException>(() => FileLauncher.BuildUrlStartInfo(url!));
        }
    }
}
