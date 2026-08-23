using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Deciding which of the nominations filed under a title belong to the film on screen.
    ///
    /// The archive is searched by name, and a name is not a film. There are four called
    /// "A Star Is Born" and three of them were nominated; putting 1937's awards on 2018's copy
    /// would be worse than showing none, because it is wrong in a way that looks authoritative.
    /// </summary>
    public class OscarMatchTests
    {
        private static OscarNomination Nomination(int ceremony, string category, bool won = false,
            string nominee = "The Film", string detail = "") => new()
        {
            Ceremony = ceremony,
            Category = category,
            Nominee = nominee,
            Detail = detail,
            Won = won
        };

        [Fact]
        public void The_ceremony_after_the_release_year_is_the_normal_case()
        {
            var honours = OscarMatch.For(new[] { Nomination(2026, "Best Picture") }, 2025);

            Assert.True(honours.Any);
            Assert.Equal(2026, honours.Ceremony);
        }

        [Fact]
        public void Another_films_awards_under_the_same_title_are_not_attributed()
        {
            var candidates = new[]
            {
                Nomination(1938, "Best Writing, Original Story", won: true),
                Nomination(2019, "Best Original Song", won: true)
            };

            var honours = OscarMatch.For(candidates, 2018);

            Assert.Equal(1, honours.Total);
            Assert.Equal(2019, honours.Ceremony);
        }

        /// <summary>
        /// The international feature award runs behind, because a country submits a film after its
        /// own release. Two and three year gaps are ordinary there.
        /// </summary>
        [Fact]
        public void A_late_ceremony_within_three_years_still_counts()
        {
            var honours = OscarMatch.For(new[] { Nomination(2023, "Best International Feature Film") }, 2020);

            Assert.True(honours.Any);
        }

        [Fact]
        public void A_ceremony_more_than_three_years_later_is_a_different_film()
        {
            Assert.False(OscarMatch.For(new[] { Nomination(2024, "Best Picture") }, 2020).Any);
        }

        /// <summary>
        /// The early ceremonies did not follow the rule — the first covered 1927 and 1928 and was
        /// held in 1929 — so a ceremony in the film's own year has to be allowed.
        /// </summary>
        [Fact]
        public void A_ceremony_in_the_films_own_year_is_allowed()
        {
            Assert.True(OscarMatch.For(new[] { Nomination(1929, "Best Actor in a Leading Role") }, 1929).Any);
        }

        [Fact]
        public void A_ceremony_before_the_film_came_out_is_never_attributed_to_it()
        {
            Assert.False(OscarMatch.For(new[] { Nomination(1975, "Best Picture") }, 1976).Any);
        }

        [Fact]
        public void With_no_release_year_one_films_worth_of_ceremonies_is_accepted()
        {
            var candidates = new[]
            {
                Nomination(2026, "Best Picture"),
                Nomination(2026, "Best Sound", won: true)
            };

            Assert.Equal(2, OscarMatch.For(candidates, null).Total);
        }

        [Fact]
        public void With_no_release_year_two_films_worth_is_rejected_rather_than_guessed_at()
        {
            var candidates = new[]
            {
                Nomination(1938, "Best Writing, Original Story"),
                Nomination(2019, "Best Original Song")
            };

            Assert.False(OscarMatch.For(candidates, null).Any);
        }

        [Fact]
        public void Nothing_found_is_no_awards_rather_than_a_failure()
        {
            Assert.False(OscarMatch.For(null, 2020).Any);
            Assert.False(OscarMatch.For(new List<OscarNomination>(), 2020).Any);
            Assert.Same(OscarHonours.None, OscarMatch.For(null, 2020));
        }

        [Fact]
        public void Wins_and_nominations_are_counted_separately()
        {
            var honours = OscarMatch.For(
                new[]
                {
                    Nomination(2026, "Best Picture"),
                    Nomination(2026, "Best Sound", won: true),
                    Nomination(2026, "Best Film Editing")
                },
                2025);

            Assert.Equal(1, honours.Wins);
            Assert.Equal(3, honours.Total);
            Assert.Equal("1 win · 3 nominations", OscarMatch.Summary(honours));
        }

        [Fact]
        public void A_film_that_only_competed_says_so_without_mentioning_wins()
        {
            var honours = OscarMatch.For(new[] { Nomination(2026, "Best Picture") }, 2025);

            Assert.Equal("1 nomination", OscarMatch.Summary(honours));
        }

        [Fact]
        public void The_summary_of_nothing_is_nothing()
        {
            Assert.Equal("", OscarMatch.Summary(OscarHonours.None));
            Assert.Equal("", OscarMatch.Summary(null));
        }

        [Fact]
        public void A_ceremony_is_only_named_when_every_award_came_from_one()
        {
            var single = OscarMatch.For(new[] { Nomination(2026, "Best Picture") }, 2025);
            var split = OscarMatch.For(
                new[] { Nomination(2025, "Best Picture"), Nomination(2026, "Best Sound") },
                2024);

            Assert.Equal(2026, single.Ceremony);
            Assert.Null(split.Ceremony);
        }

        /// <summary>
        /// The film's own name is the largest thing on the screen. Repeating it on nine
        /// consecutive rows leaves no room for the names that are actually new information.
        /// </summary>
        [Fact]
        public void The_films_own_title_is_not_repeated_on_every_row()
        {
            var bestPicture = Nomination(2026, "Best Picture", nominee: "F1", detail: "Brad Pitt, Jerry Bruckheimer");
            var editing = Nomination(2026, "Best Film Editing", nominee: "F1", detail: "Stephen Mirrione");
            var acting = Nomination(2026, "Best Actor", nominee: "Michael B. Jordan", detail: "Sinners");

            Assert.Equal("Brad Pitt, Jerry Bruckheimer", OscarMatch.Line(bestPicture, "F1"));
            Assert.Equal("Stephen Mirrione", OscarMatch.Line(editing, "F1"));
            Assert.Equal("Michael B. Jordan", OscarMatch.Line(acting, "Sinners"));
        }

        /// <summary>
        /// The ordering is the part that matters. Sinners took ten nominations at the 2026
        /// ceremony and won three; a list cut at seven in the archive's own order can drop a win
        /// and leave the panel claiming the film merely competed.
        /// </summary>
        [Fact]
        public void A_truncated_list_never_drops_a_win()
        {
            var nominations = new List<OscarNomination>();
            for (var i = 0; i < 9; i++) nominations.Add(Nomination(2026, $"Category {i}"));
            nominations.Add(Nomination(2026, "Best Cinematography", won: true));

            var honours = OscarMatch.For(nominations, 2025);
            var rows = OscarMatch.Rows(honours, "Sinners");

            Assert.Equal(OscarMatch.MaxRows, rows.Count);
            Assert.True(rows[0].Won);
            Assert.Equal("and 3 more nominations", OscarMatch.MoreNotice(honours));
        }

        [Fact]
        public void A_list_that_fits_says_nothing_about_more()
        {
            var honours = OscarMatch.For(new[] { Nomination(2026, "Best Picture") }, 2025);

            Assert.Equal("", OscarMatch.MoreNotice(honours));
            Assert.Single(OscarMatch.Rows(honours, "F1"));
        }

        /// <summary>
        /// The Academy's long forms are house style rather than information, and they wrap to
        /// three lines each in a 250 pixel column. Shortening must not change which award it is.
        /// </summary>
        [Theory]
        [InlineData("Best Achievement in Film Editing", "Best Film Editing")]
        [InlineData("Best Performance by an Actor in a Leading Role", "Best Actor in a Leading Role")]
        [InlineData("Best Performance by an Actress in a Supporting Role", "Best Actress in a Supporting Role")]
        [InlineData("Best Music Written for Motion Pictures (Original Song)", "Best Original Song")]
        [InlineData("Best Music Written for Motion Pictures (Original Score)", "Best Original Score")]
        [InlineData("Best Motion Picture of the Year", "Best Picture")]
        [InlineData("Best Foreign Language Film of the Year", "Best International Feature")]
        [InlineData("Best Picture", "Best Picture")]
        [InlineData("Best Original Screenplay", "Best Original Screenplay")]
        [InlineData("Best Writing, Original Story", "Best Writing, Original Story")]
        public void Category_names_are_shortened_without_being_changed(string full, string expected)
        {
            Assert.Equal(expected, OscarMatch.Shorten(full));
        }

        [Fact]
        public void Shortening_never_invents_a_category_out_of_nothing()
        {
            Assert.Equal("", OscarMatch.Shorten(null));
            Assert.Equal("", OscarMatch.Shorten("   "));
        }

        [Fact]
        public void A_row_carries_the_glyph_that_marks_a_win()
        {
            var honours = OscarMatch.For(
                new[] { Nomination(2026, "Best Sound", won: true), Nomination(2026, "Best Picture") },
                2025);

            var rows = OscarMatch.Rows(honours, "F1");

            Assert.Equal("★", rows.First(r => r.Won).Mark);
            Assert.Equal("·", rows.First(r => !r.Won).Mark);
        }
    }
}
