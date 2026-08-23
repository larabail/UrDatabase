using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The one door into playing something from the server, and the guarantee that going through
    /// it always starts the reporting.
    /// </summary>
    public class StreamPlaybackTests : IDisposable
    {
        // Following a launch is a task nobody awaits, and it logs when the interface it was
        // pointed at is not there — which in a test it never is. Redirected for the same reason
        // every other class here is: the real log belongs to whoever ran the suite.
        private readonly TempLog _log = new();

        public void Dispose() => _log.Dispose();

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

        /// <summary>
        /// A launcher that records what it was asked for and hands back a VLC with a control
        /// interface, without starting anything.
        /// </summary>
        private sealed class RecordingLauncher
        {
            public string? Url { get; private set; }
            public bool WithProgressReporting { get; private set; }
            public long StartTicks { get; private set; }
            public int Launches { get; private set; }

            public MediaPlayerLauncher.LaunchedPlayer Launch(string url, bool withProgressReporting, long startTicks)
            {
                Url = url;
                WithProgressReporting = withProgressReporting;
                StartTicks = startTicks;
                Launches++;

                var vlc = new MediaPlayerLauncher.PlayerCandidate(MediaPlayerLauncher.PlayerCandidate.Vlc, "/somewhere/VLC");
                return new MediaPlayerLauncher.LaunchedPlayer(vlc, new VlcControlEndpoint(51234, "secret"));
            }
        }

        /// <summary>
        /// Cancelled before anything is followed, so the reporter's loop ends at once and no socket
        /// is ever opened. What is being asserted is that following <em>started</em>.
        /// </summary>
        private static CancellationToken Stopped()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            return cts.Token;
        }

        [Fact]
        public void Playing_an_episode_asks_for_progress_reporting_and_follows_it()
        {
            // The bug this exists to prevent: an entry point that plays and forgets to follow
            // gives a viewer an episode that plays perfectly and reports nothing.
            var launcher = new RecordingLauncher();
            using var client = new JellyfinClient(Settings(), handler: Handler());

            var following = StreamPlayback.Start(
                client,
                "episode-1",
                PlaybackPosition.SecondsToTicks(600),
                Stopped(),
                launcher.Launch);

            Assert.NotNull(following);
            Assert.Equal(1, launcher.Launches);
            Assert.True(launcher.WithProgressReporting);
        }

        [Fact]
        public void It_plays_from_where_the_row_said_you_were()
        {
            var launcher = new RecordingLauncher();
            using var client = new JellyfinClient(Settings(), handler: Handler());

            StreamPlayback.Start(client, "episode-1", PlaybackPosition.SecondsToTicks(600), Stopped(), launcher.Launch);

            Assert.Equal(PlaybackPosition.SecondsToTicks(600), launcher.StartTicks);
        }

        [Fact]
        public void It_plays_the_stream_for_the_item_it_was_given()
        {
            var launcher = new RecordingLauncher();
            using var client = new JellyfinClient(Settings(), handler: Handler());

            StreamPlayback.Start(client, "  episode-1  ", 0, Stopped(), launcher.Launch);

            Assert.Equal(client.BuildStreamUrl("episode-1"), launcher.Url);
            Assert.Equal(0, launcher.StartTicks);
        }

        [Fact]
        public void A_server_that_is_not_configured_plays_without_reporting_rather_than_refusing()
        {
            // Nothing to report to, so no port and no password are spent on a socket nobody will
            // read — but the episode still plays.
            var launcher = new RecordingLauncher();
            using var unconfigured = new JellyfinClient(new JellyfinSettings());

            var following = StreamPlayback.Start(unconfigured, "episode-1", 0, Stopped(), launcher.Launch);

            Assert.Null(following);
            Assert.Equal(1, launcher.Launches);
            Assert.False(launcher.WithProgressReporting);
        }

        [Fact]
        public void There_is_nothing_to_play_without_an_item_or_a_client()
        {
            using var client = new JellyfinClient(Settings(), handler: Handler());

            // Through a void helper: the guards run before anything is launched, so these throw
            // synchronously, and asserting on the Task itself would read as though they did not.
            Assert.Throws<ArgumentException>(() => StartAndDiscard(client, "  "));
            Assert.Throws<ArgumentNullException>(() => StartAndDiscard(null!, "episode-1"));
        }

        private static void StartAndDiscard(JellyfinClient client, string itemId) =>
            _ = StreamPlayback.Start(client, itemId);
    }

    /// <summary>
    /// What the row hands to a player, and what the window says it did.
    /// </summary>
    public class ResumeFromTheRowTests
    {
        private const string ShowId = "series-1";
        private const string ShowTitle = "Interview with the Vampire";

        private static UiMovie Series() => new()
        {
            Id = 0,
            Source = MovieSource.Jellyfin,
            Kind = MediaKind.Series,
            RemoteId = ShowId,
            Title = ShowTitle,
            Genres = "Drama"
        };

        private static UiMovie Film(string itemId, string title) => new()
        {
            Id = 0,
            Source = MovieSource.Jellyfin,
            RemoteId = itemId,
            Title = title,
            Year = 1994,
            Genres = "Drama"
        };

        private static JellyfinResumeItem Episode(int positionSeconds = 600, int sortOrder = 0) => new()
        {
            ItemId = "episode-1",
            ItemType = JellyfinResumeItem.EpisodeType,
            SeriesId = ShowId,
            SeriesName = ShowTitle,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Name = "In throes of increasing wonder … ",
            PositionTicks = PlaybackPosition.SecondsToTicks(positionSeconds),
            RuntimeTicks = PlaybackPosition.SecondsToTicks(3000),
            SortOrder = sortOrder
        };

        [Fact]
        public void An_episode_card_carries_the_position_a_player_will_be_given()
        {
            var card = Assert.Single(ResumeRow.Build(new[] { Series() }, new[] { Episode() }));

            Assert.Equal(PlaybackPosition.SecondsToTicks(600), card.ResumePositionTicks);
        }

        [Fact]
        public void A_film_card_carries_it_too()
        {
            var film = Film("film-1", "The Drama");

            ResumeRow.Build(
                new[] { film },
                new[]
                {
                    new JellyfinResumeItem
                    {
                        ItemId = "film-1",
                        PositionTicks = PlaybackPosition.SecondsToTicks(1500),
                        RuntimeTicks = PlaybackPosition.SecondsToTicks(6000)
                    }
                });

            Assert.Equal(PlaybackPosition.SecondsToTicks(1500), film.ResumePositionTicks);
        }

        [Fact]
        public void A_film_that_has_since_been_finished_loses_the_position_with_the_mark()
        {
            // The window rebuilds its shelves from the same card objects, so a position left on a
            // card would resume a film the server no longer thinks is part-watched.
            var film = Film("film-1", "The Drama");

            ResumeRow.Build(
                new[] { film },
                new[] { new JellyfinResumeItem { ItemId = "film-1", PositionTicks = PlaybackPosition.SecondsToTicks(1500) } });

            Assert.NotEqual(0, film.ResumePositionTicks);

            ResumeRow.Build(new[] { film }, Array.Empty<JellyfinResumeItem>());

            Assert.Equal(0, film.ResumePositionTicks);
            Assert.False(film.HasResume);
        }

        [Fact]
        public void A_dismissed_card_keeps_no_position_either()
        {
            var film = Film("film-1", "The Drama");

            ResumeRow.Build(
                new[] { film },
                new[] { new JellyfinResumeItem { ItemId = "film-1", PositionTicks = PlaybackPosition.SecondsToTicks(1500) } },
                new[] { new ResumeDismissal("film-1", PlaybackPosition.SecondsToTicks(1500)) });

            Assert.Equal(0, film.ResumePositionTicks);
        }

        [Fact]
        public void The_window_says_it_is_resuming_and_which_episode()
        {
            var card = Assert.Single(ResumeRow.Build(new[] { Series() }, new[] { Episode() }));

            Assert.Equal($"Resuming \u201c{ShowTitle}\u201d S1E1 where you left off.", PlayPrompts.PlayingFromTheRow(card, playerCanSeek: true));
        }

        [Fact]
        public void With_no_position_it_says_playing_rather_than_claiming_a_resume()
        {
            var card = new UiMovie
            {
                Source = MovieSource.Jellyfin,
                Kind = MediaKind.Episode,
                RemoteId = "episode-1",
                SeriesId = ShowId,
                Title = ShowTitle,
                SeasonNumber = 1,
                EpisodeNumber = 1
            };

            Assert.Equal($"Playing \u201c{ShowTitle}\u201d S1E1 from the beginning.", PlayPrompts.PlayingFromTheRow(card, playerCanSeek: true));
        }

        [Fact]
        public void A_film_is_named_without_an_episode_number()
        {
            var film = Film("film-1", "The Drama");
            film.ResumePositionTicks = PlaybackPosition.SecondsToTicks(1500);

            Assert.Equal("Resuming \u201cThe Drama\u201d where you left off.", PlayPrompts.PlayingFromTheRow(film, playerCanSeek: true));
        }

        [Fact]
        public void The_prompt_refuses_to_be_asked_about_nothing()
        {
            Assert.Throws<ArgumentNullException>(() => PlayPrompts.PlayingFromTheRow(null!, playerCanSeek: true));
        }
    }
}
