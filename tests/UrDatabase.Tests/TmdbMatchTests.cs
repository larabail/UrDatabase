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

        [Fact]
        public void A_result_with_no_title_at_all_cannot_be_matched()
        {
            var results = new List<TmdbMatch.Candidate> { new() { Id = 1, ReleaseDate = "1999-01-01" } };

            Assert.Null(TmdbMatch.ChooseBest(results, "Fight Club", 1999));
        }
    }
}
