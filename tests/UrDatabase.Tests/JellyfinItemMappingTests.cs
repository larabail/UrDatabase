using System;
using System.Collections.Generic;
using UrDatabase.Models;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Turning Jellyfin's wire shape into a film. This is where the app's promise that a server
    /// item needs no enrichment is either kept or quietly broken.
    /// </summary>
    public class JellyfinItemMappingTests
    {
        private static JellyfinItemDto Item() => new()
        {
            Id = "item-1",
            Name = "A Wholly Invented Film",
            ProductionYear = 1994,
            Genres = new List<string> { "Drama", "Crime" },
            Overview = "Nothing that happened to anybody.",
            RunTimeTicks = 56754979999,
            CommunityRating = 6.8,
            ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt0000001", ["Tmdb"] = "42" },
            ImageTags = new Dictionary<string, string> { ["Primary"] = "tag-1" }
        };

        [Fact]
        public void An_item_arrives_complete_and_needs_no_second_lookup()
        {
            var movie = Item().ToMovie();

            Assert.NotNull(movie);
            Assert.Equal("item-1", movie!.ItemId);
            Assert.Equal("A Wholly Invented Film", movie.Title);
            Assert.Equal(1994, movie.Year);
            Assert.Equal("Drama, Crime", movie.Genres);
            Assert.Equal("Nothing that happened to anybody.", movie.Overview);
            Assert.Equal(95, movie.RuntimeMinutes);
            Assert.Equal(6.8, movie.CommunityRating);
            Assert.Equal("tt0000001", movie.ImdbId);
            Assert.Equal("42", movie.TmdbId);
            Assert.Equal("tag-1", movie.ImageTag);
        }

        [Fact]
        public void The_title_is_taken_as_given_rather_than_parsed()
        {
            // Jellyfin has already identified the film. Running its curated title through the
            // filename parser would strip a legitimate year or bracket out of it.
            var item = Item();
            item.Name = "2001: A Space Fiction (1080p) [Director's Cut]";

            Assert.Equal("2001: A Space Fiction (1080p) [Director's Cut]", item.ToMovie()!.Title);
        }

        [Fact]
        public void An_item_with_no_id_is_dropped()
        {
            var item = Item();
            item.Id = "";

            Assert.Null(item.ToMovie());
        }

        [Fact]
        public void An_item_with_no_title_is_dropped()
        {
            var item = Item();
            item.Name = "   ";

            Assert.Null(item.ToMovie());
        }

        [Fact]
        public void A_missing_year_stays_missing_rather_than_becoming_zero()
        {
            var item = Item();
            item.ProductionYear = 0;

            Assert.Null(item.ToMovie()!.Year);
        }

        [Fact]
        public void An_item_with_nothing_optional_still_maps()
        {
            var movie = new JellyfinItemDto { Id = "bare", Name = "Bare Minimum" }.ToMovie();

            Assert.NotNull(movie);
            Assert.Equal("", movie!.Genres);
            Assert.Equal("", movie.Overview);
            Assert.Null(movie.RuntimeMinutes);
            Assert.Null(movie.ImdbId);
            Assert.Null(movie.ImageTag);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData(0L, null)]
        [InlineData(-1L, null)]
        [InlineData(600000000L, 1)]           // exactly one minute
        [InlineData(56754979999L, 95)]        // 94.6 minutes, rounded up
        [InlineData(1000000L, null)]          // a tenth of a second is not "0 min"
        public void Runtime_is_rounded_to_whole_minutes(long? ticks, int? expected)
        {
            Assert.Equal(expected, JellyfinItemDto.TicksToMinutes(ticks));
        }

        [Fact]
        public void Genres_arrive_as_a_list_and_stay_one()
        {
            // The reason a server library never piles into the "Uncategorised" bucket the way a
            // freshly scanned one does.
            Assert.Equal("Drama, Crime", JellyfinItemDto.JoinGenres(new[] { "Drama", "Crime" }));
            Assert.Equal("Drama", JellyfinItemDto.JoinGenres(new[] { " Drama ", "", "   " }));
            Assert.Equal("", JellyfinItemDto.JoinGenres(null));
        }

        [Fact]
        public void Provider_ids_are_matched_without_regard_to_case()
        {
            var item = Item();
            item.ProviderIds = new Dictionary<string, string> { ["imdb"] = "tt0000009" };

            Assert.Equal("tt0000009", item.ToMovie()!.ImdbId);
        }

        [Fact]
        public void An_empty_provider_id_is_treated_as_absent()
        {
            var item = Item();
            item.ProviderIds = new Dictionary<string, string> { ["Imdb"] = "   " };

            Assert.Null(item.ToMovie()!.ImdbId);
        }
    }
}
