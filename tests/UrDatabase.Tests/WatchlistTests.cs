using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The watchlist: which cards are on it, what the shelf shows and in what order, and that the
    /// list survives on disk. Stored against a real SQLite file in a temporary directory, because
    /// what is being tested is what SQLite keeps.
    /// </summary>
    public class WatchlistTests : IDisposable
    {
        private readonly TempLog _log = new();
        private readonly string _dir;
        private readonly string _dbPath;

        public WatchlistTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-watchlist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _dbPath = Path.Combine(_dir, "movies.db");
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch { }
            _log.Dispose();
        }

        private static UiMovie Local(long id, string title, string? remoteId = null, string genres = "Drama") => new()
        {
            Id = id,
            Title = title,
            Genres = genres,
            Source = MovieSource.Local,
            RemoteId = remoteId
        };

        private static UiMovie Server(string itemId, string title, string genres = "Drama") => new()
        {
            Id = 0,
            Title = title,
            Genres = genres,
            Source = MovieSource.Jellyfin,
            RemoteId = itemId
        };

        private static UiMovie Series(string itemId, string title) => new()
        {
            Title = title,
            Source = MovieSource.Jellyfin,
            Kind = MediaKind.Series,
            RemoteId = itemId
        };

        private static UiMovie Episode(string itemId, string seriesTitle) => new()
        {
            Title = seriesTitle,
            Source = MovieSource.Jellyfin,
            Kind = MediaKind.Episode,
            RemoteId = itemId
        };

        private static WatchlistEntry Entry(string key, int day) =>
            new(key, new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc));

        // ---- identity -------------------------------------------------------------------------

        [Fact]
        public void A_film_or_series_can_go_on_the_watchlist_but_an_episode_cannot()
        {
            Assert.True(Watchlist.CanHold(Local(1, "Ran")));
            Assert.True(Watchlist.CanHold(Server("abc", "Ran")));
            Assert.True(Watchlist.CanHold(Series("s1", "Severance")));

            Assert.False(Watchlist.CanHold(Episode("e1", "Severance")));
            Assert.False(Watchlist.CanHold(null));

            // No identity to store it under.
            Assert.False(Watchlist.CanHold(Local(0, "Unsaved")));
            Assert.False(Watchlist.CanHold(Server("", "Nameless")));
        }

        [Fact]
        public void A_catalogued_film_the_server_also_holds_answers_to_both_keys_its_own_first()
        {
            Assert.Equal(new[] { "local:7", "jellyfin:abc" }, Watchlist.KeysFor(Local(7, "Ran", remoteId: "abc")));
            Assert.Equal(new[] { "local:7" }, Watchlist.KeysFor(Local(7, "Ran")));
            Assert.Equal(new[] { "jellyfin:abc" }, Watchlist.KeysFor(Server("abc", "Ran")));
            Assert.Equal(new[] { "jellyfin:series:s1" }, Watchlist.KeysFor(Series("s1", "Severance")));
            Assert.Empty(Watchlist.KeysFor(Episode("e1", "Severance")));
        }

        [Fact]
        public void The_details_screen_computes_the_same_keys_as_the_card_it_was_opened_from()
        {
            var both = Local(7, "Ran", remoteId: "abc");
            var server = Server("abc", "Ran");

            Assert.Equal(Watchlist.KeysFor(both), Watchlist.KeysFor(both.Id, both.RemoteId));
            Assert.Equal(Watchlist.KeysFor(server), Watchlist.KeysFor(server.Id, server.RemoteId));
            Assert.Empty(Watchlist.KeysFor(0, null));
        }

        [Fact]
        public void A_server_film_added_before_it_was_downloaded_is_still_on_the_watchlist_afterwards()
        {
            // Added while only the server had it...
            var entries = new[] { Entry("jellyfin:abc", 1) };

            // ...and now it is a catalogued film carrying the server's id beside its own.
            var downloaded = Local(42, "Ran", remoteId: "abc");

            Assert.True(Watchlist.Contains(entries, downloaded));
            Assert.Same(downloaded, Assert.Single(Watchlist.Build(new[] { downloaded }, entries)));
        }

        [Fact]
        public void Server_ids_match_whatever_their_case()
        {
            Assert.True(Watchlist.Contains(new[] { Entry("jellyfin:ABC", 1) }, Server("abc", "Ran")));
        }

        // ---- the shelf -------------------------------------------------------------------------

        [Fact]
        public void The_shelf_shows_what_is_on_the_list_most_recently_added_first()
        {
            var ran = Local(1, "Ran");
            var heat = Server("h", "Heat");
            var alien = Local(3, "Alien");
            var notOnIt = Local(4, "Brazil");

            var entries = new[]
            {
                Entry("local:1", 1),
                Entry("jellyfin:h", 3),
                Entry("local:3", 2)
            };

            var shelf = Watchlist.Build(new[] { ran, heat, alien, notOnIt }, entries);

            Assert.Equal(new[] { "Heat", "Alien", "Ran" }, shelf.Select(m => m.Title));
        }

        [Fact]
        public void A_film_appears_once_however_many_of_its_keys_are_on_the_list()
        {
            var film = Local(7, "Ran", remoteId: "abc");

            var shelf = Watchlist.Build(
                new[] { film, film },
                new[] { Entry("local:7", 1), Entry("jellyfin:abc", 2) });

            Assert.Single(shelf);
        }

        [Fact]
        public void An_entry_the_library_cannot_place_is_not_shown()
        {
            var shelf = Watchlist.Build(new[] { Local(1, "Ran") }, new[] { Entry("jellyfin:gone", 1) });

            Assert.Empty(shelf);
        }

        [Fact]
        public void An_empty_list_or_library_is_an_empty_shelf()
        {
            Assert.Empty(Watchlist.Build(null, new[] { Entry("local:1", 1) }));
            Assert.Empty(Watchlist.Build(new[] { Local(1, "Ran") }, null));
            Assert.Empty(Watchlist.Build(new[] { Local(1, "Ran") }, Array.Empty<WatchlistEntry>()));
        }

        [Fact]
        public void The_watchlist_shelf_sits_below_continue_watching_and_above_every_genre()
        {
            var resumed = Local(1, "Ran");
            var wanted = Local(2, "Heat");

            var shelves = LibraryGrouping.BuildShelves(
                new[] { resumed, wanted },
                new[] { "Drama" },
                continueWatching: new[] { resumed },
                watchlist: new[] { wanted });

            Assert.Equal(new[] { ResumeRow.Heading, Watchlist.Heading, "Drama" }, shelves.Select(s => s.Name));
            Assert.Equal(1, shelves[1].Count);
            Assert.Same(wanted, Assert.Single(shelves[1].Items));
        }

        [Fact]
        public void An_empty_watchlist_is_no_shelf_at_all()
        {
            var shelves = LibraryGrouping.BuildShelves(
                new[] { Local(1, "Ran") },
                new[] { "Drama" },
                watchlist: Array.Empty<UiMovie>());

            Assert.DoesNotContain(shelves, s => s.Name == Watchlist.Heading);
        }

        [Fact]
        public void The_button_says_which_list_the_film_is_on()
        {
            Assert.Equal("+ Watchlist", Watchlist.ButtonLabel(false));
            Assert.Equal("\u2713 On Watchlist", Watchlist.ButtonLabel(true));
        }

        // ---- on disk ---------------------------------------------------------------------------

        [Fact]
        public void A_film_added_is_still_there_when_the_catalogue_is_reopened()
        {
            var at = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

            using (var conn = Database.Open(_dbPath))
                WatchlistStore.Add(conn, new[] { "local:7" }, at);

            using (var conn = Database.Open(_dbPath))
            {
                var entry = Assert.Single(WatchlistStore.Load(conn));
                Assert.Equal("local:7", entry.ItemKey);
                Assert.Equal(at, entry.AddedAt);
            }
        }

        [Fact]
        public void Adding_a_film_already_on_the_list_neither_duplicates_it_nor_moves_it()
        {
            var first = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

            using var conn = Database.Open(_dbPath);
            WatchlistStore.Add(conn, new[] { "jellyfin:abc" }, first);

            // The same film, downloaded since, added again from its new card.
            WatchlistStore.Add(conn, new[] { "local:7", "jellyfin:ABC" }, first.AddDays(5));

            var entry = Assert.Single(WatchlistStore.Load(conn));
            Assert.Equal("jellyfin:abc", entry.ItemKey);
            Assert.Equal(first, entry.AddedAt);
        }

        [Fact]
        public void Removing_a_film_removes_it_under_every_key_it_answers_to()
        {
            using var conn = Database.Open(_dbPath);
            WatchlistStore.Add(conn, new[] { "jellyfin:abc" });
            WatchlistStore.Add(conn, new[] { "local:9" });

            WatchlistStore.Remove(conn, new[] { "local:7", "jellyfin:ABC" });

            Assert.Equal(new[] { "local:9" }, WatchlistStore.Load(conn).Select(e => e.ItemKey));
        }

        [Fact]
        public void Set_adds_and_removes()
        {
            using var conn = Database.Open(_dbPath);

            WatchlistStore.Set(conn, new[] { "local:1" }, onWatchlist: true);
            Assert.True(Watchlist.Contains(WatchlistStore.Load(conn), new[] { "local:1" }));

            WatchlistStore.Set(conn, new[] { "local:1" }, onWatchlist: false);
            Assert.Empty(WatchlistStore.Load(conn));
        }

        [Fact]
        public void Nothing_with_no_key_is_written()
        {
            using var conn = Database.Open(_dbPath);

            WatchlistStore.Add(conn, Array.Empty<string>());
            WatchlistStore.Add(conn, new[] { " ", "" });
            WatchlistStore.Add(conn, null);

            Assert.Empty(WatchlistStore.Load(conn));
        }

        [Fact]
        public void A_library_from_before_the_watchlist_gains_the_table_and_keeps_its_films()
        {
            using (var old = new SqliteConnection($"Data Source={_dbPath}"))
            {
                old.Open();
                using var cmd = old.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE movies (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    title       TEXT NOT NULL,
    year        INTEGER,
    genres      TEXT,
    poster_path TEXT
);
INSERT INTO movies (title, year) VALUES ('Ran', 1985);";
                cmd.ExecuteNonQuery();
            }

            using var conn = Database.Open(_dbPath);

            Assert.True(Database.TableExists(conn, "watchlist"));
            Assert.Empty(WatchlistStore.Load(conn));

            WatchlistStore.Add(conn, new[] { "local:1" });
            Assert.Single(WatchlistStore.Load(conn));

            using var read = conn.CreateCommand();
            read.CommandText = "SELECT title FROM movies WHERE id = 1";
            Assert.Equal("Ran", read.ExecuteScalar() as string);
        }

        // ---- re-keying onto the catalogue -------------------------------------------------------

        [Fact]
        public void A_downloaded_film_with_no_server_link_is_still_on_the_watchlist()
        {
            var added = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

            using var conn = Database.Open(_dbPath);

            // Added while only the server had it, then downloaded and catalogued as row 42.
            WatchlistStore.Add(conn, Watchlist.KeysFor(Server("abc", "Ran")), added);
            Assert.True(WatchlistStore.AdoptDownload(conn, "abc", 42));

            // Later the server is switched off, or the match breaks: the card has no RemoteId.
            var stranded = Local(42, "Ran");
            var entries = WatchlistStore.Load(conn);

            Assert.True(Watchlist.Contains(entries, stranded));
            Assert.Same(stranded, Assert.Single(Watchlist.Build(new[] { stranded }, entries)));
            Assert.Equal(added, Assert.Single(entries).AddedAt);
        }

        [Fact]
        public void An_entry_left_under_a_server_key_is_lost_to_a_film_with_no_server_link()
        {
            // The failure the re-keying exists for: left under jellyfin:abc, nothing links local:42.
            var entries = new[] { Entry("jellyfin:abc", 1) };

            Assert.Empty(Watchlist.Build(new[] { Local(42, "Ran") }, entries));
        }

        [Fact]
        public void A_film_matched_through_its_server_key_is_moved_onto_its_catalogue_row()
        {
            var linked = Local(42, "Ran", remoteId: "ABC");
            var plain = Local(7, "Heat");
            var serverOnly = Server("xyz", "Alien");

            var entries = new[] { Entry("jellyfin:abc", 1), Entry("local:7", 2), Entry("jellyfin:xyz", 3) };

            var moves = Watchlist.Adoptions(new[] { linked, plain, serverOnly, linked }, entries);

            Assert.Equal(new[] { ("jellyfin:ABC", "local:42") }, moves);
        }

        [Fact]
        public void Once_moved_the_film_comes_off_the_list_even_with_the_match_broken()
        {
            using var conn = Database.Open(_dbPath);
            WatchlistStore.Add(conn, new[] { "jellyfin:abc" });

            var moves = Watchlist.Adoptions(new[] { Local(42, "Ran", remoteId: "abc") }, WatchlistStore.Load(conn));
            Assert.Equal(1, WatchlistStore.Adopt(conn, moves));

            // Removed from a card that no longer knows its server id.
            WatchlistStore.Remove(conn, Watchlist.KeysFor(Local(42, "Ran")));

            Assert.Empty(WatchlistStore.Load(conn));
        }

        [Fact]
        public void Moving_onto_a_row_already_on_the_list_keeps_one_entry_and_the_earlier_date()
        {
            var earlier = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

            using var conn = Database.Open(_dbPath);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO watchlist (item_key, added_at) VALUES ('jellyfin:abc', '2026-10-01T00:00:00.0000000Z');
INSERT INTO watchlist (item_key, added_at) VALUES ('local:42', '2026-10-05T00:00:00.0000000Z');";
                cmd.ExecuteNonQuery();
            }

            Assert.True(WatchlistStore.Rekey(conn, "jellyfin:abc", "local:42"));

            var entry = Assert.Single(WatchlistStore.Load(conn));
            Assert.Equal("local:42", entry.ItemKey);
            Assert.Equal(earlier, entry.AddedAt);
        }

        [Fact]
        public void Re_keying_something_not_on_the_list_changes_nothing()
        {
            using var conn = Database.Open(_dbPath);
            WatchlistStore.Add(conn, new[] { "local:1" });

            Assert.False(WatchlistStore.Rekey(conn, "jellyfin:nope", "local:2"));
            Assert.False(WatchlistStore.AdoptDownload(conn, null, 2));
            Assert.False(WatchlistStore.AdoptDownload(conn, "abc", 0));

            Assert.Equal(new[] { "local:1" }, WatchlistStore.Load(conn).Select(e => e.ItemKey));
        }

        // ---- the markup ------------------------------------------------------------------------

        [Fact]
        public void The_card_menu_and_the_details_screen_both_offer_the_watchlist()
        {
            var views = Path.Combine(ShelfNavigationMarkupTests.AppSource(), "Views");

            var main = XDocument.Load(Path.Combine(views, "MainWindow.axaml"));
            var item = Assert.Single(main.Descendants(), e =>
                e.Name.LocalName == "MenuItem" && (string?)e.Attribute("Name") == "WatchlistItem");
            Assert.Equal("ToggleWatchlist_Click", (string?)item.Attribute("Click"));

            Assert.Single(main.Descendants(), e =>
                e.Name.LocalName == "MenuItem" && (string?)e.Attribute("Name") == "DismissFromRowItem");

            var details = XDocument.Load(Path.Combine(views, "MovieDetailsView.axaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var button = Assert.Single(details.Descendants(), e =>
                e.Name.LocalName == "Button" && (string?)e.Attribute(x + "Name") == "WatchlistButton");
            Assert.Equal("Watchlist_Click", (string?)button.Attribute("Click"));
        }
    }
}
