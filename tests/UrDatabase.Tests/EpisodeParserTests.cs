using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// What <see cref="EpisodeParser"/> makes of the shapes a real television library contains.
    ///
    /// Split into what it accepts, what it refuses, and — the section worth the most — the things
    /// that look like episodes and are not. A parser that files episodes correctly and also files
    /// every 1080p film as season nineteen hundred is a worse parser than the one that existed
    /// before it.
    /// </summary>
    public class EpisodeParserTests
    {
        // ---------------------------------------------------------------- accepts

        [Theory]
        [InlineData("The Sopranos.S01E02.mkv", "The Sopranos", 1, 2)]
        [InlineData("The.Sopranos.S01E02.mkv", "The Sopranos", 1, 2)]
        [InlineData("The Sopranos s1e2.mkv", "The Sopranos", 1, 2)]
        [InlineData("The Sopranos S01.E02.mkv", "The Sopranos", 1, 2)]
        [InlineData("The Sopranos - S01E02.mkv", "The Sopranos", 1, 2)]
        [InlineData("The Sopranos S12E34.mkv", "The Sopranos", 12, 34)]
        public void Reads_a_season_and_episode_marker(string name, string series, int season, int episode)
        {
            Assert.True(EpisodeParser.TryParse(name, out var parsed));

            Assert.Equal(series, parsed.SeriesTitle);
            Assert.Equal(season, parsed.SeasonNumber);
            Assert.Equal(episode, parsed.EpisodeNumber);
        }

        [Theory]
        [InlineData("The Sopranos.1x02.mkv", 1, 2)]
        [InlineData("The Sopranos 1x02.mkv", 1, 2)]
        [InlineData("The Sopranos.12x34.mkv", 12, 34)]
        public void Reads_a_cross_numbered_marker(string name, int season, int episode)
        {
            Assert.True(EpisodeParser.TryParse(name, out var parsed));

            Assert.Equal("The Sopranos", parsed.SeriesTitle);
            Assert.Equal(season, parsed.SeasonNumber);
            Assert.Equal(episode, parsed.EpisodeNumber);
        }

        [Fact]
        public void Reads_the_episode_title_from_behind_the_marker()
        {
            Assert.True(EpisodeParser.TryParse("The Sopranos.S01E02.46 Long.mkv", out var parsed));

            Assert.Equal("The Sopranos", parsed.SeriesTitle);
            Assert.Equal("46 Long", parsed.EpisodeTitle);
        }

        [Fact]
        public void Strips_release_noise_from_the_episode_title()
        {
            Assert.True(EpisodeParser.TryParse(
                "The Sopranos.S01E02.46 Long.1080p.BluRay.x264-GROUP.mkv", out var parsed));

            Assert.Equal("The Sopranos", parsed.SeriesTitle);
            Assert.Equal("46 Long", parsed.EpisodeTitle);
        }

        /// <summary>
        /// The reason this class takes a path. Nothing in the filename names the programme, so a
        /// parser reading only the file would have to refuse it — and this is one of the most
        /// common layouts there is.
        /// </summary>
        [Theory]
        [InlineData("/films/The Sopranos/Season 01/02 - 46 Long.mkv")]
        [InlineData("/films/The Sopranos/Season 1/02 - 46 Long.mkv")]
        [InlineData("/films/The Sopranos/season.01/02 - 46 Long.mkv")]
        [InlineData("/films/The Sopranos/S01/02 - 46 Long.mkv")]
        [InlineData(@"C:\films\The Sopranos\Season 01\02 - 46 Long.mkv")]
        public void Takes_the_season_from_a_directory_and_the_programme_from_above_it(string path)
        {
            Assert.True(EpisodeParser.TryParse(path, out var parsed));

            Assert.Equal("The Sopranos", parsed.SeriesTitle);
            Assert.Equal(1, parsed.SeasonNumber);
            Assert.Equal(2, parsed.EpisodeNumber);
            Assert.Equal("46 Long", parsed.EpisodeTitle);
        }

        [Fact]
        public void Reads_a_bare_episode_number_with_no_title_after_it()
        {
            Assert.True(EpisodeParser.TryParse("/films/The Sopranos/Season 01/02.mkv", out var parsed));

            Assert.Equal("The Sopranos", parsed.SeriesTitle);
            Assert.Equal(1, parsed.SeasonNumber);
            Assert.Equal(2, parsed.EpisodeNumber);
            Assert.Equal("", parsed.EpisodeTitle);
        }

        /// <summary>
        /// A marker in the filename outranks the directory it sits in. A file filed in the wrong
        /// season folder is commoner than one named with the wrong season.
        /// </summary>
        [Fact]
        public void Prefers_the_filename_marker_over_the_directory()
        {
            Assert.True(EpisodeParser.TryParse(
                "/films/The Sopranos/Season 01/The Sopranos.S02E05.mkv", out var parsed));

            Assert.Equal(2, parsed.SeasonNumber);
            Assert.Equal(5, parsed.EpisodeNumber);
        }

        /// <summary>
        /// The programme still has to come from the directory when the filename is only a marker.
        /// </summary>
        [Fact]
        public void Names_the_programme_from_the_directory_when_the_file_is_only_a_marker()
        {
            Assert.True(EpisodeParser.TryParse("/films/The Sopranos/Season 01/S01E02.mkv", out var parsed));

            Assert.Equal("The Sopranos", parsed.SeriesTitle);
            Assert.Equal(1, parsed.SeasonNumber);
            Assert.Equal(2, parsed.EpisodeNumber);
        }

        [Theory]
        [InlineData("/films/The Sopranos/Specials/02 - A Christmas Special.mkv")]
        [InlineData("/films/The Sopranos/Special/02 - A Christmas Special.mkv")]
        public void Reads_a_specials_directory_as_season_zero(string path)
        {
            Assert.True(EpisodeParser.TryParse(path, out var parsed));

            Assert.Equal("The Sopranos", parsed.SeriesTitle);
            Assert.Equal(SeriesGrouping.SpecialsSeasonNumber, parsed.SeasonNumber);
        }

        /// <summary>
        /// Season zero has to survive being written out as a number too, or a special named
        /// <c>S00E01</c> lands in a season of its own above season one.
        /// </summary>
        [Fact]
        public void Reads_an_explicit_season_zero_as_specials()
        {
            Assert.True(EpisodeParser.TryParse("The Sopranos.S00E01.mkv", out var parsed));

            Assert.Equal(SeriesGrouping.SpecialsSeasonNumber, parsed.SeasonNumber);
        }

        [Fact]
        public void Reads_a_year_off_the_programme_name()
        {
            Assert.True(EpisodeParser.TryParse("Doctor Who (2005).S01E02.mkv", out var parsed));

            Assert.Equal("Doctor Who", parsed.SeriesTitle);
            Assert.Equal(2005, parsed.SeriesYear);
        }

        [Fact]
        public void Reads_a_year_off_a_programme_directory()
        {
            Assert.True(EpisodeParser.TryParse(
                "/films/Doctor Who (2005)/Season 01/02 - The End of the World.mkv", out var parsed));

            Assert.Equal("Doctor Who", parsed.SeriesTitle);
            Assert.Equal(2005, parsed.SeriesYear);
        }

        [Fact]
        public void Leaves_the_year_null_when_the_programme_carries_none()
        {
            Assert.True(EpisodeParser.TryParse("The Sopranos.S01E02.mkv", out var parsed));

            Assert.Null(parsed.SeriesYear);
        }

        /// <summary>
        /// Documented in <see cref="EpisodeParser"/>: one file holding two episodes is a question
        /// about the catalogue's shape, not about parsing, and is not answered yet. Pinned so that
        /// answering it later is a deliberate act rather than an accident.
        /// </summary>
        [Fact]
        public void Reads_a_double_episode_as_its_first_episode()
        {
            Assert.True(EpisodeParser.TryParse("The Sopranos.S01E02E03.mkv", out var parsed));

            Assert.Equal(1, parsed.SeasonNumber);
            Assert.Equal(2, parsed.EpisodeNumber);
        }

        // ---------------------------------------------------------------- refuses

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Refuses_nothing_at_all(string? path)
        {
            Assert.False(EpisodeParser.TryParse(path, out _));
        }

        [Theory]
        [InlineData("The Matrix (1999).mkv")]
        [InlineData("the.matrix.1999.BluRay.x264-GROUP.mkv")]
        [InlineData("Blade Runner 2049 (2017).mkv")]
        [InlineData("/films/The Matrix (1999)/The Matrix.mkv")]
        public void Refuses_a_film(string path)
        {
            Assert.False(EpisodeParser.TryParse(path, out _));
        }

        /// <summary>
        /// A leading number is only an episode inside a season directory. Everywhere else it is
        /// very much likelier to be a film, and <c>1917.mkv</c> is the standing example.
        /// </summary>
        [Theory]
        [InlineData("/films/1917.mkv")]
        [InlineData("/films/The Sopranos/02 - 46 Long.mkv")]
        public void Refuses_a_leading_number_outside_a_season_directory(string path)
        {
            Assert.False(EpisodeParser.TryParse(path, out _));
        }

        /// <summary>
        /// A four-digit number in a season directory is a year, not an episode. Somebody who has
        /// filed a film under a season folder gets a film, which is what it is.
        /// </summary>
        [Fact]
        public void Refuses_a_four_digit_leading_number_even_in_a_season_directory()
        {
            Assert.False(EpisodeParser.TryParse("/films/Shows/Season 01/1917.mkv", out _));
        }

        /// <summary>
        /// Absolute numbering, deliberately out of scope: there is no season anywhere and a bare
        /// number beside a title is indistinguishable from a film without a catalogue to check.
        /// </summary>
        [Theory]
        [InlineData("/anime/Bleach/Bleach - 137.mkv")]
        [InlineData("/anime/Bleach/137 - The Man Who Risks His Life.mkv")]
        public void Refuses_absolute_numbering(string path)
        {
            Assert.False(EpisodeParser.TryParse(path, out _));
        }

        /// <summary>
        /// Date-based naming for a daily programme, also out of scope. It collides with
        /// <see cref="FilenameParser"/>'s year heuristic and the two cannot both be right.
        /// </summary>
        [Fact]
        public void Refuses_date_based_naming()
        {
            Assert.False(EpisodeParser.TryParse("The Daily Show.2024.03.11.mkv", out _));
        }

        /// <summary>
        /// Three-digit season-and-episode: <c>102</c> meaning season one, episode two. Refused
        /// because it cannot be told from an absolute number.
        /// </summary>
        [Fact]
        public void Refuses_three_digit_season_and_episode()
        {
            Assert.False(EpisodeParser.TryParse("/shows/Cheers/Cheers - 102 - Sam's Women.mkv", out _));
        }

        /// <summary>
        /// An episode that cannot name its programme is refused rather than filed under an
        /// invented name: <c>series.title</c> is NOT NULL, and a film card is a better failure
        /// than a shelf of episodes under a title this parser made up.
        /// </summary>
        [Theory]
        [InlineData("S01E02.mkv")]
        [InlineData("1x02.mkv")]
        [InlineData("/Season 01/02 - 46 Long.mkv")]
        public void Refuses_an_episode_whose_programme_cannot_be_named(string path)
        {
            Assert.False(EpisodeParser.TryParse(path, out _));
        }

        // ------------------------------------------------- things that look like episodes

        /// <summary>
        /// The trap that would have done the most damage. A resolution written with an <c>x</c> is
        /// the exact shape of a cross-numbered marker, and reading it would file a whole library
        /// under season nineteen hundred.
        /// </summary>
        [Theory]
        [InlineData("The Matrix 1920x1080.mkv")]
        [InlineData("The Matrix.1920x1080.BluRay.mkv")]
        [InlineData("The Matrix 1280x720.mkv")]
        public void Refuses_a_resolution_written_as_a_cross_numbered_marker(string name)
        {
            Assert.False(EpisodeParser.TryParse(name, out _));
        }

        /// <summary>
        /// A marker has to stand on its own. A word that merely contains an s, digits and an e is
        /// not a season — otherwise a programme with a codec or a group name in it starts parsing
        /// as television.
        /// </summary>
        [Theory]
        [InlineData("Words1e2.mkv")]
        [InlineData("The Matrix x265.mkv")]
        [InlineData("The Matrix h265.mkv")]
        public void Refuses_a_marker_buried_inside_a_word(string name)
        {
            Assert.False(EpisodeParser.TryParse(name, out _));
        }

        /// <summary>
        /// The lookahead earns its place here: without it this reads as episode two with a stray
        /// digit, and episode twenty would collide with episode two.
        /// </summary>
        [Fact]
        public void Does_not_truncate_a_long_episode_number()
        {
            Assert.True(EpisodeParser.TryParse("The Sopranos.S01E020.mkv", out var parsed));

            Assert.Equal(20, parsed.EpisodeNumber);
        }

        /// <summary>
        /// A programme whose own name ends in a number keeps it, because the rightmost marker is
        /// the real one.
        /// </summary>
        [Fact]
        public void Keeps_a_number_that_belongs_to_the_programme_name()
        {
            Assert.True(EpisodeParser.TryParse("Battlestar Galactica 1978.S01E02.mkv", out var parsed));

            Assert.Equal(1, parsed.SeasonNumber);
            Assert.Equal(2, parsed.EpisodeNumber);
            Assert.Equal("Battlestar Galactica", parsed.SeriesTitle);
            Assert.Equal(1978, parsed.SeriesYear);
        }

        /// <summary>
        /// A file that is all extension. <see cref="FilenameParser"/> keeps the name rather than
        /// returning nothing, and this must not fall over on the same input.
        /// </summary>
        [Fact]
        public void Survives_a_name_that_is_only_an_extension()
        {
            Assert.False(EpisodeParser.TryParse(".mkv", out _));
        }

        /// <summary>
        /// A season directory nested under another programme's directory must take the nearest
        /// programme, not the outermost one.
        /// </summary>
        [Fact]
        public void Takes_the_nearest_programme_directory_above_the_season()
        {
            Assert.True(EpisodeParser.TryParse(
                "/media/Television/The Sopranos/Season 01/02 - 46 Long.mkv", out var parsed));

            Assert.Equal("The Sopranos", parsed.SeriesTitle);
        }
    }
}
