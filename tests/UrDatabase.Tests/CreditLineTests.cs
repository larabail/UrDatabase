using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The details screen sets a cast member's name over their character, and a crew job as a
    /// small label before the name. Both arrive as one string apiece, so both have to be taken
    /// apart again — and the interesting cases are the ones where the punctuation being split on
    /// also appears inside the value.
    /// </summary>
    public class CreditLineTests
    {
        [Fact]
        public void A_cast_line_splits_into_the_actor_and_the_part()
        {
            var (name, character) = CreditLine.SplitCast("Keir Dullea (Dave Bowman)");

            Assert.Equal("Keir Dullea", name);
            Assert.Equal("Dave Bowman", character);
        }

        /// <summary>
        /// TMDB leaves the character off an uncredited part, and the app formats that as a bare
        /// name. Splitting it must not produce a role with nobody playing it.
        /// </summary>
        [Fact]
        public void A_cast_line_with_no_part_is_all_name()
        {
            var (name, character) = CreditLine.SplitCast("Douglas Rain");

            Assert.Equal("Douglas Rain", name);
            Assert.Equal("", character);
        }

        /// <summary>
        /// Searching for the bracket from the right splits this one after "voice", which is why
        /// the search runs from the left.
        /// </summary>
        [Fact]
        public void A_part_that_contains_brackets_is_kept_whole()
        {
            var (name, character) = CreditLine.SplitCast("Douglas Rain (HAL 9000 (voice))");

            Assert.Equal("Douglas Rain", name);
            Assert.Equal("HAL 9000 (voice)", character);
        }

        [Fact]
        public void A_line_that_is_only_a_bracketed_part_is_not_split_into_a_nameless_credit()
        {
            var (name, character) = CreditLine.SplitCast("(uncredited)");

            Assert.Equal("(uncredited)", name);
            Assert.Equal("", character);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void An_empty_cast_line_produces_two_empty_halves(string? line)
        {
            var (name, character) = CreditLine.SplitCast(line);

            Assert.Equal("", name);
            Assert.Equal("", character);
        }

        [Fact]
        public void A_crew_line_splits_into_the_job_and_the_person()
        {
            var (job, name) = CreditLine.SplitCrew("Director: Stanley Kubrick");

            Assert.Equal("Director", job);
            Assert.Equal("Stanley Kubrick", name);
        }

        /// <summary>
        /// A name can contain a colon — "Writer: Smith: A Life" is contrived, but a title-cased
        /// job followed by a colon inside the name is not. Only the first colon separates.
        /// </summary>
        [Fact]
        public void Only_the_first_colon_separates_the_job_from_the_name()
        {
            var (job, name) = CreditLine.SplitCrew("Writer: Kubrick: and Clarke");

            Assert.Equal("Writer", job);
            Assert.Equal("Kubrick: and Clarke", name);
        }

        [Fact]
        public void A_crew_line_with_no_job_is_all_name()
        {
            var (job, name) = CreditLine.SplitCrew("Stanley Kubrick");

            Assert.Equal("", job);
            Assert.Equal("Stanley Kubrick", name);
        }

        [Fact]
        public void A_crew_line_with_a_job_and_no_name_is_left_alone()
        {
            var (job, name) = CreditLine.SplitCrew("Director:");

            Assert.Equal("", job);
            Assert.Equal("Director:", name);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void An_empty_crew_line_produces_two_empty_halves(string? line)
        {
            var (job, name) = CreditLine.SplitCrew(line);

            Assert.Equal("", job);
            Assert.Equal("", name);
        }
    }
}
