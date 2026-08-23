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
    /// Typing in the search box, end to end: a real catalogue, the real loader, and the coordinator
    /// that decides what reaches the screen. The window itself is the only piece left out, and it
    /// does nothing here but copy the winning result into its collections.
    ///
    /// The point of running these against SQLite rather than a stub is that the timing that causes
    /// the bug is a property of the query. "ma" matches most of a library and "matrix" matches
    /// three films, so the broad search is the slow one, and the broad search is the one the user
    /// has already moved on from.
    /// </summary>
    public class LibrarySearchTests : IDisposable
    {
        private readonly string _root;
        private readonly string _dbPath;

        public LibrarySearchTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-search-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _dbPath = Path.Combine(_root, "movies.db");
            SeedLibrary();
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public async Task The_library_settles_on_the_last_word_typed_not_the_last_query_to_finish()
        {
            var loader = new LibraryLoader(new MovieRepository(_dbPath));
            var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shown = new List<LibraryView>();

            using var coordinator = new SearchCoordinator<LibraryView>(
                run: async (query, ct) =>
                {
                    // CancellationToken.None on purpose. SQLite cannot interrupt a statement that
                    // has already started, so a superseded read really does come back holding its
                    // rows, and the coordinator has to refuse them on its own.
                    var view = await loader.LoadAsync(query, null, CancellationToken.None);
                    if (query == "ma") await slow.Task;
                    return view;
                },
                apply: (_, view) => shown.Add(view),
                delay: NoWait);

            var broad = coordinator.PostAsync("ma");
            var narrow = coordinator.PostAsync("matrix");

            await narrow;
            slow.SetResult();
            await broad;

            var settled = Assert.Single(shown);
            Assert.Equal("matrix", settled.Query);
            Assert.Equal(
                new[] { "The Matrix Reloaded", "The Matrix Revolutions", "The Matrix" },
                settled.All.Select(m => m.Title));
        }

        [Fact]
        public async Task Typing_a_word_queries_the_catalogue_once()
        {
            var loader = new LibraryLoader(new MovieRepository(_dbPath));
            var debounce = new ManualDebounce();
            var queried = new List<string?>();
            var shown = new List<LibraryView>();

            using var coordinator = new SearchCoordinator<LibraryView>(
                run: (query, ct) =>
                {
                    lock (queried) queried.Add(query);
                    return loader.LoadAsync(query, null, ct);
                },
                apply: (_, view) => shown.Add(view),
                delay: debounce.Wait);

            var typing = new[] { "m", "ma", "mat", "matr", "matri", "matrix" }
                .Select(coordinator.PostAsync)
                .ToArray();

            debounce.ReleaseAll();
            await Task.WhenAll(typing);

            Assert.Equal(new string?[] { "matrix" }, queried);
            Assert.Equal("matrix", Assert.Single(shown).Query);
        }

        [Fact]
        public async Task Backspacing_the_box_empty_puts_the_whole_library_back()
        {
            var loader = new LibraryLoader(new MovieRepository(_dbPath));
            var shown = new List<LibraryView>();

            using var coordinator = new SearchCoordinator<LibraryView>(
                run: (query, ct) => loader.LoadAsync(query, null, ct),
                apply: (_, view) => shown.Add(view),
                delay: NoWait);

            await coordinator.PostAsync("matrix");
            await coordinator.PostAsync(null);

            var settled = shown.Last();
            Assert.False(settled.IsSearch);
            Assert.Equal(6, settled.All.Count);
        }

        [Fact]
        public async Task A_search_in_flight_when_the_window_closes_is_dropped()
        {
            var loader = new LibraryLoader(new MovieRepository(_dbPath));
            var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shown = new List<LibraryView>();

            // The window's own _cts, cancelled on Closed.
            using var window = new CancellationTokenSource();

            using var coordinator = new SearchCoordinator<LibraryView>(
                run: async (query, ct) =>
                {
                    var view = await loader.LoadAsync(query, null, CancellationToken.None);
                    await slow.Task;
                    return view;
                },
                apply: (_, view) => shown.Add(view),
                lifetime: window.Token,
                delay: NoWait);

            var search = coordinator.PostAsync("matrix");

            window.Cancel();
            slow.SetResult();
            await search;

            Assert.Empty(shown);
        }

        [Fact]
        public async Task Choosing_a_source_while_searching_narrows_the_results_rather_than_discarding_them()
        {
            var screen = new LibraryScreen();
            using var coordinator = NewCoordinator(screen);

            await coordinator.PostAsync("ma");
            Assert.Contains(screen.Shown, m => m.IsRemote);
            Assert.Contains(screen.Shown, m => !m.IsRemote);

            screen.ChooseSource(LibrarySource.ThisComputer);

            // The search survives the click. It used to be thrown away, and the shelves came back.
            Assert.Equal("ma", screen.View.Query);
            Assert.All(screen.Shown, m => Assert.False(m.IsRemote));
            Assert.Equal(
                new[] { "Mamma Mia!", "The Matrix Reloaded", "The Matrix Revolutions", "The Matrix", "Manhattan" },
                screen.Shown.Select(m => m.Title));
        }

        [Fact]
        public async Task A_search_typed_while_a_source_is_chosen_respects_both()
        {
            var screen = new LibraryScreen();
            using var coordinator = NewCoordinator(screen);

            screen.ChooseSource(LibrarySource.Server);
            await coordinator.PostAsync("ma");

            // The source survives the typing, the same way the search survives the click.
            Assert.Equal(LibrarySource.Server, screen.Source);
            Assert.NotEmpty(screen.Shown);
            Assert.All(screen.Shown, m => Assert.True(m.IsRemote));
            Assert.Equal(new[] { "Mad Max", "The Matrix" }, screen.Shown.Select(m => m.Title).OrderBy(t => t));
        }

        [Fact]
        public async Task A_stale_search_cannot_undo_a_source_chosen_while_it_was_running()
        {
            var loader = new LibraryLoader(new MovieRepository(_dbPath));
            var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var screen = new LibraryScreen();

            using var coordinator = new SearchCoordinator<LibraryView>(
                run: async (query, ct) =>
                {
                    var view = await loader.LoadAsync(query, Remote, CancellationToken.None);
                    if (query == "ma") await slow.Task;
                    return view;
                },
                apply: screen.Apply,
                delay: NoWait);

            var broad = coordinator.PostAsync("ma");

            // Both inputs change while the broad search is still running.
            screen.ChooseSource(LibrarySource.Server);
            var narrow = coordinator.PostAsync("matrix");
            await narrow;

            slow.SetResult();
            await broad;

            // Both survive: the newest query, seen from the place chosen while it was in flight.
            // The stale result cannot undo either, because it only ever supplies the library and
            // the source filter is re-derived after it lands.
            Assert.Equal("matrix", screen.View.Query);
            Assert.Equal(LibrarySource.Server, screen.Source);
            Assert.Equal(new[] { "The Matrix" }, screen.Shown.Select(m => m.Title));
        }

        private SearchCoordinator<LibraryView> NewCoordinator(LibraryScreen screen)
        {
            var loader = new LibraryLoader(new MovieRepository(_dbPath));

            return new SearchCoordinator<LibraryView>(
                run: (query, ct) => loader.LoadAsync(query, Remote, ct),
                apply: screen.Apply,
                delay: NoWait);
        }

        private static readonly UiMovie[] Remote =
        {
            JellyfinLibrary.ToUiMovie(new JellyfinMovie { ItemId = "mad-max", Title = "Mad Max", Year = 1979 }),
            JellyfinLibrary.ToUiMovie(new JellyfinMovie { ItemId = "matrix-99", Title = "The Matrix", Year = 1999 }),
        };

        /// <summary>
        /// What <c>MainWindow.ApplyLibrary</c> and <c>ShowSearchResults</c> do between them, with
        /// the window left out because a window needs a UI thread to exist.
        ///
        /// The property being asserted is structural rather than a matter of timing: a search
        /// result supplies the library and nothing else, and the source filter is re-derived from
        /// the field afterwards. Written this way a stale result cannot undo a source change even
        /// in principle. Applying the filter inside the background read would look equivalent and
        /// would reintroduce exactly that race, which is what these tests exist to catch.
        /// </summary>
        private sealed class LibraryScreen
        {
            private IReadOnlyList<UiMovie> _all = Array.Empty<UiMovie>();

            public LibraryView View { get; private set; } = LibraryView.Empty;
            public LibrarySource Source { get; private set; } = LibrarySource.Everywhere;
            public IReadOnlyList<UiMovie> Shown { get; private set; } = Array.Empty<UiMovie>();

            public void Apply(string? query, LibraryView view)
            {
                View = view;
                _all = view.All;
                Render();
            }

            public void ChooseSource(LibrarySource source)
            {
                Source = source;
                Render();
            }

            private void Render() => Shown = LibraryFilter.Apply(_all, Source);
        }

        private static Task NoWait(TimeSpan wait, CancellationToken ct) => Task.CompletedTask;

        private void SeedLibrary()
        {
            using var conn = Database.Open(_dbPath);

            foreach (var (title, year) in new (string, int?)[]
                     {
                         ("The Matrix", 1999),
                         ("The Matrix Reloaded", 2003),
                         ("The Matrix Revolutions", 2003),
                         ("Mamma Mia!", 2008),
                         ("Manhattan", 1979),
                         ("Heat", 1995),
                     })
            {
                conn.Execute("INSERT INTO movies (title, year) VALUES (@title, @year)", new { title, year });
            }
        }
    }
}
