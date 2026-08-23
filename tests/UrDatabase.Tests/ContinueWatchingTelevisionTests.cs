using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Television in the Continue watching row: an episode is a card the library does not hold, so
    /// every fact on it comes from somewhere else and every one of them can be wrong.
    /// </summary>
    public class ResumeRowTelevisionTests
    {
        private const string ShowId = "series-1";
        private const string ShowTitle = "Interview with the Vampire";
        private const string EpisodeName = "In throes of increasing wonder … ";

        private static UiMovie Series(string itemId = ShowId, string title = ShowTitle) => new()
        {
            Id = 0,
            Source = MovieSource.Jellyfin,
            Kind = MediaKind.Series,
            RemoteId = itemId,
            Title = title,
            Genres = "Drama",
            SeasonCount = 2,
            PosterPath = "https://media.invalid/series-1/poster.jpg"
        };

        private static UiMovie Film(string itemId, string title, int year = 1994) => new()
        {
            Id = 0,
            Source = MovieSource.Jellyfin,
            RemoteId = itemId,
            Title = title,
            Year = year,
            Genres = "Drama"
        };

        private static JellyfinResumeItem Episode(
            string itemId = "episode-1",
            string seriesId = ShowId,
            int? season = 1,
            int? number = 1,
            string name = EpisodeName,
            int positionSeconds = 600,
            int? runtimeSeconds = 3000,
            int sortOrder = 0) => new()
            {
                ItemId = itemId,
                ItemType = JellyfinResumeItem.EpisodeType,
                SeriesId = seriesId,
                SeriesName = ShowTitle,
                SeasonNumber = season,
                EpisodeNumber = number,
                Name = name,
                PositionTicks = PlaybackPosition.SecondsToTicks(positionSeconds),
                RuntimeTicks = runtimeSeconds is null ? null : PlaybackPosition.SecondsToTicks(runtimeSeconds.Value),
                SortOrder = sortOrder
            };

        private static JellyfinResumeItem FilmEntry(string itemId, int positionSeconds = 1500, int sortOrder = 0) => new()
        {
            ItemId = itemId,
            ItemType = JellyfinResumeItem.MovieType,
            PositionTicks = PlaybackPosition.SecondsToTicks(positionSeconds),
            RuntimeTicks = PlaybackPosition.SecondsToTicks(6000),
            SortOrder = sortOrder
        };

        [Fact]
        public void An_episode_is_in_the_row_under_its_programme_and_its_number()
        {
            // The whole point of the design: the episode's own name identifies nothing, so the
            // card is titled with the show and placed with S1E1.
            var show = Series();

            var row = ResumeRow.Build(new[] { show }, new[] { Episode() });

            var card = Assert.Single(row);

            Assert.Equal(ShowTitle, card.Title);
            Assert.Equal("S1E1", card.MetaLine);
            Assert.Equal("40 MIN LEFT", card.ResumeNote);
            Assert.True(card.IsEpisode);
        }

        [Fact]
        public void An_episode_card_borrows_the_programmes_poster()
        {
            // A row of 2:3 plates with one 16:9 still in it is the version of this that looks
            // broken, and the show's poster is already on screen below.
            var show = Series();

            var card = Assert.Single(ResumeRow.Build(new[] { show }, new[] { Episode() }));

            Assert.Equal(show.DisplayPosterPath, card.DisplayPosterPath);
        }

        [Fact]
        public void An_episode_card_is_not_its_programme()
        {
            var show = Series();

            var card = Assert.Single(ResumeRow.Build(new[] { show }, new[] { Episode() }));

            Assert.NotEqual(show.Key, card.Key);
            Assert.Equal("episode-1", card.RemoteId);
            Assert.Equal(ShowId, card.SeriesId);
            Assert.False(card.IsSeries);

            // The series badge would be a lie on an episode; the server badge is the useful fact
            // in a row where some films play offline.
            Assert.True(card.ShowServerBadge);
        }

        [Fact]
        public void A_programme_takes_no_mark_from_an_episode_of_it()
        {
            // "Twenty minutes from the end" is true of one episode and says nothing about a
            // hundred hours of television.
            var show = Series();

            ResumeRow.Build(new[] { show }, new[] { Episode() });

            Assert.False(show.HasResume);
            Assert.Null(show.ResumeNote);
        }

        [Fact]
        public void A_mixed_row_keeps_the_order_the_server_gave_it()
        {
            // Two programmes rather than two episodes of one, because only one episode of a
            // programme is ever in the row — see below.
            var show = Series();
            var other = Series("series-2", "The Other Programme");
            var film = Film("film-1", "The Drama");

            var row = ResumeRow.Build(
                new[] { film, show, other },
                new[]
                {
                    Episode(sortOrder: 0),
                    FilmEntry("film-1", sortOrder: 1),
                    Episode("episode-2", seriesId: "series-2", season: 4, number: 7, name: "Après le Déluge", sortOrder: 2)
                });

            Assert.Equal(
                new[] { ShowTitle, "The Drama", "The Other Programme" },
                row.Select(c => c.Title).ToArray());

            Assert.Equal(new[] { "S1E1", "", "S4E7" }, row.Select(c => c.EpisodeLabel).ToArray());
        }

        [Fact]
        public void Only_one_episode_of_a_programme_is_in_the_row_and_it_is_the_newest()
        {
            // Somebody who dips in and out of a series is part way through several of its
            // episodes at once. All of them in the row is one show repeated across the shelf under
            // a single poster, with S1E1 and S1E2 the only difference between two identical cards.
            // The server lists most recently watched first, so the first is the one to keep.
            var show = Series();

            var row = ResumeRow.Build(
                new[] { show },
                new[]
                {
                    Episode("episode-2", season: 1, number: 2, name: "Après le Déluge", sortOrder: 0),
                    Episode("episode-1", season: 1, number: 1, sortOrder: 1),
                    Episode("episode-9", season: 2, number: 3, name: "Like Angels Put in Hell by God", sortOrder: 2)
                });

            var card = Assert.Single(row);

            Assert.Equal("S1E2", card.EpisodeLabel);
            Assert.Equal("episode-2", card.RemoteId);
        }

        [Fact]
        public void Each_programme_still_gets_its_own_card()
        {
            // The rule is one per programme, not one in total: two shows on the go are two things
            // to carry on with.
            var row = ResumeRow.Build(
                new[] { Series(), Series("series-2", "The Other Programme") },
                new[]
                {
                    Episode(sortOrder: 0),
                    Episode("episode-7", seriesId: "series-2", season: 2, number: 1, sortOrder: 1)
                });

            Assert.Equal(new[] { ShowTitle, "The Other Programme" }, row.Select(c => c.Title).ToArray());
        }

        [Fact]
        public void Two_part_watched_films_are_both_kept()
        {
            // Films are not folded the way episodes are. Neither stands in for the other.
            var one = Film("film-1", "The Drama");
            var two = Film("film-2", "The Western", 1971);

            var row = ResumeRow.Build(
                new[] { one, two },
                new[] { FilmEntry("film-1", sortOrder: 0), FilmEntry("film-2", sortOrder: 1) });

            Assert.Equal(new[] { "The Drama", "The Western" }, row.Select(c => c.Title).ToArray());
        }

        [Fact]
        public void Dismissing_the_newest_episode_brings_the_one_behind_it_forward()
        {
            // The two rules compose in the order that makes sense: what the owner has dismissed is
            // not in the row at all, so the programme's place goes to the next episode they are
            // part way through rather than being left empty.
            var show = Series();

            var entries = new[]
            {
                Episode("episode-2", season: 1, number: 2, name: "Après le Déluge", sortOrder: 0),
                Episode("episode-1", season: 1, number: 1, sortOrder: 1)
            };

            var row = ResumeRow.Build(
                new[] { show },
                entries,
                new[] { new ResumeDismissal("episode-2", entries[0].PositionTicks) });

            var card = Assert.Single(row);

            Assert.Equal("S1E1", card.EpisodeLabel);
        }

        [Fact]
        public void A_mixed_row_says_what_it_counted()
        {
            var show = Series();
            var film = Film("film-1", "The Drama");

            var other = Series("series-2", "The Other Programme");

            var row = ResumeRow.Build(
                new[] { film, show, other },
                new[]
                {
                    FilmEntry("film-1"),
                    Episode(sortOrder: 1),
                    Episode("episode-2", seriesId: "series-2", number: 2, sortOrder: 2)
                });

            var shelf = LibraryGrouping.BuildShelves(new[] { film, show, other }, Array.Empty<string>(), row)[0];

            // "3 FILMS" over two episodes and a film is exactly the dishonesty the count label
            // exists to prevent.
            Assert.Equal("1 FILM · 2 EPISODES", shelf.CountLabel);
        }

        [Fact]
        public void An_episode_with_no_position_is_not_something_to_continue()
        {
            var show = Series();

            Assert.Empty(ResumeRow.Build(new[] { show }, new[] { Episode(positionSeconds: 0) }));
        }

        [Fact]
        public void An_episode_of_a_programme_the_library_has_never_seen_is_dropped()
        {
            // Television that was never synced, or a library this app was not pointed at. There is
            // no poster, no show to open, and nothing else in the app that can answer for it.
            var row = ResumeRow.Build(new[] { Film("film-1", "The Drama") }, new[] { Episode() });

            Assert.Empty(row);
        }

        [Fact]
        public void An_episode_with_no_series_id_is_dropped_rather_than_titled_from_the_entry()
        {
            var show = Series();

            Assert.Empty(ResumeRow.Build(new[] { show }, new[] { Episode(seriesId: "  ") }));
        }

        [Fact]
        public void Narrowing_the_library_to_films_takes_the_episodes_out_of_the_row_too()
        {
            // The row is built from whatever the page is showing. With television filtered out
            // there is no programme to resolve an episode through, so the shelf stops describing
            // a library nothing else on the page is showing.
            var film = Film("film-1", "The Drama");

            var visible = LibraryFilter.Apply(new[] { film, Series() }, LibraryKind.Films);

            var row = ResumeRow.Build(visible, new[] { Episode(), FilmEntry("film-1", sortOrder: 1) });

            Assert.Equal(new[] { "The Drama" }, row.Select(c => c.Title).ToArray());
        }

        [Fact]
        public void A_season_the_server_did_not_number_still_places_the_episode()
        {
            var show = Series();

            var card = Assert.Single(ResumeRow.Build(new[] { show }, new[] { Episode(season: null, number: 7) }));

            Assert.Equal("E7", card.EpisodeLabel);
            Assert.Equal("E7", card.MetaLine);
        }

        [Fact]
        public void An_episode_the_server_numbered_not_at_all_falls_back_to_its_own_name()
        {
            var show = Series();

            var card = Assert.Single(ResumeRow.Build(
                new[] { show },
                new[] { Episode(season: null, number: null, name: "Pilot") }));

            Assert.Equal("", card.EpisodeLabel);
            Assert.Equal("Pilot", card.MetaLine);
        }

        [Fact]
        public void The_episode_name_the_card_has_no_room_for_is_in_the_tooltip()
        {
            // The card prints "S1E1" and how much is left, which is all 152 pixels will carry.
            // The name has to be somewhere, and this is it.
            var card = Assert.Single(ResumeRow.Build(new[] { Series() }, new[] { Episode() }));

            Assert.Equal($"{ShowTitle} — S1E1 · In throes of increasing wonder … — 40 MIN LEFT", card.CardTooltip);
        }

        [Fact]
        public void A_card_says_the_number_and_not_a_truncation_of_the_name()
        {
            // "S1E1 · In …" cost the line and said nothing. The number places the episode; the
            // name does not fit and is not worth the space it would take to fail to fit.
            var card = Assert.Single(ResumeRow.Build(new[] { Series() }, new[] { Episode() }));

            Assert.Equal("S1E1", card.MetaLine);
            Assert.DoesNotContain("…", card.MetaLine, StringComparison.Ordinal);
        }

        [Fact]
        public void A_programme_the_cache_has_not_named_falls_back_to_what_the_server_just_said()
        {
            var show = Series();
            show.Title = "";

            var card = Assert.Single(ResumeRow.Build(new[] { show }, new[] { Episode() }));

            Assert.Equal(ShowTitle, card.Title);
        }

        [Fact]
        public void Building_the_row_twice_does_not_produce_two_episode_cards_for_one_episode()
        {
            var show = Series();

            var first = ResumeRow.Build(new[] { show }, new[] { Episode(), Episode(sortOrder: 1) });
            var second = ResumeRow.Build(new[] { show }, new[] { Episode() });

            Assert.Single(first);
            Assert.Single(second);
        }
    }

    /// <summary>
    /// Whether a dismissal still applies. The rule most likely to be got wrong, and the one whose
    /// being wrong is hardest to see: a dismissal that never expires is a blacklist nobody can
    /// read, and one that expires too eagerly makes the gesture look broken.
    /// </summary>
    public class ResumeDismissalRuleTests
    {
        private static JellyfinResumeItem Entry(string id, int positionSeconds, int sortOrder = 0) => new()
        {
            ItemId = id,
            PositionTicks = PlaybackPosition.SecondsToTicks(positionSeconds),
            RuntimeTicks = PlaybackPosition.SecondsToTicks(6000),
            SortOrder = sortOrder
        };

        private static ResumeDismissal Dismissal(string id, int positionSeconds) =>
            new(id, PlaybackPosition.SecondsToTicks(positionSeconds));

        [Fact]
        public void A_dismissal_hides_the_item_at_the_position_it_was_made_at()
        {
            Assert.True(ResumeDismissals.Hides(Dismissal("item1", 1500), Entry("item1", 1500)));
        }

        [Fact]
        public void A_dismissal_stops_applying_once_the_position_moves()
        {
            // The owner's rule: watch more of it anywhere else and you have plainly not abandoned
            // it, so it comes back.
            Assert.False(ResumeDismissals.Hides(Dismissal("item1", 1500), Entry("item1", 1800)));

            // Backwards counts too. Somebody who rewound has been watching it.
            Assert.False(ResumeDismissals.Hides(Dismissal("item1", 1500), Entry("item1", 900)));
        }

        [Fact]
        public void A_dismissal_that_nothing_has_moved_keeps_applying_forever()
        {
            var dismissal = Dismissal("item1", 1500);

            for (var sync = 0; sync < 50; sync++)
                Assert.True(ResumeDismissals.Hides(dismissal, Entry("item1", 1500)));
        }

        [Fact]
        public void A_dismissal_says_nothing_about_any_other_item()
        {
            Assert.False(ResumeDismissals.Hides(Dismissal("item1", 1500), Entry("item2", 1500)));
        }

        [Fact]
        public void Dismissing_one_episode_does_not_hide_its_siblings()
        {
            // Keyed on the item and never on the programme. The next episode is a different thing
            // to be part way through, and hiding a whole show would make the gesture far larger
            // than it looks.
            var row = new[]
            {
                Entry("episode-1", 600),
                Entry("episode-2", 900, 1),
                Entry("episode-3", 120, 2)
            };

            var showing = ResumeDismissals.Apply(row, new[] { Dismissal("episode-1", 600) });

            Assert.Equal(new[] { "episode-2", "episode-3" }, showing.Select(e => e.ItemId).ToArray());
        }

        [Fact]
        public void Applying_no_dismissals_at_all_changes_nothing()
        {
            var row = new[] { Entry("item1", 1500), Entry("item2", 300, 1) };

            Assert.Equal(2, ResumeDismissals.Apply(row, null).Count);
            Assert.Equal(2, ResumeDismissals.Apply(row, Array.Empty<ResumeDismissal>()).Count);
        }

        [Fact]
        public void A_dismissal_of_nothing_hides_nothing()
        {
            Assert.False(ResumeDismissals.Hides(null, Entry("item1", 1500)));
            Assert.False(ResumeDismissals.Hides(Dismissal("item1", 1500), null));
            Assert.False(ResumeDismissals.Hides(new ResumeDismissal("  ", 0), Entry("item1", 0)));
        }

        [Fact]
        public void A_dismissal_whose_position_has_moved_is_stale()
        {
            var stale = ResumeDismissals.Stale(
                new[] { Dismissal("item1", 1500), Dismissal("item2", 300) },
                new[] { Entry("item1", 1800), Entry("item2", 300, 1) });

            Assert.Equal(new[] { "item1" }, stale.Select(d => d.ItemId).ToArray());
        }

        [Fact]
        public void A_dismissal_for_something_no_longer_in_the_row_is_stale()
        {
            // Finished, reset, or removed from the server. It cannot hide anything, and keeping it
            // is how this table would grow forever.
            var stale = ResumeDismissals.Stale(
                new[] { Dismissal("gone", 1500) },
                new[] { Entry("item1", 300) });

            Assert.Equal(new[] { "gone" }, stale.Select(d => d.ItemId).ToArray());
        }
    }

    /// <summary>
    /// Dismissing something from the row, as the window does it: the rule, the row it produces,
    /// and the shelf that disappears when there is nothing left in it.
    /// </summary>
    public class DismissedFromTheRowTests
    {
        private static UiMovie Film(string itemId, string title) => new()
        {
            Id = 0,
            Source = MovieSource.Jellyfin,
            RemoteId = itemId,
            Title = title,
            Year = 1994,
            Genres = "Drama"
        };

        private static JellyfinResumeItem Entry(string id, int positionSeconds, int sortOrder = 0) => new()
        {
            ItemId = id,
            PositionTicks = PlaybackPosition.SecondsToTicks(positionSeconds),
            RuntimeTicks = PlaybackPosition.SecondsToTicks(6000),
            SortOrder = sortOrder
        };

        [Fact]
        public void A_dismissed_film_is_not_in_the_row()
        {
            var film = Film("item1", "The Drama");

            var row = ResumeRow.Build(
                new[] { film },
                new[] { Entry("item1", 1500) },
                new[] { new ResumeDismissal("item1", PlaybackPosition.SecondsToTicks(1500)) });

            Assert.Empty(row);
        }

        [Fact]
        public void A_dismissed_film_carries_no_progress_mark_anywhere_else_either()
        {
            // The mark is stamped on the card the shelves below share, so a dismissal that only
            // filtered the row would leave a brass rule under a poster nothing explains.
            var film = Film("item1", "The Drama");

            ResumeRow.Build(
                new[] { film },
                new[] { Entry("item1", 1500) },
                new[] { new ResumeDismissal("item1", PlaybackPosition.SecondsToTicks(1500)) });

            Assert.False(film.HasResume);
            Assert.Null(film.ResumeNote);
        }

        [Fact]
        public void A_dismissed_film_returns_once_the_server_reports_a_different_position()
        {
            var film = Film("item1", "The Drama");
            var dismissal = new ResumeDismissal("item1", PlaybackPosition.SecondsToTicks(1500));

            Assert.Empty(ResumeRow.Build(new[] { film }, new[] { Entry("item1", 1500) }, new[] { dismissal }));

            var row = ResumeRow.Build(new[] { film }, new[] { Entry("item1", 2400) }, new[] { dismissal });

            Assert.Same(film, Assert.Single(row));
            Assert.True(film.HasResume);
        }

        [Fact]
        public void A_dismissed_film_stays_out_while_the_position_does_not_move()
        {
            var film = Film("item1", "The Drama");
            var dismissal = new ResumeDismissal("item1", PlaybackPosition.SecondsToTicks(1500));

            for (var sync = 0; sync < 5; sync++)
                Assert.Empty(ResumeRow.Build(new[] { film }, new[] { Entry("item1", 1500) }, new[] { dismissal }));
        }

        [Fact]
        public void The_row_disappears_entirely_when_everything_in_it_is_dismissed()
        {
            // A heading with nothing under it reads as a shelf that failed to load.
            var film = Film("item1", "The Drama");

            var row = ResumeRow.Build(
                new[] { film },
                new[] { Entry("item1", 1500) },
                new[] { new ResumeDismissal("item1", PlaybackPosition.SecondsToTicks(1500)) });

            var shelves = LibraryGrouping.BuildShelves(new[] { film }, new[] { "Drama" }, row);

            Assert.Equal(new[] { "Drama" }, shelves.Select(s => s.Name).ToArray());
        }

        [Fact]
        public void Dismissing_one_thing_leaves_the_rest_of_the_row_alone()
        {
            var one = Film("item1", "The Drama");
            var two = Film("item2", "The Other");

            var row = ResumeRow.Build(
                new[] { one, two },
                new[] { Entry("item1", 1500), Entry("item2", 300, 1) },
                new[] { new ResumeDismissal("item1", PlaybackPosition.SecondsToTicks(1500)) });

            Assert.Equal(new[] { "The Other" }, row.Select(c => c.Title).ToArray());
        }
    }

    /// <summary>
    /// Dismissals on disk, and the two things that must be true of them: that a sync cannot wipe
    /// them, and that they clean themselves up.
    /// </summary>
    public class ResumeDismissalStoreTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _dbPath;

        public ResumeDismissalStoreTests()
        {
            // A temporary directory of this test's own. Never the real application data directory:
            // that one holds somebody's catalogue, their poster cache and their credentials.
            _dir = Path.Combine(Path.GetTempPath(), "urdb-dismiss-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _dbPath = Path.Combine(_dir, "movies.db");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static JellyfinResumeItem Entry(string id, int positionSeconds, int sortOrder = 0) => new()
        {
            ItemId = id,
            PositionTicks = PlaybackPosition.SecondsToTicks(positionSeconds),
            RuntimeTicks = PlaybackPosition.SecondsToTicks(6000),
            SortOrder = sortOrder
        };

        [Fact]
        public void The_schema_makes_room_for_dismissals()
        {
            using var conn = Database.Open(_dbPath);

            Assert.Empty(ResumeDismissalStore.Load(conn));
        }

        [Fact]
        public void An_existing_library_gains_the_table_rather_than_failing_on_it()
        {
            using (var old = Database.Open(_dbPath))
            {
                using var drop = old.CreateCommand();
                drop.CommandText = "DROP TABLE jellyfin_resume_dismissals";
                drop.ExecuteNonQuery();
            }

            using var upgraded = Database.Open(_dbPath);

            Assert.Empty(ResumeDismissalStore.Load(upgraded));
        }

        [Fact]
        public void A_dismissal_survives_a_restart()
        {
            using (var conn = Database.Open(_dbPath))
            {
                ResumeDismissalStore.Dismiss(conn, "item1", PlaybackPosition.SecondsToTicks(1500));
            }

            using var reopened = Database.Open(_dbPath);

            var kept = Assert.Single(ResumeDismissalStore.Load(reopened));

            Assert.Equal("item1", kept.ItemId);
            Assert.Equal(PlaybackPosition.SecondsToTicks(1500), kept.PositionTicks);
        }

        [Fact]
        public void A_dismissal_survives_a_sync_that_replaces_the_cached_row()
        {
            // The reason this is a table of its own. Replace deletes every row of jellyfin_resume
            // and writes the server's answer back, so a dismissal stored there would last minutes.
            using var conn = Database.Open(_dbPath);

            JellyfinResumeCache.Replace(conn, new[] { Entry("item1", 1500) });
            ResumeDismissalStore.Dismiss(conn, "item1", PlaybackPosition.SecondsToTicks(1500));

            JellyfinResumeCache.Replace(conn, new[] { Entry("item1", 1500) });

            Assert.Single(ResumeDismissalStore.Load(conn));
            Assert.Empty(ResumeDismissals.Apply(JellyfinResumeCache.Load(conn), ResumeDismissalStore.Load(conn)));
        }

        [Fact]
        public void Dismissing_the_same_thing_again_records_where_it_is_now()
        {
            using var conn = Database.Open(_dbPath);

            ResumeDismissalStore.Dismiss(conn, "item1", PlaybackPosition.SecondsToTicks(1500));
            ResumeDismissalStore.Dismiss(conn, "item1", PlaybackPosition.SecondsToTicks(2400));

            var kept = Assert.Single(ResumeDismissalStore.Load(conn));

            Assert.Equal(PlaybackPosition.SecondsToTicks(2400), kept.PositionTicks);
        }

        [Fact]
        public void Undoing_a_dismissal_puts_the_item_straight_back()
        {
            using var conn = Database.Open(_dbPath);

            JellyfinResumeCache.Replace(conn, new[] { Entry("item1", 1500) });
            ResumeDismissalStore.Dismiss(conn, "item1", PlaybackPosition.SecondsToTicks(1500));

            ResumeDismissalStore.Restore(conn, "item1");

            Assert.Empty(ResumeDismissalStore.Load(conn));
            Assert.Single(ResumeDismissals.Apply(JellyfinResumeCache.Load(conn), ResumeDismissalStore.Load(conn)));
        }

        [Fact]
        public void Pruning_forgets_a_dismissal_whose_position_has_moved_and_keeps_one_that_has_not()
        {
            using var conn = Database.Open(_dbPath);

            ResumeDismissalStore.Dismiss(conn, "moved", PlaybackPosition.SecondsToTicks(1500));
            ResumeDismissalStore.Dismiss(conn, "still", PlaybackPosition.SecondsToTicks(300));

            var pruned = ResumeDismissalStore.Prune(conn, new[] { Entry("moved", 1800), Entry("still", 300, 1) });

            Assert.Equal(1, pruned);
            Assert.Equal(new[] { "still" }, ResumeDismissalStore.Load(conn).Select(d => d.ItemId).ToArray());
        }

        [Fact]
        public void Dismissing_nothing_at_all_writes_nothing()
        {
            using var conn = Database.Open(_dbPath);

            ResumeDismissalStore.Dismiss(conn, "  ", 500);

            Assert.Empty(ResumeDismissalStore.Load(conn));
        }

        [Fact]
        public void Disconnecting_a_server_forgets_them()
        {
            using var conn = Database.Open(_dbPath);
            ResumeDismissalStore.Dismiss(conn, "item1", 500);

            ResumeDismissalStore.Clear(conn);

            Assert.Empty(ResumeDismissalStore.Load(conn));
        }
    }

    /// <summary>
    /// An episode through the cache, the migration that lets an existing library hold one, and the
    /// sync that writes both halves.
    /// </summary>
    public class ResumeTelevisionCacheTests : IDisposable
    {
        private const string ServerUrl = "http://media.invalid:8096";

        private readonly string _dir;
        private readonly string _dbPath;
        private readonly TempLog _log = new();

        public ResumeTelevisionCacheTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-resume-tv-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _dbPath = Path.Combine(_dir, "movies.db");
        }

        public void Dispose()
        {
            _log.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static JellyfinResumeItem Episode(string id = "episode-1", int sortOrder = 0) => new()
        {
            ItemId = id,
            ItemType = JellyfinResumeItem.EpisodeType,
            SeriesId = "series-1",
            SeriesName = "Interview with the Vampire",
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Name = "In throes of increasing wonder … ",
            PositionTicks = PlaybackPosition.SecondsToTicks(600),
            RuntimeTicks = PlaybackPosition.SecondsToTicks(3000),
            SortOrder = sortOrder
        };

        [Fact]
        public void An_episode_survives_a_restart_with_everything_its_card_needs()
        {
            using (var conn = Database.Open(_dbPath))
            {
                JellyfinResumeCache.Replace(conn, new[] { Episode() });
            }

            using var reopened = Database.Open(_dbPath);

            var loaded = Assert.Single(JellyfinResumeCache.Load(reopened));

            Assert.True(loaded.IsEpisode);
            Assert.Equal("series-1", loaded.SeriesId);
            Assert.Equal("Interview with the Vampire", loaded.SeriesName);
            Assert.Equal(1, loaded.SeasonNumber);
            Assert.Equal(1, loaded.EpisodeNumber);
            Assert.Equal("In throes of increasing wonder …", loaded.Name);
        }

        [Fact]
        public void A_film_is_stored_as_a_film_and_not_as_an_episode_of_nothing()
        {
            using var conn = Database.Open(_dbPath);

            JellyfinResumeCache.Replace(conn, new[]
            {
                new JellyfinResumeItem { ItemId = "item1", PositionTicks = 900_000_000 }
            });

            var loaded = Assert.Single(JellyfinResumeCache.Load(conn));

            Assert.False(loaded.IsEpisode);
            Assert.Equal(JellyfinResumeItem.MovieType, loaded.ItemType);
            Assert.Equal("", loaded.SeriesId);
            Assert.Null(loaded.SeasonNumber);
            Assert.Null(loaded.EpisodeNumber);
        }

        [Fact]
        public void A_library_written_before_television_gains_the_columns_rather_than_failing_on_them()
        {
            // The upgrade path that matters. CREATE TABLE IF NOT EXISTS sees the table, does
            // nothing, and every sync then fails on "no such column" — which costs the owner the
            // shelf they already had rather than gaining them episodes.
            using (var old = Database.Open(_dbPath))
            {
                using var rebuild = old.CreateCommand();
                rebuild.CommandText = @"
DROP TABLE jellyfin_resume;
CREATE TABLE jellyfin_resume (
    item_id           TEXT PRIMARY KEY,
    position_ticks    INTEGER NOT NULL,
    runtime_ticks     INTEGER,
    played_percentage REAL,
    sort_order        INTEGER NOT NULL,
    synced_at         TEXT NOT NULL
);
INSERT INTO jellyfin_resume (item_id, position_ticks, sort_order, synced_at)
VALUES ('old-film', 900000000, 0, '2026-01-01T00:00:00Z');";
                rebuild.ExecuteNonQuery();
            }

            using var upgraded = Database.Open(_dbPath);

            // The row written by the older build reads back as what it was: a film.
            var existing = Assert.Single(JellyfinResumeCache.Load(upgraded));
            Assert.False(existing.IsEpisode);

            // And the next sync can write an episode into the same table.
            JellyfinResumeCache.Replace(upgraded, new[] { Episode() });
            Assert.True(JellyfinResumeCache.Load(upgraded).Single().IsEpisode);
        }

        private static JellyfinSettings Settings() => new()
        {
            ServerUrl = ServerUrl,
            Username = "viewer",
            Password = "hunter2"
        };

        private const string AuthJson = """
            {
              "AccessToken": "issued-session-token",
              "User": { "Id": "22222222222222222222222222222222", "Name": "viewer" }
            }
            """;

        private const string ViewsJson = """
            {
              "Items": [ { "Id": "library-1", "Name": "Films", "CollectionType": "movies" } ],
              "TotalRecordCount": 1
            }
            """;

        private const string LibraryJson = """
            {
              "Items": [
                { "Id": "film-1", "Name": "The Drama", "ProductionYear": 1994, "Genres": ["Drama"] }
              ],
              "TotalRecordCount": 1
            }
            """;

        private const string MixedResumeJson = """
            {
              "Items": [
                {
                  "Id": "episode-1",
                  "Name": "In throes of increasing wonder … ",
                  "Type": "Episode",
                  "SeriesId": "series-1",
                  "SeriesName": "Interview with the Vampire",
                  "SeasonName": "Season 1",
                  "ParentIndexNumber": 1,
                  "IndexNumber": 1,
                  "RunTimeTicks": 30000000000,
                  "UserData": { "PlaybackPositionTicks": 6000000000, "PlayedPercentage": 20.0 }
                },
                {
                  "Id": "film-1",
                  "Name": "The Drama",
                  "Type": "Movie",
                  "ProductionYear": 1994,
                  "RunTimeTicks": 60000000000,
                  "UserData": { "PlaybackPositionTicks": 15000000000, "PlayedPercentage": 25.0 }
                }
              ]
            }
            """;

        [Fact]
        public async Task The_resume_request_asks_for_television_as_well_as_film()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("Users/AuthenticateByName", HttpStatusCode.OK, AuthJson),
                ("UserItems/Resume", HttpStatusCode.OK, MixedResumeJson));

            using var client = new JellyfinClient(Settings(), handler: handler);

            var resume = await client.GetResumeAsync();

            var request = handler.Requests.Single(r => r.Contains("UserItems/Resume", StringComparison.Ordinal));

            Assert.Contains("IncludeItemTypes=Movie,Episode", request, StringComparison.Ordinal);
            Assert.Equal(new[] { "episode-1", "film-1" }, resume.Select(e => e.ItemId).ToArray());
        }

        [Fact]
        public async Task An_episode_comes_back_with_its_programme_and_its_numbers()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("Users/AuthenticateByName", HttpStatusCode.OK, AuthJson),
                ("UserItems/Resume", HttpStatusCode.OK, MixedResumeJson));

            using var client = new JellyfinClient(Settings(), handler: handler);

            var episode = (await client.GetResumeAsync()).First();

            Assert.True(episode.IsEpisode);
            Assert.Equal("series-1", episode.SeriesId);
            Assert.Equal("Interview with the Vampire", episode.SeriesName);
            Assert.Equal(1, episode.SeasonNumber);
            Assert.Equal(1, episode.EpisodeNumber);
            Assert.Equal("In throes of increasing wonder …", episode.Name);
        }

        [Fact]
        public async Task A_film_in_the_same_answer_is_not_given_an_episodes_rendering()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("Users/AuthenticateByName", HttpStatusCode.OK, AuthJson),
                ("UserItems/Resume", HttpStatusCode.OK, MixedResumeJson));

            using var client = new JellyfinClient(Settings(), handler: handler);

            var film = (await client.GetResumeAsync()).Single(e => e.ItemId == "film-1");

            Assert.False(film.IsEpisode);
            Assert.Equal("", film.SeriesName);
            Assert.Null(film.SeasonNumber);
        }

        [Fact]
        public async Task A_sync_writes_the_mixed_row_and_prunes_a_dismissal_the_server_has_moved_past()
        {
            using (var seeded = Database.Open(_dbPath))
            {
                ResumeDismissalStore.Dismiss(seeded, "film-1", PlaybackPosition.SecondsToTicks(600));
                ResumeDismissalStore.Dismiss(seeded, "episode-1", 6_000_000_000L);
            }

            var handler = FakeHttpMessageHandler.Routed(
                ("Users/AuthenticateByName", HttpStatusCode.OK, AuthJson),
                ("UserItems/Resume", HttpStatusCode.OK, MixedResumeJson),
                ("Views", HttpStatusCode.OK, ViewsJson),
                ("Items", HttpStatusCode.OK, LibraryJson));

            using var client = new JellyfinClient(Settings(), handler: handler);
            using var conn = Database.Open(_dbPath);

            await JellyfinSync.RefreshAsync(client, conn);

            Assert.Equal(new[] { "episode-1", "film-1" }, JellyfinResumeCache.Load(conn).Select(e => e.ItemId).ToArray());

            // The film was dismissed at a position the server no longer reports, so that dismissal
            // is gone; the episode is exactly where it was dismissed, so its dismissal stands.
            Assert.Equal(new[] { "episode-1" }, ResumeDismissalStore.Load(conn).Select(d => d.ItemId).ToArray());
        }

        [Fact]
        public async Task A_sync_that_could_not_read_the_row_keeps_every_dismissal()
        {
            // Pruning against a fetch that failed would forget every dismissal the first time the
            // app was opened away from home.
            using (var seeded = Database.Open(_dbPath))
            {
                ResumeDismissalStore.Dismiss(seeded, "film-1", PlaybackPosition.SecondsToTicks(1500));
            }

            var handler = FakeHttpMessageHandler.Routed(
                ("Users/AuthenticateByName", HttpStatusCode.OK, AuthJson),
                ("UserItems/Resume", HttpStatusCode.NotFound, "{}"),
                ("Views", HttpStatusCode.OK, ViewsJson),
                ("Items", HttpStatusCode.OK, LibraryJson));

            using var client = new JellyfinClient(Settings(), handler: handler);
            using var conn = Database.Open(_dbPath);

            await JellyfinSync.RefreshAsync(client, conn);

            Assert.Single(ResumeDismissalStore.Load(conn));
        }
    }

    /// <summary>
    /// Playing an episode. The report path was written for films and turns out to be about item
    /// ids, which is what this asserts rather than assumes.
    /// </summary>
    public class EpisodePlaybackReportingTests
    {
        private const string ServerUrl = "http://media.invalid:8096";

        private const string AuthJson = """
            {
              "AccessToken": "issued-session-token",
              "User": { "Id": "22222222222222222222222222222222", "Name": "viewer" }
            }
            """;

        private static JellyfinSettings Settings() => new()
        {
            ServerUrl = ServerUrl,
            Username = "viewer",
            Password = "hunter2"
        };

        private static FakeHttpMessageHandler Handler() => new(request =>
            request.RequestUri!.ToString().Contains("AuthenticateByName", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(AuthJson, System.Text.Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.NoContent));

        [Fact]
        public async Task An_episode_is_reported_exactly_as_a_film_is()
        {
            var handler = Handler();
            using var client = new JellyfinClient(Settings(), handler: handler);

            var sink = new JellyfinPlaybackSink(client, "episode-1");

            await sink.StartedAsync(0);
            await sink.ProgressAsync(PlaybackPosition.SecondsToTicks(600), isPaused: false);
            await sink.StoppedAsync(PlaybackPosition.SecondsToTicks(900));

            Assert.Equal(
                new[]
                {
                    $"{ServerUrl}/Sessions/Playing",
                    $"{ServerUrl}/Sessions/Playing/Progress",
                    $"{ServerUrl}/Sessions/Playing/Stopped"
                },
                handler.Requests.Where(r => r.Contains("Sessions/Playing", StringComparison.Ordinal)).ToArray());

            var body = handler.RequestBodies.Last(b => b is not null && b.Contains("PositionTicks", StringComparison.Ordinal))!;

            Assert.Contains("\"ItemId\":\"episode-1\"", body, StringComparison.Ordinal);
            Assert.Contains("\"PositionTicks\":9000000000", body, StringComparison.Ordinal);
        }

        [Fact]
        public void An_episode_is_followed_on_the_same_terms_a_film_is()
        {
            using var client = new JellyfinClient(Settings(), handler: Handler());

            var vlc = new MediaPlayerLauncher.PlayerCandidate("VLC", "/somewhere/VLC");
            var launch = new MediaPlayerLauncher.LaunchedPlayer(vlc, new VlcControlEndpoint(51234, "secret"));

            // Nothing here reaches a socket: the task is started on an already-cancelled token and
            // what is being asserted is that an episode id is followed at all rather than treated
            // as something with nowhere to report to.
            using var stopped = new System.Threading.CancellationTokenSource();
            stopped.Cancel();

            Assert.NotNull(PlaybackTracking.Follow(launch, client, "episode-1", stopped.Token));
            Assert.Null(PlaybackTracking.Follow(launch, client, "  ", stopped.Token));
        }

        [Fact]
        public void An_episode_is_launched_with_a_control_interface_exactly_as_a_film_is()
        {
            // The decision the two play screens make before launching. It was written for films
            // and asks nothing about what is playing, which is the point — an episode played from
            // the series screen used to report nothing at all, and was invisible in every other
            // client in the house.
            using var configured = new JellyfinClient(Settings(), handler: Handler());
            using var unconfigured = new JellyfinClient(new JellyfinSettings());

            Assert.True(PlaybackTracking.CanReport(configured, "episode-1"));
            Assert.True(PlaybackTracking.CanReport(configured, "film-1"));

            Assert.False(PlaybackTracking.CanReport(configured, ""));
            Assert.False(PlaybackTracking.CanReport(unconfigured, "episode-1"));
            Assert.False(PlaybackTracking.CanReport(null, "episode-1"));
        }
    }

    /// <summary>
    /// Which season the series screen opens on, now that something can ask for one.
    /// </summary>
    public class SeasonToShowTests
    {
        private static SeasonGroup Season(string name, int? number) => new()
        {
            Name = name,
            Number = number,
            Episodes = new List<EpisodeRow>()
        };

        [Fact]
        public void Opening_a_programme_normally_lands_on_its_first_season()
        {
            var seasons = new[] { Season("Season 1", 1), Season("Season 2", 2) };

            Assert.Equal("Season 1", SeriesGrouping.SeasonToShow(seasons, null, null)!.Name);
        }

        [Fact]
        public void Opening_from_an_episode_lands_on_that_episodes_season()
        {
            // "S4E7, 22 minutes left" that opened on season one would have answered a question
            // with a different question.
            var seasons = new[] { Season("Season 1", 1), Season("Season 4", 4) };

            Assert.Equal("Season 4", SeriesGrouping.SeasonToShow(seasons, null, 4)!.Name);
        }

        [Fact]
        public void A_season_the_reader_has_chosen_beats_the_one_the_screen_was_opened_at()
        {
            // The list is rebuilt when the server answers, and a refresh that jumped back would
            // undo a click made while it was in flight.
            var seasons = new[] { Season("Season 1", 1), Season("Season 4", 4) };

            Assert.Equal("Season 1", SeriesGrouping.SeasonToShow(seasons, "Season 1", 4)!.Name);
        }

        [Fact]
        public void A_season_this_programme_does_not_have_falls_back_to_the_first()
        {
            var seasons = new[] { Season("Season 1", 1) };

            Assert.Equal("Season 1", SeriesGrouping.SeasonToShow(seasons, "Season 9", 9)!.Name);
        }

        [Fact]
        public void A_programme_with_no_seasons_has_nothing_to_show()
        {
            Assert.Null(SeriesGrouping.SeasonToShow(Array.Empty<SeasonGroup>(), null, 1));
            Assert.Null(SeriesGrouping.SeasonToShow(null, null, null));
        }
    }
}
