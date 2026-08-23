using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The facts row under the title on the details screen.
    ///
    /// The reason this is a service and not five conditionally visible panels in the view is the
    /// pair of ratings. "IMDb 8.3" and "Jellyfin 8.2" are different measurements of different
    /// populations, and this repository has already shipped one bug from labelling one service's
    /// number as another's — so the rule that each number is printed under the name of the
    /// service it came from is enforced somewhere it can be tested.
    /// </summary>
    public class DetailFactsTests
    {
        [Fact]
        public void The_two_ratings_are_never_presented_as_the_same_kind_of_number()
        {
            var facts = DetailFacts.For(new MovieDetailsVm
            {
                ImdbRating = 8.3,
                CommunityRating = 8.2
            });

            var imdb = facts.Single(f => f.Kind == DetailFactKind.Imdb);
            var jellyfin = facts.Single(f => f.Kind == DetailFactKind.Server && f.Label == "JELLYFIN");

            // Each is named for where it came from.
            Assert.Equal("IMDB", imdb.Label);
            Assert.Equal("8.3", imdb.Value);
            Assert.Equal("8.2", jellyfin.Value);

            // And they are inked differently, so two numbers a tenth apart cannot read as one
            // measurement quoted twice.
            Assert.NotEqual(imdb.Kind, jellyfin.Kind);
        }

        [Fact]
        public void A_rating_is_never_labelled_merely_Rating()
        {
            var facts = DetailFacts.For(new MovieDetailsVm { ImdbRating = 7.0, CommunityRating = 6.5 });

            Assert.DoesNotContain(facts, f => f.Label == "RATING");
        }

        [Fact]
        public void A_film_with_no_ratings_shows_no_rating_facts()
        {
            var facts = DetailFacts.For(new MovieDetailsVm { Year = 1968, Runtime = 149 });

            Assert.DoesNotContain(facts, f => f.Kind == DetailFactKind.Imdb);
            Assert.DoesNotContain(facts, f => f.Label == "JELLYFIN");
            Assert.Equal(new[] { "YEAR", "RUNTIME" }, facts.Select(f => f.Label));
        }

        [Fact]
        public void Ratings_are_printed_to_one_decimal_place()
        {
            var facts = DetailFacts.For(new MovieDetailsVm { ImdbRating = 8, CommunityRating = 7.25 });

            Assert.Equal("8.0", facts.Single(f => f.Label == "IMDB").Value);
            Assert.Equal("7.3", facts.Single(f => f.Label == "JELLYFIN").Value);
        }

        [Fact]
        public void The_runtime_carries_its_unit()
        {
            var facts = DetailFacts.For(new MovieDetailsVm { Runtime = 149 });

            Assert.Equal("149 min", facts.Single(f => f.Label == "RUNTIME").Value);
        }

        /// <summary>
        /// TMDB reports an unknown runtime as zero rather than as nothing, and "0 min" is a
        /// worse answer than no runtime at all.
        /// </summary>
        [Fact]
        public void A_zero_runtime_is_treated_as_no_runtime()
        {
            var facts = DetailFacts.For(new MovieDetailsVm { Year = 1968, Runtime = 0 });

            Assert.DoesNotContain(facts, f => f.Label == "RUNTIME");
        }

        [Fact]
        public void A_server_film_says_where_it_is()
        {
            var facts = DetailFacts.For(new MovieDetailsVm { IsRemote = true });

            var where = Assert.Single(facts);
            Assert.Equal("WHERE", where.Label);
            Assert.Equal("On the server", where.Value);
            Assert.Equal(DetailFactKind.Server, where.Kind);
        }

        [Fact]
        public void A_local_film_does_not_claim_to_be_anywhere_in_particular()
        {
            var facts = DetailFacts.For(new MovieDetailsVm { Year = 1995, IsRemote = false });

            Assert.DoesNotContain(facts, f => f.Label == "WHERE");
        }

        /// <summary>
        /// The card for a film in both places carries two badges. A details screen that mentioned
        /// neither would be the one place the app went quiet about it.
        /// </summary>
        [Fact]
        public void A_film_in_both_places_says_so_in_the_words_the_wall_uses()
        {
            var facts = DetailFacts.For(new MovieDetailsVm { Year = 1999, IsRemote = false, IsOnServer = true });

            var where = Assert.Single(facts, f => f.Label == "WHERE");
            Assert.Equal("Offline and on the server", where.Value);
            Assert.Equal(DetailFactKind.Server, where.Kind);
        }

        /// <summary>
        /// Which fact is last depends on which ones the film has, and a hairline hanging off the
        /// end of the row is the giveaway that nobody checked.
        /// </summary>
        [Fact]
        public void The_last_fact_never_draws_a_trailing_separator()
        {
            foreach (var vm in new[]
            {
                new MovieDetailsVm { Year = 1968 },
                new MovieDetailsVm { Year = 1968, Runtime = 149 },
                new MovieDetailsVm { Year = 1968, Runtime = 149, ImdbRating = 8.3 },
                new MovieDetailsVm { IsRemote = true, CommunityRating = 7.1 },
            })
            {
                var facts = DetailFacts.For(vm);

                Assert.False(facts[^1].ShowSeparator, "the last fact drew a separator");
                Assert.All(facts.Take(facts.Count - 1), f => Assert.True(f.ShowSeparator));
            }
        }

        [Fact]
        public void A_film_with_nothing_known_about_it_produces_an_empty_row_rather_than_throwing()
        {
            Assert.Empty(DetailFacts.For(new MovieDetailsVm()));
            Assert.Empty(DetailFacts.For((MovieDetailsVm?)null));
            Assert.Empty(DetailFacts.For((SeriesDetailsVm?)null));
        }

        /// <summary>
        /// The row reads left to right in the order a person asks: when, how long, how good,
        /// and only then where it is.
        /// </summary>
        [Fact]
        public void The_facts_come_in_a_fixed_order()
        {
            var facts = DetailFacts.For(new MovieDetailsVm
            {
                Year = 1968,
                Runtime = 149,
                ImdbRating = 8.3,
                CommunityRating = 8.2,
                IsRemote = true
            });

            Assert.Equal(
                new[] { "YEAR", "RUNTIME", "IMDB", "JELLYFIN", "WHERE" },
                facts.Select(f => f.Label));
        }
    }
}
