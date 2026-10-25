using System;
using System.Collections.Generic;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class TmdbMatchTests
    {
        private static TmdbMatch.Candidate Result(int id, string? title, string? year = null, string? originalTitle = null) =>
            new()
            {
                Id = id,
                Title = title,
                OriginalTitle = originalTitle,
                ReleaseDate = year is null ? null : $"{year}-06-01",
                PosterPath = $"/{id}.jpg"
            };

        [Fact]
        public void The_reported_bug_a_longer_film_containing_the_title_is_refused()
        {
            // El Drama (2026) was catalogued from a file, TMDB's top hit was El Sabor del Drama,
            // and its poster was written to the catalogue as if it were this film's own.
            var results = new List<TmdbMatch.Candidate>
            {
                Result(900, "El Sabor del Drama", "2019")
            };

            Assert.Null(TmdbMatch.ChooseBest(results, "El Drama", 2026));
        }

        [Fact]
        public void A_title_that_agrees_is_chosen_over_a_more_popular_one_that_does_not()
        {
            var results = new List<TmdbMatch.Candidate>
            {
                Result(900, "El Sabor del Drama", "2019"),
                Result(901, "El Drama", "2026")
            };

            Assert.Equal(901, TmdbMatch.ChooseBest(results, "El Drama", 2026)!.Id);
        }

        [Fact]
        public void A_film_listed_under_a_translated_title_is_found_by_its_original_one()
        {
            var results = new List<TmdbMatch.Candidate>
            {
                Result(901, "The Drama", "2026", originalTitle: "El Drama")
            };

            Assert.Equal(901, TmdbMatch.ChooseBest(results, "El Drama", 2026)!.Id);
        }

        [Fact]
        public void Punctuation_accents_and_case_do_not_stop_a_match()
        {
            var results = new List<TmdbMatch.Candidate> { Result(1, "Am\u00e9lie", "2001") };

            Assert.NotNull(TmdbMatch.ChooseBest(results, "amelie", 2001));
            Assert.NotNull(TmdbMatch.ChooseBest(new List<TmdbMatch.Candidate> { Result(2, "Spider-Man: No Way Home", "2021") },
                "Spider Man No Way Home", 2021));
            Assert.NotNull(TmdbMatch.ChooseBest(new List<TmdbMatch.Candidate> { Result(3, "Ocean\u2019s Eleven", "2001") },
                "Oceans Eleven", 2001));
        }

        [Fact]
        public void A_remake_is_not_a_worse_answer_it_is_the_wrong_one()
        {
            var results = new List<TmdbMatch.Candidate> { Result(1, "Dune", "1984") };

            Assert.Null(TmdbMatch.ChooseBest(results, "Dune", 2021));
        }

        [Fact]
        public void A_year_one_out_still_matches_because_a_release_crosses_new_year()
        {
            var results = new List<TmdbMatch.Candidate> { Result(1, "The Brutalist", "2024") };

            Assert.Equal(1, TmdbMatch.ChooseBest(results, "The Brutalist", 2025)!.Id);
        }

        [Fact]
        public void The_exact_year_beats_the_near_one_whatever_order_they_arrive_in()
        {
            var nearFirst = new List<TmdbMatch.Candidate> { Result(1, "Dune", "2020"), Result(2, "Dune", "2021") };
            var exactFirst = new List<TmdbMatch.Candidate> { Result(2, "Dune", "2021"), Result(1, "Dune", "2020") };

            Assert.Equal(2, TmdbMatch.ChooseBest(nearFirst, "Dune", 2021)!.Id);
            Assert.Equal(2, TmdbMatch.ChooseBest(exactFirst, "Dune", 2021)!.Id);
        }

        [Fact]
        public void A_title_alone_is_enough_when_the_catalogue_has_no_year()
        {
            var results = new List<TmdbMatch.Candidate> { Result(1, "Fight Club", "1999") };

            Assert.Equal(1, TmdbMatch.ChooseBest(results, "Fight Club", null)!.Id);
        }

        [Fact]
        public void A_missing_year_does_not_choose_between_a_film_and_its_remake()
        {
            var results = new[] { Result(1, "Dune", "2021"), Result(2, "Dune", "1984") };
            Assert.Null(TmdbMatch.ChooseBest(results, "Dune", null));
        }

        [Theory]
        [InlineData("Matrix, The", "The Matrix")]
        [InlineData("S W A T", "SWAT")]
        [InlineData("WALL E", "WALL·E")]
        public void Release_title_variations_match_with_a_corroborating_year(string requested, string actual)
        {
            Assert.Equal(1, TmdbMatch.ChooseBest(new[] { Result(1, actual, "1999") }, requested, 1999)!.Id);
        }

        [Fact]
        public void Whitespace_compaction_needs_a_corroborating_year()
        {
            Assert.Null(TmdbMatch.ChooseBest(new[] { Result(1, "The Rapist", "1999") }, "Therapist", null));
            Assert.Null(TmdbMatch.ChooseBest(new[] { Result(1, "SWAT", "2003") }, "S W A T", 2011));
        }

        [Fact]
        public void A_result_tmdb_cannot_date_is_accepted_on_its_title()
        {
            var results = new List<TmdbMatch.Candidate> { Result(1, "Fight Club"), Result(2, "Fight Club", "1999") };

            // Undated is accepted, but the dated one that agrees wins over it.
            Assert.Equal(2, TmdbMatch.ChooseBest(results, "Fight Club", 1999)!.Id);
            Assert.Equal(1, TmdbMatch.ChooseBest(new List<TmdbMatch.Candidate> { Result(1, "Fight Club") }, "Fight Club", 1999)!.Id);
        }

        [Fact]
        public void An_empty_release_date_is_read_as_no_date_rather_than_throwing()
        {
            var results = new List<TmdbMatch.Candidate>
            {
                new() { Id = 1, Title = "Fight Club", ReleaseDate = "" }
            };

            Assert.Equal(1, TmdbMatch.ChooseBest(results, "Fight Club", 1999)!.Id);
            Assert.Null(TmdbMatch.ParseYear(""));
            Assert.Null(TmdbMatch.ParseYear("not-a-date"));
            Assert.Null(TmdbMatch.ParseYear("12"));
            Assert.Equal(1999, TmdbMatch.ParseYear("1999-10-15"));
        }

        [Fact]
        public void The_first_of_two_records_of_one_film_is_taken_rather_than_neither()
        {
            // Unlike MovieFileMatcher, a tie here is one film listed twice: both have the same
            // normalised title and the same year. TMDB orders by popularity, and the user can now
            // change it anyway.
            var results = new List<TmdbMatch.Candidate> { Result(1, "Fight Club", "1999"), Result(2, "Fight Club", "1999") };

            Assert.Equal(1, TmdbMatch.ChooseBest(results, "Fight Club", 1999)!.Id);
        }

        [Fact]
        public void Nothing_to_search_and_nothing_to_search_for_are_both_answered_with_null()
        {
            Assert.Null(TmdbMatch.ChooseBest(null, "Fight Club", 1999));
            Assert.Null(TmdbMatch.ChooseBest(Array.Empty<TmdbMatch.Candidate>(), "Fight Club", 1999));
            Assert.Null(TmdbMatch.ChooseBest(new List<TmdbMatch.Candidate> { Result(1, "Fight Club", "1999") }, "", 1999));
            Assert.Null(TmdbMatch.ChooseBest(new List<TmdbMatch.Candidate> { Result(1, "Fight Club", "1999") }, "   ", 1999));
        }

        [Theory]
        // A subtitle the file name left off.
        [InlineData("Borat", "Borat: Cultural Learnings of America for Make Benefit Glorious Nation of Kazakhstan", null, "2006", 2006)]
        // Translated, sharing only the proper nouns.
        [InlineData("Harry Potter y la piedra filosofal", "Harry Potter and the Philosopher's Stone", null, "2001", 2001)]
        // Translated, sharing nothing at all: TMDB found it under a name it did not send back.
        [InlineData("La vida de los otros", "The Lives of Others", "Das Leben der Anderen", "2006", 2006)]
        // A leading article TMDB does not use, and a release that crossed new year.
        [InlineData("The Shawshank Redemption", "Shawshank Redemption", null, "1994", 1995)]
        public void The_only_result_of_a_year_scoped_search_is_accepted(
            string catalogued, string title, string? originalTitle, string resultYear, int year)
        {
            var results = new[] { Result(7, title, resultYear, originalTitle) };

            Assert.Null(TmdbMatch.ChooseBest(results, catalogued, year));
            Assert.Equal(7, TmdbMatch.ChooseSoleResult(results, catalogued, year)!.Id);
        }

        [Fact]
        public void A_sole_result_that_merely_contains_a_short_title_is_still_refused()
        {
            // The original bug, in exactly the shape the sole-result rule would otherwise accept.
            Assert.Null(TmdbMatch.ChooseSoleResult(new[] { Result(900, "El Sabor del Drama", "2026") }, "El Drama", 2026));
            Assert.Null(TmdbMatch.ChooseSoleResult(new[] { Result(900, "Sabor de Drama", "2026") }, "El Drama", 2026));
            Assert.Null(TmdbMatch.ChooseSoleResult(new[] { Result(901, "Mission: Impossible - Dead Reckoning", "2023") },
                "Dead Reckoning", 2023));
        }

        [Fact]
        public void A_sole_result_needs_a_year_on_both_sides_that_agrees()
        {
            var results = new[] { Result(7, "The Lives of Others", "2006") };

            Assert.Null(TmdbMatch.ChooseSoleResult(results, "La vida de los otros", null));
            Assert.Null(TmdbMatch.ChooseSoleResult(results, "La vida de los otros", 2008));
            Assert.Null(TmdbMatch.ChooseSoleResult(new[] { Result(7, "The Lives of Others") }, "La vida de los otros", 2006));
        }

        [Fact]
        public void Two_results_are_a_choice_for_a_person_not_a_sole_result()
        {
            var results = new[] { Result(7, "The Lives of Others", "2006"), Result(8, "Other Lives", "2006") };

            Assert.Null(TmdbMatch.ChooseSoleResult(results, "La vida de los otros", 2006));
            Assert.Null(TmdbMatch.ChooseSoleResult(Array.Empty<TmdbMatch.Candidate>(), "La vida de los otros", 2006));
            Assert.Null(TmdbMatch.ChooseSoleResult(null, "La vida de los otros", 2006));
            Assert.Null(TmdbMatch.ChooseSoleResult(new[] { Result(0, "The Lives of Others", "2006") }, "La vida de los otros", 2006));
        }

        [Fact]
        public void A_sole_result_is_refused_when_the_title_names_a_film_from_another_year()
        {
            // Dune catalogued with the wrong year: that year's only hit is some other film, and
            // the film the file is really called was seen in the broader search.
            var scoped = new[] { Result(7, "Dune Drifter", "2020") };
            var seen = new[] { Result(7, "Dune Drifter", "2020"), Result(438631, "Dune", "2021") };

            Assert.NotNull(TmdbMatch.ChooseSoleResult(scoped, "Dune", 2020));
            Assert.Null(TmdbMatch.ChooseSoleResult(scoped, "Dune", 2020, seen));
        }

        [Fact]
        public void Every_searched_spelling_must_admit_the_sole_result()
        {
            var sabor = new[] { Result(900, "El Sabor del Drama", "2026") };
            Assert.NotNull(TmdbMatch.ChooseSoleResult(sabor, "El Drama SPANISH", 2026));
            Assert.Null(TmdbMatch.ChooseSoleResult(sabor, new[] { "El Drama SPANISH", "El Drama" }, 2026));

            var drifter = new[] { Result(7, "Dune Drifter", "2018") };
            var seen = new[] { Result(438631, "Dune", "2021"), Result(7, "Dune Drifter", "2018") };
            Assert.NotNull(TmdbMatch.ChooseSoleResult(drifter, "Dune SPANISH", 2018, seen));
            Assert.Null(TmdbMatch.ChooseSoleResult(drifter, new[] { "Dune SPANISH", "Dune" }, 2018, seen));

            // Two spellings that each found the same film once still found one film.
            var lives = new[] { Result(582, "The Lives of Others", "2006"), Result(582, "The Lives of Others", "2006") };
            Assert.Equal(582, TmdbMatch.ChooseSoleResult(lives, new[] { "La vida de los otros 1080p", "La vida de los otros" }, 2006)!.Id);
        }

        [Fact]
        public void A_result_with_no_title_at_all_cannot_be_matched()
        {
            var results = new List<TmdbMatch.Candidate> { new() { Id = 1, ReleaseDate = "1999-01-01" } };

            Assert.Null(TmdbMatch.ChooseBest(results, "Fight Club", 1999));
        }
    }
}
