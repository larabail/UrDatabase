using System.Collections.Generic;
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

        // ---------- building the lines in the first place ----------
        //
        // The building used to sit in the main window's code-behind, out of reach of any test.
        // Correcting a wrong TMDB match needs the same lines built a second time, which is what
        // brought it in here beside the splitting.

        private static TmdbService.TmdbCredits Credits(
            IEnumerable<(string Name, string? Character)>? cast = null,
            IEnumerable<(string Name, string? Job)>? crew = null)
        {
            var credits = new TmdbService.TmdbCredits();

            foreach (var (name, character) in cast ?? new List<(string, string?)>())
                credits.Cast.Add(new TmdbService.TmdbCast { Name = name, Character = character });

            foreach (var (name, job) in crew ?? new List<(string, string?)>())
                credits.Crew.Add(new TmdbService.TmdbCrew { Name = name, Job = job });

            return credits;
        }

        [Fact]
        public void An_actor_is_built_with_the_part_they_played_and_without_it_when_tmdb_has_none()
        {
            var lines = CreditLine.Cast(Credits(cast: new[]
            {
                ("Edward Norton", (string?)"The Narrator"),
                ("Somebody Else", null)
            }));

            Assert.Equal(new[] { "Edward Norton (The Narrator)", "Somebody Else" }, lines);
        }

        [Fact]
        public void The_cast_is_cut_off_before_it_becomes_a_list_of_extras()
        {
            var cast = new List<(string, string?)>();
            for (var i = 0; i < 40; i++) cast.Add(($"Actor {i}", null));

            Assert.Equal(CreditLine.MaxCast, CreditLine.Cast(Credits(cast: cast)).Count);
        }

        [Fact]
        public void Directors_come_first_and_writers_are_found_under_every_job_that_names_one()
        {
            var lines = CreditLine.Crew(Credits(crew: new[]
            {
                ("A Writer", (string?)"Writer"),
                ("David Fincher", "Director"),
                ("A Screenwriter", "Screenplay Writer"),
                ("A Composer", "Original Music Composer"),
                ("Nobody", null)
            }));

            Assert.Equal(new[] { "Director: David Fincher", "Writer: A Writer", "Writer: A Screenwriter" }, lines);
        }

        [Fact]
        public void No_credits_at_all_is_an_empty_list_rather_than_a_failure()
        {
            Assert.Empty(CreditLine.Cast(null));
            Assert.Empty(CreditLine.Crew(null));
            Assert.Equal("", CreditLine.Genres(null));
        }

        [Fact]
        public void Genres_are_joined_for_display_and_blank_ones_dropped()
        {
            var details = new TmdbService.TmdbDetails
            {
                Genres = new List<TmdbService.TmdbGenre>
                {
                    new() { Name = "Drama" },
                    new() { Name = "" },
                    new() { Name = "Comedy" }
                }
            };

            Assert.Equal("Drama, Comedy", CreditLine.Genres(details));
        }

        /// <summary>
        /// What the details screen actually does with a built line. Building and splitting are
        /// each other's inverse here, and a change to one that quietly breaks the other would
        /// otherwise only show up as a blank name on screen.
        /// </summary>
        [Fact]
        public void A_built_cast_line_splits_back_into_the_halves_it_was_built_from()
        {
            var line = Assert.Single(CreditLine.Cast(Credits(cast: new[] { ("Keir Dullea", (string?)"Dave Bowman") })));
            var (name, character) = CreditLine.SplitCast(line);

            Assert.Equal("Keir Dullea", name);
            Assert.Equal("Dave Bowman", character);
        }

        [Fact]
        public void A_built_crew_line_splits_back_into_the_halves_it_was_built_from()
        {
            var line = Assert.Single(CreditLine.Crew(Credits(crew: new[] { ("Stanley Kubrick", (string?)"Director") })));
            var (job, name) = CreditLine.SplitCrew(line);

            Assert.Equal("Director", job);
            Assert.Equal("Stanley Kubrick", name);
        }
    }
}
