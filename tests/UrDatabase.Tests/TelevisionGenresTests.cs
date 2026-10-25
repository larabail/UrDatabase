using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// TMDB's television genres, folded onto the shelves the films already use. Without this a
    /// library grows a "Sci-Fi &amp; Fantasy" shelf of programmes beside a "Science Fiction" shelf
    /// of films, and neither is the whole answer to what somebody went looking for.
    /// </summary>
    public class TelevisionGenresTests : System.IDisposable
    {
        private readonly TempLog _log = new();

        public void Dispose() => _log.Dispose();

        [Theory]
        [InlineData("Sci-Fi & Fantasy", "Science Fiction, Fantasy")]
        [InlineData("Action & Adventure", "Action, Adventure")]
        [InlineData("War & Politics", "War, Politics")]
        [InlineData("sci-fi & fantasy", "Science Fiction, Fantasy")]
        public void A_compound_television_genre_lands_on_both_film_shelves(string tmdb, string shelves)
        {
            Assert.Equal(shelves, TelevisionGenres.Join(new[] { tmdb }));
        }

        [Theory]
        [InlineData("Drama")]
        [InlineData("Crime")]
        [InlineData("Comedy")]
        [InlineData("Kids")]
        [InlineData("Reality")]
        [InlineData("Soap")]
        public void A_plain_genre_passes_through_unchanged(string genre)
        {
            Assert.Equal(new[] { genre }, TelevisionGenres.Fold(genre));
        }

        /// <summary>TMDB adds genres rarely; two plain names beat a shelf nobody else is on.</summary>
        [Fact]
        public void An_unknown_compound_is_still_split_on_its_ampersand()
        {
            Assert.Equal(new[] { "Mystery", "Science Fiction" }, TelevisionGenres.Fold("Mystery & Sci-Fi"));
        }

        [Fact]
        public void A_shelf_named_twice_is_listed_once()
        {
            Assert.Equal(
                "Action, Adventure, Science Fiction, Fantasy",
                TelevisionGenres.Join(new[] { "Action & Adventure", "Action", "Sci-Fi & Fantasy", "Sci-Fi" }));
        }

        /// <summary>
        /// Null, not an empty string: a caller writes null as "leave the column alone", and an
        /// empty string would blank genres somebody already had.
        /// </summary>
        [Fact]
        public void Nothing_to_store_is_null()
        {
            Assert.Null(TelevisionGenres.Join(null));
            Assert.Null(TelevisionGenres.Join(new string?[] { null, " ", "" }));
            Assert.Empty(TelevisionGenres.Fold("  "));
        }
    }
}
