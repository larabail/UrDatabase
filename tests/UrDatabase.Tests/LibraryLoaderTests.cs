using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// One library read, against a real catalogue in a temporary directory.
    ///
    /// SQLite is cheap enough to test for real, and the interesting cases here are all about what
    /// the database actually does — an FTS prefix match, a table an older build never created —
    /// which a mock would only assert back at itself.
    /// </summary>
    public class LibraryLoaderTests : IDisposable
    {
        private readonly string _root;
        private readonly string _dbPath;

        public LibraryLoaderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-loader-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _dbPath = Path.Combine(_root, "movies.db");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void The_whole_catalogue_comes_back_when_nothing_was_typed()
        {
            SeedLibrary();

            var view = NewLoader().Load(null);

            Assert.False(view.IsSearch);
            Assert.Equal(
                new[] { "Mamma Mia!", "The Matrix Reloaded", "The Matrix Revolutions", "The Matrix", "Heat", "Manhattan" },
                view.All.Select(m => m.Title));
        }

        [Fact]
        public void A_query_narrows_the_catalogue_to_what_was_typed()
        {
            SeedLibrary();

            var view = NewLoader().Load("matrix");

            Assert.True(view.IsSearch);
            Assert.Equal("matrix", view.Query);
            Assert.Equal(
                new[] { "The Matrix Reloaded", "The Matrix Revolutions", "The Matrix" },
                view.All.Select(m => m.Title));
        }

        [Fact]
        public void A_half_typed_word_matches_on_its_prefix()
        {
            SeedLibrary();

            // What the search box is looking at most of the time: a word still being typed.
            var view = NewLoader().Load("ma");

            Assert.Equal(5, view.All.Count);
            Assert.DoesNotContain(view.All, m => m.Title == "Heat");
        }

        [Fact]
        public void Text_with_no_word_in_it_is_still_a_search()
        {
            SeedLibrary();

            // "???" has nothing FTS5 can match on, so the whole library comes back — but the
            // person typing it is mid-word and must not be thrown back into the grouped view.
            var view = NewLoader().Load("???");

            Assert.True(view.IsSearch);
            Assert.Equal(6, view.All.Count);
        }

        [Fact]
        public void Punctuation_in_a_title_is_searched_for_rather_than_obeyed()
        {
            SeedLibrary();

            // FTS5 reads a bare colon or slash as an operator. Nothing here should raise.
            foreach (var typed in new[] { "Mission: Impossible", "Face/Off", "Dude, Where's My Car?", "matrix (" })
            {
                var view = NewLoader().Load(typed);
                Assert.StartsWith("Posters present:", view.Status);
            }
        }

        [Fact]
        public void A_catalogue_that_cannot_be_read_is_reported_rather_than_thrown()
        {
            // A file that is a database but has none of the tables the query needs, which is what
            // a catalogue from an older build looks like.
            using (var conn = Database.Connect(_dbPath)) { }

            var failures = new List<Exception>();
            var view = new LibraryLoader(new MovieRepository(_dbPath), failures.Add).Load("matrix");

            Assert.Single(failures);
            Assert.Empty(view.Local);
            Assert.StartsWith("Could not read the library:", view.Status);
        }

        [Fact]
        public void A_server_library_is_still_browsable_with_no_catalogue_at_all()
        {
            var remote = new[] { Server("solaris", "Solaris", 1972), Server("stalker", "Stalker", 1979) };

            var view = NewLoader().Load(null, remote);

            Assert.False(File.Exists(_dbPath));
            Assert.Empty(view.Local);
            Assert.Equal(new[] { "Stalker", "Solaris" }, view.All.Select(m => m.Title));
            Assert.Contains("2 films on the Jellyfin server", view.Status);
        }

        [Fact]
        public void A_search_narrows_the_server_half_too()
        {
            SeedLibrary();
            var remote = new[] { Server("mad-max", "Mad Max", 1979), Server("stalker", "Stalker", 1979) };

            var view = NewLoader().Load("ma", remote);

            Assert.Single(view.Remote);
            Assert.Equal("Mad Max", view.Remote[0].Title);
            Assert.DoesNotContain(view.All, m => m.Title == "Stalker");
        }

        [Fact]
        public void A_local_film_and_a_server_copy_of_it_are_one_card()
        {
            SeedLibrary();
            var remote = new[] { Server("the-matrix", "The Matrix", 1999) };

            var view = NewLoader().Load("matrix", remote);

            // One film, in two places, carrying both facts — rather than two identical posters
            // with nothing to tell them apart until you click one.
            var matrix = Assert.Single(view.All, m => m.Title == "The Matrix");
            Assert.True(matrix.IsInBothPlaces);
            Assert.Equal("the-matrix", matrix.RemoteId);

            // Both halves are still reported as they were found, because the status line counts
            // what each library holds and not what the wall does with them.
            Assert.Single(view.Remote);
            Assert.Contains(view.Local, m => m.Title == "The Matrix");
        }

        [Fact]
        public void The_status_line_counts_the_posters_that_arrived()
        {
            SeedLibrary();

            var view = NewLoader().Load(null);

            Assert.Equal("Posters present: 1/6", view.Status);
        }

        [Fact]
        public void An_empty_catalogue_says_where_it_would_have_been()
        {
            var view = NewLoader().Load(null);

            Assert.Equal($"No library yet. Expected a database at {_dbPath}.", view.Status);
        }

        [Fact]
        public async Task The_read_happens_off_the_calling_thread()
        {
            // The point of the whole change: this used to run on the UI thread on every keystroke.
            // Proved through the failure callback, which runs on whichever thread did the read.
            using (var conn = Database.Connect(_dbPath)) { }

            var caller = Environment.CurrentManagedThreadId;
            var worker = 0;
            using var read = new ManualResetEventSlim();

            var loader = new LibraryLoader(
                new MovieRepository(_dbPath),
                _ => { worker = Environment.CurrentManagedThreadId; read.Set(); });

            var loading = loader.LoadAsync(null);

            // A blocking wait rather than an await, so the calling thread is provably occupied for
            // the whole of the read and cannot be the thread that performed it.
            Assert.True(read.Wait(TimeSpan.FromSeconds(30)));
            Assert.NotEqual(caller, worker);

            await loading;
        }

        [Fact]
        public async Task A_read_cancelled_before_it_starts_never_touches_the_database()
        {
            SeedLibrary();

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => NewLoader().LoadAsync("matrix", null, cancelled.Token));
        }

        private LibraryLoader NewLoader() => new LibraryLoader(new MovieRepository(_dbPath));

        private static UiMovie Server(string itemId, string title, int? year) =>
            JellyfinLibrary.ToUiMovie(new JellyfinMovie { ItemId = itemId, Title = title, Year = year });

        private void SeedLibrary()
        {
            using var conn = Database.Open(_dbPath);

            foreach (var (title, year, poster) in new (string, int?, string?)[]
                     {
                         ("The Matrix", 1999, "/tmp/the-matrix.jpg"),
                         ("The Matrix Reloaded", 2003, null),
                         ("The Matrix Revolutions", 2003, null),
                         ("Mamma Mia!", 2008, null),
                         ("Manhattan", 1979, null),
                         ("Heat", 1995, null),
                     })
            {
                conn.Execute(
                    "INSERT INTO movies (title, year, poster_path) VALUES (@title, @year, @poster)",
                    new { title, year, poster });
            }
        }
    }
}
