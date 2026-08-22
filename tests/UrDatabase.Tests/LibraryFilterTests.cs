using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Filtering the library by where its films actually are.
    ///
    /// This exists because genre alone could not answer "what is on this disk". A scanned film has
    /// no genre until something enriches it, so every local film lands in the Uncategorised
    /// bucket, which sorts last — behind every genre a server library brought with it. On a real
    /// library of 396 server films and 3 local ones, the three were the twenty-first chip in a
    /// scrolling row and the last shelf on the page: rendered, and unreachable.
    /// </summary>
    public class LibraryFilterTests
    {
        private static UiMovie Local(long id, string title) =>
            new() { Id = id, Title = title };

        private static UiMovie Remote(string remoteId, string title) =>
            new() { Title = title, Source = MovieSource.Jellyfin, RemoteId = remoteId };

        private static readonly UiMovie[] Mixed =
        {
            Local(1, "Toy Story 5"),
            Local(2, "El Drama"),
            Remote("a", "Ran"),
            Remote("b", "Solaris"),
            Remote("c", "Stalker"),
        };

        [Fact]
        public void Everywhere_keeps_the_whole_library()
        {
            Assert.Equal(5, LibraryFilter.Apply(Mixed, LibrarySource.Everywhere).Count);
        }

        [Fact]
        public void This_computer_keeps_only_the_films_with_a_file_here()
        {
            var local = LibraryFilter.Apply(Mixed, LibrarySource.ThisComputer);

            Assert.Equal(new[] { "Toy Story 5", "El Drama" }, local.Select(m => m.Title));
            Assert.All(local, m => Assert.False(m.IsRemote));
        }

        [Fact]
        public void The_server_keeps_only_the_films_that_need_it()
        {
            var remote = LibraryFilter.Apply(Mixed, LibrarySource.Server);

            Assert.Equal(3, remote.Count);
            Assert.All(remote, m => Assert.True(m.IsRemote));
        }

        /// <summary>
        /// Every server film carries local id 0, so counting on the id alone reports the whole
        /// remote library as a single film.
        /// </summary>
        [Fact]
        public void Counting_the_server_does_not_collapse_it_to_one_film()
        {
            Assert.Equal(3, LibraryFilter.Count(Mixed, LibrarySource.Server));
            Assert.Equal(2, LibraryFilter.Count(Mixed, LibrarySource.ThisComputer));
            Assert.Equal(5, LibraryFilter.Count(Mixed, LibrarySource.Everywhere));
        }

        /// <summary>
        /// The row is worth its space only when there is a choice to make. An install with no
        /// server must not carry a permanent, empty "On the server" control.
        /// </summary>
        [Fact]
        public void The_row_is_offered_only_when_films_come_from_more_than_one_place()
        {
            Assert.Equal(3, LibraryFilter.Available(Mixed).Count);

            Assert.Empty(LibraryFilter.Available(new[] { Local(1, "Toy Story 5") }));
            Assert.Empty(LibraryFilter.Available(new[] { Remote("a", "Ran") }));
            Assert.Empty(LibraryFilter.Available(System.Array.Empty<UiMovie>()));
            Assert.Empty(LibraryFilter.Available(null));
        }

        [Fact]
        public void The_row_leads_with_everywhere()
        {
            Assert.Equal(LibrarySource.Everywhere, LibraryFilter.Available(Mixed)[0]);
        }

        [Fact]
        public void A_missing_library_filters_to_nothing_rather_than_throwing()
        {
            Assert.Empty(LibraryFilter.Apply(null, LibrarySource.ThisComputer));
            Assert.Equal(0, LibraryFilter.Count(null, LibrarySource.Everywhere));
        }

        [Fact]
        public void Every_source_has_a_name_fit_to_put_on_a_control()
        {
            Assert.Equal("Everywhere", LibraryFilter.Label(LibrarySource.Everywhere));
            Assert.Equal("On this computer", LibraryFilter.Label(LibrarySource.ThisComputer));
            Assert.Equal("On the server", LibraryFilter.Label(LibrarySource.Server));
        }

        /// <summary>
        /// The point of the whole feature: three local films with no genres at all are one click
        /// away, rather than behind twenty genres they do not belong to.
        /// </summary>
        [Fact]
        public void Local_films_with_no_genres_are_reachable_without_touching_a_genre()
        {
            var library = new[]
            {
                Local(1, "Toy Story 5"),
                Local(2, "El Drama"),
                Local(3, "Minions & Monsters"),
            }.Concat(Enumerable.Range(0, 396).Select(i => new UiMovie
            {
                Title = $"Server film {i}",
                Source = MovieSource.Jellyfin,
                RemoteId = i.ToString(),
                Genres = "Drama, Thriller"
            })).ToList();

            // Every local film is uncategorised, and the bucket sorts last.
            var chips = LibraryGrouping.BuildGenreChips(library);
            Assert.Equal(LibraryGrouping.Uncategorised, chips[^1].Name);

            // The source filter reaches them regardless.
            var local = LibraryFilter.Apply(library, LibrarySource.ThisComputer);

            Assert.Equal(3, local.Count);
            Assert.All(local, m => Assert.True(LibraryGrouping.HasNoGenre(m)));
        }

        /// <summary>
        /// Once the local films are the only ones showing, the genre row has to describe them and
        /// not the library they were filtered out of — otherwise it offers twenty genres that
        /// select nothing.
        /// </summary>
        [Fact]
        public void The_genre_row_describes_the_filtered_library_and_not_the_whole_one()
        {
            var library = new[]
            {
                Local(1, "Toy Story 5"),
                new UiMovie { Title = "Ran", Source = MovieSource.Jellyfin, RemoteId = "a", Genres = "Drama, War" },
            };

            var everywhere = LibraryGrouping.BuildGenreChips(library).Select(c => c.Name).ToList();
            Assert.Contains("Drama", everywhere);

            var justLocal = LibraryGrouping
                .BuildGenreChips(LibraryFilter.Apply(library, LibrarySource.ThisComputer))
                .Select(c => c.Name)
                .ToList();

            Assert.DoesNotContain("Drama", justLocal);
            Assert.Contains(LibraryGrouping.Uncategorised, justLocal);
        }
    }
}
