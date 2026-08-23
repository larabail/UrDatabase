using System.Collections.Generic;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// What a film in both places is described by.
    ///
    /// Folding the two cards into one made this necessary rather than optional: the server's own
    /// description used to be a click away on a second card, and on an install with no TMDB key it
    /// is the only description there is.
    /// </summary>
    public class ServerDetailsTests
    {
        private static JellyfinMovie Server() => new()
        {
            ItemId = "a",
            Title = "A Wholly Invented Film",
            Year = 1999,
            Genres = "Drama, Crime",
            Overview = "What the server says it is about.",
            RuntimeMinutes = 121,
            CommunityRating = 7.4,
            ImdbId = "tt0000001",
            Cast = new List<string> { "An Actor (A Part)" },
            Crew = new List<string> { "Director: A Name" }
        };

        [Fact]
        public void A_film_TMDB_knew_nothing_about_is_described_by_the_server()
        {
            var vm = new MovieDetailsVm { Title = "A Wholly Invented Film", Year = 1999 };

            ServerDetails.FillGaps(vm, Server(), id => $"http://media.invalid/{id}/backdrop");

            Assert.Equal("What the server says it is about.", vm.Overview);
            Assert.Equal("Drama, Crime", vm.Genres);
            Assert.Equal(121, vm.Runtime);
            Assert.Equal("tt0000001", vm.ImdbId);
            Assert.Equal(7.4, vm.CommunityRating);
            Assert.Equal(new[] { "An Actor (A Part)" }, vm.TopCast);
            Assert.Equal(new[] { "Director: A Name" }, vm.KeyCrew);
            Assert.Equal("http://media.invalid/a/backdrop", vm.BackdropUrl);
        }

        /// <summary>
        /// The local half is the half a person can correct. "Wrong film?" rewrites it, and a
        /// server overview quietly landing on top would make the correction look like it had not
        /// taken.
        /// </summary>
        [Fact]
        public void Nothing_that_was_already_answered_is_overwritten()
        {
            var vm = new MovieDetailsVm
            {
                Overview = "What TMDB says it is about.",
                Genres = "Comedy",
                Runtime = 98,
                ImdbId = "tt0000002",
                BackdropUrl = "http://tmdb.invalid/backdrop.jpg",
                TopCast = new List<string> { "Somebody Else (A Part)" },
                KeyCrew = new List<string> { "Director: Somebody Else" }
            };

            ServerDetails.FillGaps(vm, Server(), _ => "http://media.invalid/backdrop");

            Assert.Equal("What TMDB says it is about.", vm.Overview);
            Assert.Equal("Comedy", vm.Genres);
            Assert.Equal(98, vm.Runtime);
            Assert.Equal("tt0000002", vm.ImdbId);
            Assert.Equal("http://tmdb.invalid/backdrop.jpg", vm.BackdropUrl);
            Assert.Equal(new[] { "Somebody Else (A Part)" }, vm.TopCast);
            Assert.Equal(new[] { "Director: Somebody Else" }, vm.KeyCrew);

            // Except the one number nothing else in the app produces, which is printed under
            // Jellyfin's own name rather than standing in for anybody else's.
            Assert.Equal(7.4, vm.CommunityRating);
        }

        [Fact]
        public void A_runtime_of_zero_counts_as_no_runtime()
        {
            var vm = new MovieDetailsVm { Runtime = 0 };

            ServerDetails.FillGaps(vm, Server());

            Assert.Equal(121, vm.Runtime);
        }

        [Fact]
        public void A_film_no_server_has_is_left_exactly_as_it_was()
        {
            var vm = new MovieDetailsVm { Title = "A Local Film", Overview = "" };

            ServerDetails.FillGaps(vm, null);

            Assert.Equal("", vm.Overview);
            Assert.Null(vm.CommunityRating);
            Assert.Empty(vm.TopCast);
        }

        [Fact]
        public void A_backdrop_is_not_invented_when_the_caller_cannot_build_one()
        {
            // The server may be configured away between the sync that cached this film and the
            // click that opens it, leaving nothing to build a URL with.
            var vm = new MovieDetailsVm();

            ServerDetails.FillGaps(vm, Server(), backdropUrl: null);

            Assert.Null(vm.BackdropUrl);
        }

        /// <summary>
        /// The whole reason this exists: the film is opened as a local one, so it keeps the local
        /// behaviour, and the facts row still says the server has it.
        /// </summary>
        [Fact]
        public void The_film_is_still_played_from_disk_and_still_says_where_it_is()
        {
            var vm = new MovieDetailsVm
            {
                LocalId = 3,
                FilePath = "/films/a-wholly-invented-film.mkv",
                FileMatch = PlayTargetKind.Linked,
                IsOnServer = true
            };

            ServerDetails.FillGaps(vm, Server());

            Assert.False(vm.IsRemote);
            Assert.True(vm.HasFile);
            Assert.Equal(PlayTargetKind.Linked, vm.FileMatch);
            Assert.Contains(DetailFacts.For(vm), f => f.Label == "WHERE");
        }
    }
}
