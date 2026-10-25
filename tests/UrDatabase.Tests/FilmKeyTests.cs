using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class FilmKeyTests
    {
        [Fact]
        public void An_identified_film_is_keyed_on_its_tmdb_id()
        {
            Assert.Equal("tmdb:603", FilmKey.For(603, "The Matrix", 1999));
        }

        [Fact]
        public void An_unidentified_film_is_keyed_on_its_title_and_year()
        {
            Assert.Equal("title:the matrix|1999", FilmKey.For(null, "The Matrix", 1999));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-4)]
        public void A_tmdb_id_that_is_not_positive_is_no_id(int tmdbId)
        {
            Assert.Equal("title:ran|1985", FilmKey.For(tmdbId, "Ran", 1985));
        }

        [Fact]
        public void The_same_title_typed_differently_is_the_same_key()
        {
            Assert.Equal(
                FilmKey.ForTitle("  the   MATRIX ", 1999),
                FilmKey.ForTitle("The Matrix", 1999));
        }

        [Fact]
        public void The_year_tells_two_films_of_the_same_name_apart()
        {
            Assert.NotEqual(FilmKey.ForTitle("A Star Is Born", 1954), FilmKey.ForTitle("A Star Is Born", 2018));
        }

        [Fact]
        public void An_unknown_year_still_makes_a_key()
        {
            Assert.Equal("title:ran|", FilmKey.ForTitle("Ran", null));
            Assert.Equal("title:ran|", FilmKey.ForTitle("Ran", 0));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_film_with_no_title_and_no_id_has_no_key(string? title)
        {
            Assert.Null(FilmKey.For(null, title, 2000));
            Assert.False(new FilmIdentity(null, title, 2000).CanBeJournalled);
        }

        [Fact]
        public void An_identity_exposes_both_keys()
        {
            var film = new FilmIdentity(603, " The Matrix ", 1999);

            Assert.Equal("tmdb:603", film.Key);
            Assert.Equal("title:the matrix|1999", film.TitleKey);
            Assert.Equal("The Matrix", film.Title);
        }
    }
}
