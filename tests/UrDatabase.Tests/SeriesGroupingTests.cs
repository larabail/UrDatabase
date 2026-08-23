using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Turning a server's seasons and episodes into a list somebody can read.
    ///
    /// Every test here is an edge case a real server produces: specials with no number, a season
    /// the server declined to enumerate, an episode whose season id points at nothing. Each of
    /// them is a way for an episode list to come out empty, and none of them could be asserted on
    /// while this logic was inside a view.
    /// </summary>
    public class SeriesGroupingTests
    {
        private static JellyfinSeason Season(string id, int? number, string? name = null) => new()
        {
            ItemId = id,
            SeriesId = "series1",
            Name = name ?? (number is int n ? $"Season {n}" : "Season"),
            Number = number
        };

        private static JellyfinEpisode Episode(
            string id,
            int? season,
            int? number,
            string? seasonId = null,
            string name = "An Invented Episode") => new()
        {
            ItemId = id,
            SeriesId = "series1",
            SeasonId = seasonId ?? "",
            Name = name,
            SeasonNumber = season,
            Number = number
        };

        [Fact]
        public void Episodes_are_filed_under_the_season_they_belong_to()
        {
            var groups = SeriesGrouping.Group(
                new[] { Season("s1", 1), Season("s2", 2) },
                new[]
                {
                    Episode("e1", 1, 1, "s1"),
                    Episode("e2", 1, 2, "s1"),
                    Episode("e3", 2, 1, "s2")
                });

            Assert.Equal(2, groups.Count);
            Assert.Equal("Season 1", groups[0].Name);
            Assert.Equal(2, groups[0].Episodes.Count);
            Assert.Single(groups[1].Episodes);
        }

        [Fact]
        public void An_episode_that_knows_only_its_season_number_still_finds_its_season()
        {
            // Some servers send no SeasonId at all. Matching on a single key is how half a
            // programme's episodes go missing.
            var groups = SeriesGrouping.Group(
                new[] { Season("s1", 1) },
                new[] { Episode("e1", 1, 1, seasonId: "") });

            Assert.Single(Assert.Single(groups).Episodes);
        }

        [Fact]
        public void An_episode_whose_season_the_server_never_listed_is_still_shown()
        {
            // Losing an episode because its folder was not enumerated would be the worst possible
            // failure here: it is a file that plays, and nothing on screen would admit it existed.
            var groups = SeriesGrouping.Group(
                Array.Empty<JellyfinSeason>(),
                new[] { Episode("e1", 3, 1), Episode("e2", 3, 2) });

            var group = Assert.Single(groups);
            Assert.Equal("Season 3", group.Name);
            Assert.Equal(2, group.Episodes.Count);
        }

        [Fact]
        public void An_episode_with_no_season_at_all_gets_one_group_of_its_own()
        {
            var groups = SeriesGrouping.Group(
                Array.Empty<JellyfinSeason>(),
                new[] { Episode("e1", null, null), Episode("e2", null, null) });

            var group = Assert.Single(groups);
            Assert.Equal("Episodes", group.Name);
            Assert.Null(group.Number);
            Assert.Equal(2, group.Episodes.Count);
        }

        [Fact]
        public void Specials_come_last_rather_than_first()
        {
            // Season zero sorts first on the number alone, which is where Jellyfin's own API puts
            // it. Somebody opening a programme wants episode one, not a Christmas special.
            var groups = SeriesGrouping.Group(
                new[] { Season("s0", 0, "Specials"), Season("s1", 1), Season("s2", 2) },
                Array.Empty<JellyfinEpisode>());

            Assert.Equal(new[] { "Season 1", "Season 2", "Specials" }, groups.Select(g => g.Name).ToArray());
        }

        [Fact]
        public void An_unnumbered_season_comes_after_the_specials()
        {
            var groups = SeriesGrouping.Group(
                new[] { Season("sx", null, "Extras"), Season("s0", 0, "Specials"), Season("s1", 1) },
                Array.Empty<JellyfinEpisode>());

            Assert.Equal(new[] { "Season 1", "Specials", "Extras" }, groups.Select(g => g.Name).ToArray());
        }

        [Fact]
        public void A_season_the_server_listed_but_did_not_fill_is_still_shown()
        {
            // A season with no episodes is a fact about the library worth seeing, not a reason to
            // pretend the season does not exist.
            var groups = SeriesGrouping.Group(new[] { Season("s1", 1) }, Array.Empty<JellyfinEpisode>());

            Assert.Empty(Assert.Single(groups).Episodes);
        }

        [Fact]
        public void Episodes_within_a_season_are_in_broadcast_order()
        {
            var groups = SeriesGrouping.Group(
                new[] { Season("s1", 1) },
                new[] { Episode("c", 1, 3, "s1"), Episode("a", 1, 1, "s1"), Episode("b", 1, 2, "s1") });

            Assert.Equal(
                new[] { "a", "b", "c" },
                Assert.Single(groups).Episodes.Select(e => e.ItemId).ToArray());
        }

        [Fact]
        public void Nothing_at_all_produces_no_groups_rather_than_throwing()
        {
            Assert.Empty(SeriesGrouping.Group(null, null));
            Assert.Empty(SeriesGrouping.Group(Array.Empty<JellyfinSeason>(), Array.Empty<JellyfinEpisode>()));
        }

        // ---------- what a row says ----------

        [Theory]
        [InlineData(1, 2, "S01E02")]
        [InlineData(12, 134, "S12E134")]
        [InlineData(0, 1, "S00E01")]
        [InlineData(null, 2, "E02")]
        [InlineData(1, null, "S01")]
        [InlineData(null, null, "")]
        public void An_episode_is_numbered_the_way_everybody_writes_it(int? season, int? number, string expected)
        {
            Assert.Equal(expected, SeriesGrouping.EpisodeLabel(season, number));
        }

        [Fact]
        public void An_unnamed_episode_is_called_after_its_number()
        {
            // A row reading only "S02E07" with an empty title beside it looks like a rendering
            // fault rather than like an unnamed episode.
            Assert.Equal("Episode 7", SeriesGrouping.EpisodeTitle(Episode("e", 2, 7, name: "")));
            Assert.Equal("Untitled episode", SeriesGrouping.EpisodeTitle(Episode("e", null, null, name: " ")));
        }

        [Theory]
        [InlineData(48, "48 min")]
        [InlineData(0, "")]
        [InlineData(null, "")]
        public void A_runtime_is_printed_only_when_there_is_one(int? minutes, string expected)
        {
            Assert.Equal(expected, SeriesGrouping.RuntimeLabel(minutes));
        }

        [Fact]
        public void A_row_carries_everything_the_list_needs_and_nothing_it_does_not()
        {
            var episode = new JellyfinEpisode
            {
                ItemId = "e1",
                SeriesId = "series1",
                Name = "An Invented Episode",
                SeasonNumber = 1,
                Number = 2,
                Overview = "Something happens.",
                RuntimeMinutes = 48
            };

            var row = SeriesGrouping.ToRow(episode);

            Assert.Equal("e1", row.ItemId);
            Assert.Equal("S01E02", row.Label);
            Assert.Equal("An Invented Episode", row.Title);
            Assert.Equal("48 min", row.Runtime);
            Assert.True(row.HasRuntime);
            Assert.Equal("Something happens.", row.Tip);
        }

        [Fact]
        public void An_episode_with_no_plot_gets_no_empty_tooltip()
        {
            // Avalonia shows a tooltip for an empty string and nothing for null, so an episode the
            // server said nothing about would pop up an empty box under the cursor.
            var row = SeriesGrouping.ToRow(Episode("e1", 1, 1));

            Assert.False(row.HasOverview);
            Assert.Null(row.Tip);
        }

        [Fact]
        public void A_season_heading_counts_its_episodes()
        {
            Assert.Equal("1 EPISODE", SeriesGrouping.CountLabel(1));
            Assert.Equal("12 EPISODES", SeriesGrouping.CountLabel(12));
            Assert.Equal("0 EPISODES", SeriesGrouping.CountLabel(0));
        }

        [Fact]
        public void A_programme_describes_what_was_actually_fetched()
        {
            var groups = SeriesGrouping.Group(
                new[] { Season("s1", 1), Season("s2", 2) },
                new[] { Episode("e1", 1, 1, "s1"), Episode("e2", 2, 1, "s2"), Episode("e3", 2, 2, "s2") });

            Assert.Equal("2 seasons · 3 episodes", SeriesGrouping.Describe(groups));
        }

        [Fact]
        public void A_programme_nothing_has_been_fetched_for_describes_nothing()
        {
            // So the screen can say "looking for episodes" rather than "0 seasons", which reads as
            // an answer when it is the absence of one.
            Assert.Equal("", SeriesGrouping.Describe(Array.Empty<SeasonGroup>()));
            Assert.Equal("", SeriesGrouping.Describe(null));
        }

        [Fact]
        public void One_season_and_one_episode_are_described_in_the_singular()
        {
            var groups = SeriesGrouping.Group(new[] { Season("s1", 1) }, new[] { Episode("e1", 1, 1, "s1") });

            Assert.Equal("1 season · 1 episode", SeriesGrouping.Describe(groups));
        }
    }
}
