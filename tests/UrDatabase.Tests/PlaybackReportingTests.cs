using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Reading VLC's status document. Pure, so the readings that actually go wrong are asserted
    /// rather than hoped for: a paused film, a player that has not opened the stream yet, and the
    /// HTML error page a wrong password produces.
    /// </summary>
    public class VlcStatusTests
    {
        /// <summary>
        /// Trimmed from what VLC 3.0.23 actually answers, keeping the shape: the fields this app
        /// reads are direct children of the root, and an <c>information</c> tree of stream
        /// metadata sits alongside them.
        /// </summary>
        private static string StatusXml(string state, int time, int length) => $"""
            <?xml version="1.0" encoding="utf-8" standalone="yes"?>
            <root>
              <fullscreen>false</fullscreen>
              <seek_sec>10</seek_sec>
              <apiversion>3</apiversion>
              <time>{time}</time>
              <volume>256</volume>
              <length>{length}</length>
              <random>false</random>
              <rate>1</rate>
              <state>{state}</state>
              <loop>false</loop>
              <version>3.0.23 Vetinari</version>
              <position>0.2500</position>
              <information>
                <category name="meta">
                  <info name="filename">stream</info>
                  <info name="time">not a position at all</info>
                </category>
              </information>
            </root>
            """;

        [Fact]
        public void A_playing_film_reports_where_it_is()
        {
            var status = VlcStatus.Parse(StatusXml("playing", time: 1500, length: 6000));

            Assert.NotNull(status);
            Assert.Equal(VlcPlaybackState.Playing, status!.State);
            Assert.Equal(1500, status.PositionSeconds);
            Assert.Equal(6000, status.LengthSeconds);
            Assert.True(status.IsPlaying);
            Assert.True(status.HasFilm);
            Assert.Equal(PlaybackPosition.SecondsToTicks(1500), status.PositionTicks);
            Assert.Equal(PlaybackPosition.SecondsToTicks(6000), status.RuntimeTicks);
        }

        [Fact]
        public void A_paused_film_is_still_a_film_being_watched()
        {
            // The distinction the whole feature turns on: a paused film must not be reported as an
            // abandoned one.
            var status = VlcStatus.Parse(StatusXml("paused", time: 1500, length: 6000));

            Assert.Equal(VlcPlaybackState.Paused, status!.State);
            Assert.True(status.IsPaused);
            Assert.True(status.HasFilm);
            Assert.False(status.IsPlaying);
        }

        [Fact]
        public void A_stopped_player_is_holding_nothing()
        {
            var status = VlcStatus.Parse(StatusXml("stopped", time: 0, length: 0));

            Assert.Equal(VlcPlaybackState.Stopped, status!.State);
            Assert.False(status.HasFilm);
            Assert.Null(status.LengthSeconds);
            Assert.Null(status.RuntimeTicks);
        }

        [Fact]
        public void A_player_still_opening_the_stream_has_started_without_playing()
        {
            Assert.Equal(VlcPlaybackState.Starting, VlcStatus.Parse(StatusXml("opening", 0, 0))!.State);
            Assert.Equal(VlcPlaybackState.Starting, VlcStatus.Parse(StatusXml("buffering", 0, 0))!.State);
        }

        [Fact]
        public void A_negative_time_is_not_a_position()
        {
            // VLC answers -1 before it has opened the stream.
            var status = VlcStatus.Parse(StatusXml("opening", time: -1, length: -1));

            Assert.Equal(0, status!.PositionSeconds);
            Assert.Equal(0, status.PositionTicks);
            Assert.Null(status.LengthSeconds);
        }

        [Fact]
        public void Only_direct_children_are_read()
        {
            // The information tree carries stream tags with names of their own. A descendant
            // search would eventually pick a position out of a film's own metadata.
            var status = VlcStatus.Parse(StatusXml("playing", time: 1500, length: 6000));

            Assert.Equal(1500, status!.PositionSeconds);
        }

        [Fact]
        public void A_malformed_document_is_no_reading_rather_than_an_exception()
        {
            // What a wrong password produces: the interface answers with an HTML error page.
            Assert.Null(VlcStatus.Parse("<html><body>401 Unauthorized</body>"));
            Assert.Null(VlcStatus.Parse("not xml at all"));
            Assert.Null(VlcStatus.Parse("<root><time>12</time>"));
            Assert.Null(VlcStatus.Parse(""));
            Assert.Null(VlcStatus.Parse(null));
        }

        [Fact]
        public void A_document_that_parses_but_says_nothing_useful_is_still_an_answer()
        {
            // A VLC that answered is a different fact from one that did not, even when what it
            // said means nothing here.
            var status = VlcStatus.Parse("<root><state>gibberish</state></root>");

            Assert.NotNull(status);
            Assert.Equal(VlcPlaybackState.Unknown, status!.State);
            Assert.False(status.HasFilm);
        }

        [Fact]
        public void A_time_that_is_not_a_number_is_not_a_position()
        {
            var status = VlcStatus.Parse("<root><time>abc</time><state>playing</state></root>");

            Assert.Equal(0, status!.PositionSeconds);
        }

        [Fact]
        public void The_state_is_matched_by_name_and_not_by_case()
        {
            Assert.Equal(VlcPlaybackState.Playing, VlcStatus.ReadState("Playing"));
            Assert.Equal(VlcPlaybackState.Paused, VlcStatus.ReadState("  PAUSED "));
            Assert.Equal(VlcPlaybackState.Unknown, VlcStatus.ReadState(null));
        }
    }

    /// <summary>
    /// The loopback interface one launch is given. The password ends up in a command line every
    /// user on the machine can read, so its properties are a security matter rather than a detail.
    /// </summary>
    public class VlcControlTests
    {
        [Fact]
        public void Every_launch_gets_a_different_password()
        {
            var passwords = Enumerable.Range(0, 50).Select(_ => VlcControl.NewPassword()).ToList();

            Assert.Equal(50, passwords.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void The_password_carries_real_entropy()
        {
            var password = VlcControl.NewPassword();

            Assert.Equal(VlcControl.PasswordBytes * 2, password.Length);
            Assert.All(password, c => Assert.True(Uri.IsHexDigit(c), $"'{c}' is not a hex digit"));
        }

        [Fact]
        public void The_interface_binds_to_loopback_and_nothing_else()
        {
            // Bound to any address it would be a remote control for anybody on the same network.
            var arguments = VlcControl.BuildArguments(new VlcControlEndpoint(51234, "secret"));

            var host = arguments.SkipWhile(a => a != "--http-host").Skip(1).First();

            Assert.Equal("127.0.0.1", host);
            Assert.Equal("127.0.0.1", VlcControlEndpoint.Host);
        }

        [Fact]
        public void The_real_interface_is_added_beside_the_players_own_rather_than_replacing_it()
        {
            // --intf dummy is how this endpoint was verified from a terminal. The person pressing
            // Play wants to watch the film.
            var arguments = VlcControl.BuildArguments(new VlcControlEndpoint(51234, "secret"));

            Assert.Contains("--extraintf", arguments);
            Assert.DoesNotContain("--intf", arguments);
            Assert.DoesNotContain("dummy", arguments);
        }

        [Fact]
        public void The_arguments_name_the_port_and_the_password()
        {
            var arguments = VlcControl.BuildArguments(new VlcControlEndpoint(51234, "s3cret"));

            Assert.Equal(
                new[] { "--extraintf", "http", "--http-host", "127.0.0.1", "--http-port", "51234", "--http-password", "s3cret" },
                arguments.ToArray());
        }

        [Fact]
        public void The_status_address_is_on_loopback()
        {
            var endpoint = new VlcControlEndpoint(51234, "secret");

            Assert.Equal("http://127.0.0.1:51234/requests/status.xml", endpoint.StatusUri.ToString());
        }

        [Fact]
        public void What_a_launch_may_log_never_includes_the_password()
        {
            var endpoint = new VlcControlEndpoint(51234, "the-actual-secret");

            var described = VlcControl.Describe("VLC", endpoint);

            Assert.DoesNotContain("the-actual-secret", described, StringComparison.Ordinal);
            Assert.Contains("51234", described, StringComparison.Ordinal);
            Assert.Contains("VLC", described, StringComparison.Ordinal);
        }

        [Fact]
        public void A_launch_with_no_interface_says_only_what_it_used_to()
        {
            Assert.Equal("streaming through IINA", VlcControl.Describe("IINA", null));
        }

        [Fact]
        public void An_offered_port_is_one_the_machine_has_just_confirmed_is_free()
        {
            // Assuming a port is how two films playing at once would silently stop reporting.
            var first = VlcControl.FindFreePort();
            var second = VlcControl.FindFreePort();

            Assert.InRange(first, 1024, 65535);
            Assert.InRange(second, 1024, 65535);
        }

        [Fact]
        public void Basic_authentication_uses_an_empty_username()
        {
            var header = HttpVlcStatusReader.BuildBasicAuthorization("s3cret");

            Assert.StartsWith("Basic ", header, StringComparison.Ordinal);
            Assert.Equal(
                ":s3cret",
                System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(header["Basic ".Length..])));
        }
    }

    /// <summary>
    /// The launch itself: what VLC is told, and what IINA is deliberately not.
    /// </summary>
    public class MediaPlayerControlLaunchTests
    {
        private static MediaPlayerLauncher.PlayerCandidate Vlc() => new("VLC", "/somewhere/VLC");
        private static MediaPlayerLauncher.PlayerCandidate Iina() => new("IINA", "/somewhere/IINA");

        private const string Url = "http://media.invalid/Videos/item0/stream?static=true&api_key=abc123";

        [Fact]
        public void Vlc_can_be_followed_and_iina_cannot()
        {
            // IINA is mpv underneath and exposes a JSON IPC socket over a unix domain socket,
            // which is a different protocol over a different transport.
            Assert.True(Vlc().CanReportProgress);
            Assert.False(Iina().CanReportProgress);
        }

        [Fact]
        public void The_control_arguments_come_after_the_url()
        {
            var psi = MediaPlayerLauncher.BuildStartInfo(Vlc(), Url, new VlcControlEndpoint(51234, "s3cret"));

            Assert.Equal(Url, psi.ArgumentList[0]);
            Assert.Contains("--extraintf", psi.ArgumentList);
            Assert.Contains("51234", psi.ArgumentList);
            Assert.False(psi.UseShellExecute);
        }

        [Fact]
        public void An_iina_launch_is_exactly_what_it_always_was()
        {
            // The constraint that matters for the one user who has IINA: it still plays films.
            var psi = MediaPlayerLauncher.BuildStartInfo(Iina(), Url, new VlcControlEndpoint(51234, "s3cret"));

            Assert.Equal(new[] { Url }, psi.ArgumentList.ToArray());
        }

        [Fact]
        public void A_launch_with_no_interface_is_unchanged()
        {
            var psi = MediaPlayerLauncher.BuildStartInfo(Vlc(), Url);

            Assert.Equal(new[] { Url }, psi.ArgumentList.ToArray());
        }
    }

    /// <summary>
    /// When a report is due. The whole of the judgement in progress reporting, and the reason it
    /// is a class of its own rather than a few conditions inside a polling loop.
    /// </summary>
    public class PlaybackReportScheduleTests
    {
        private static VlcStatus Playing(int seconds, int length = 6000) => new()
        {
            State = VlcPlaybackState.Playing,
            PositionSeconds = seconds,
            LengthSeconds = length
        };

        private static VlcStatus Paused(int seconds, int length = 6000) => new()
        {
            State = VlcPlaybackState.Paused,
            PositionSeconds = seconds,
            LengthSeconds = length
        };

        private static VlcStatus Stopped() => new() { State = VlcPlaybackState.Stopped };

        private static PlaybackReportSchedule Schedule() =>
            new(interval: TimeSpan.FromSeconds(10),
                silenceBeforeStopped: TimeSpan.FromSeconds(6),
                giveUpAfterSilence: TimeSpan.FromSeconds(30));

        [Fact]
        public void A_film_that_starts_is_reported_once()
        {
            var schedule = Schedule();

            Assert.Equal(PlaybackReportKind.Start, schedule.Next(Playing(0), TimeSpan.Zero).Kind);
            Assert.Equal(PlaybackReportKind.None, schedule.Next(Playing(1), TimeSpan.FromSeconds(2)).Kind);
            Assert.True(schedule.HasStarted);
        }

        [Fact]
        public void A_film_nobody_started_is_never_reported()
        {
            // Pointing the app at a stream and closing the player before a frame is drawn must not
            // put a film in the viewer's Continue watching row.
            var schedule = Schedule();

            Assert.Equal(PlaybackReportKind.None, schedule.Next(new VlcStatus { State = VlcPlaybackState.Starting }, TimeSpan.Zero).Kind);
            Assert.Equal(PlaybackReportKind.None, schedule.Next(Stopped(), TimeSpan.FromSeconds(2)).Kind);
            Assert.Equal(PlaybackReportKind.None, schedule.Finish().Kind);
            Assert.False(schedule.HasStarted);
        }

        [Fact]
        public void A_film_already_paused_before_it_played_has_not_started()
        {
            var schedule = Schedule();

            Assert.Equal(PlaybackReportKind.None, schedule.Next(Paused(0), TimeSpan.Zero).Kind);
            Assert.False(schedule.HasStarted);
        }

        [Fact]
        public void Progress_arrives_on_the_interval_and_not_before()
        {
            var schedule = Schedule();
            schedule.Next(Playing(0), TimeSpan.Zero);

            Assert.Equal(PlaybackReportKind.None, schedule.Next(Playing(8), TimeSpan.FromSeconds(8)).Kind);

            var due = schedule.Next(Playing(10), TimeSpan.FromSeconds(10));

            Assert.Equal(PlaybackReportKind.Progress, due.Kind);
            Assert.Equal(PlaybackPosition.SecondsToTicks(10), due.PositionTicks);
            Assert.False(due.IsPaused);

            Assert.Equal(PlaybackReportKind.None, schedule.Next(Playing(12), TimeSpan.FromSeconds(12)).Kind);
            Assert.Equal(PlaybackReportKind.Progress, schedule.Next(Playing(20), TimeSpan.FromSeconds(20)).Kind);
        }

        [Fact]
        public void A_pause_is_reported_at_once_rather_than_at_the_next_interval()
        {
            var schedule = Schedule();
            schedule.Next(Playing(0), TimeSpan.Zero);

            var paused = schedule.Next(Paused(4), TimeSpan.FromSeconds(4));

            Assert.Equal(PlaybackReportKind.Progress, paused.Kind);
            Assert.True(paused.IsPaused);
        }

        [Fact]
        public void Resuming_is_reported_at_once_too()
        {
            var schedule = Schedule();
            schedule.Next(Playing(0), TimeSpan.Zero);
            schedule.Next(Paused(4), TimeSpan.FromSeconds(4));

            var resumed = schedule.Next(Playing(5), TimeSpan.FromSeconds(5));

            Assert.Equal(PlaybackReportKind.Progress, resumed.Kind);
            Assert.False(resumed.IsPaused);
        }

        [Fact]
        public void A_film_left_paused_keeps_reporting_rather_than_going_quiet()
        {
            // A session that simply goes silent is one the server eventually times out, which
            // would turn "gone to make tea" into "stopped watching".
            var schedule = Schedule();
            schedule.Next(Playing(0), TimeSpan.Zero);
            schedule.Next(Paused(4), TimeSpan.FromSeconds(4));

            var still = schedule.Next(Paused(4), TimeSpan.FromSeconds(30));

            Assert.Equal(PlaybackReportKind.Progress, still.Kind);
            Assert.True(still.IsPaused);
            Assert.False(schedule.IsFinished);
        }

        [Fact]
        public void The_end_of_a_film_is_reported_at_the_place_it_reached()
        {
            // VLC reports a time of zero the instant a film ends. Forwarding that would rewind the
            // film to the beginning on the server — the exact opposite of the point.
            var schedule = Schedule();
            schedule.Next(Playing(0), TimeSpan.Zero);
            schedule.Next(Playing(5900), TimeSpan.FromSeconds(10));

            var stopped = schedule.Next(Stopped(), TimeSpan.FromSeconds(12));

            Assert.Equal(PlaybackReportKind.Stopped, stopped.Kind);
            Assert.Equal(PlaybackPosition.SecondsToTicks(5900), stopped.PositionTicks);
            Assert.True(schedule.IsFinished);
        }

        [Fact]
        public void Nothing_is_reported_after_the_stop()
        {
            var schedule = Schedule();
            schedule.Next(Playing(0), TimeSpan.Zero);
            schedule.Next(Stopped(), TimeSpan.FromSeconds(4));

            Assert.Equal(PlaybackReportKind.None, schedule.Next(Playing(10), TimeSpan.FromSeconds(20)).Kind);
            Assert.Equal(PlaybackReportKind.None, schedule.Finish().Kind);
        }

        [Fact]
        public void A_player_that_goes_away_mid_film_is_a_stop_at_the_last_position_seen()
        {
            var schedule = Schedule();
            schedule.Next(Playing(0), TimeSpan.Zero);
            schedule.Next(Playing(1500), TimeSpan.FromSeconds(10));

            // One missed poll is not a closed player.
            Assert.Equal(PlaybackReportKind.None, schedule.Next(null, TimeSpan.FromSeconds(12)).Kind);

            var stopped = schedule.Next(null, TimeSpan.FromSeconds(18));

            Assert.Equal(PlaybackReportKind.Stopped, stopped.Kind);
            Assert.Equal(PlaybackPosition.SecondsToTicks(1500), stopped.PositionTicks);
        }

        [Fact]
        public void A_hiccup_that_recovers_does_not_end_the_session()
        {
            var schedule = Schedule();
            schedule.Next(Playing(0), TimeSpan.Zero);
            schedule.Next(null, TimeSpan.FromSeconds(2));
            schedule.Next(Playing(4), TimeSpan.FromSeconds(4));

            Assert.Equal(PlaybackReportKind.None, schedule.Next(null, TimeSpan.FromSeconds(8)).Kind);
            Assert.False(schedule.IsFinished);
        }

        [Fact]
        public void An_interface_that_never_answers_is_given_up_on_quietly()
        {
            // A VLC built without the HTTP interface, a port taken between being offered and being
            // bound, a player closed immediately. The film played, which was the point.
            var schedule = Schedule();

            for (var second = 0; second < 30; second += 2)
                Assert.Equal(PlaybackReportKind.None, schedule.Next(null, TimeSpan.FromSeconds(second)).Kind);

            Assert.False(schedule.IsFinished);

            Assert.Equal(PlaybackReportKind.None, schedule.Next(null, TimeSpan.FromSeconds(30)).Kind);

            Assert.True(schedule.IsFinished);
            Assert.False(schedule.HasStarted);
        }

        [Fact]
        public void Closing_the_app_mid_film_still_says_where_it_got_to()
        {
            var schedule = Schedule();
            schedule.Next(Playing(0), TimeSpan.Zero);
            schedule.Next(Playing(1500), TimeSpan.FromSeconds(10));

            var last = schedule.Finish();

            Assert.Equal(PlaybackReportKind.Stopped, last.Kind);
            Assert.Equal(PlaybackPosition.SecondsToTicks(1500), last.PositionTicks);
            Assert.True(schedule.IsFinished);
        }

        [Fact]
        public void Finishing_a_film_nobody_started_says_nothing()
        {
            Assert.Equal(PlaybackReportKind.None, Schedule().Finish().Kind);
        }
    }

    /// <summary>
    /// The loop between VLC and the server, driven by a fake reader, a fake sink and a fake clock.
    /// No socket, no player and no second passing.
    /// </summary>
    public class PlaybackReporterTests
    {
        private sealed class ScriptedReader : IVlcStatusReader
        {
            private readonly Queue<string?> _answers;

            public ScriptedReader(params string?[] answers) => _answers = new Queue<string?>(answers);

            public int Reads { get; private set; }

            public Task<string?> ReadAsync(CancellationToken ct = default)
            {
                Reads++;
                return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
            }
        }

        private sealed class RecordingSink : IPlaybackReportSink
        {
            public List<string> Calls { get; } = new();
            public List<long> Positions { get; } = new();

            public Task StartedAsync(long positionTicks, CancellationToken ct = default)
            {
                Calls.Add("start");
                Positions.Add(positionTicks);
                return Task.CompletedTask;
            }

            public Task ProgressAsync(long positionTicks, bool isPaused, CancellationToken ct = default)
            {
                Calls.Add(isPaused ? "progress(paused)" : "progress");
                Positions.Add(positionTicks);
                return Task.CompletedTask;
            }

            public Task StoppedAsync(long positionTicks, CancellationToken ct = default)
            {
                Calls.Add("stopped");
                Positions.Add(positionTicks);
                return Task.CompletedTask;
            }
        }

        private sealed class ThrowingSink : IPlaybackReportSink
        {
            public int Attempts { get; private set; }

            public Task StartedAsync(long positionTicks, CancellationToken ct = default) => Fail();
            public Task ProgressAsync(long positionTicks, bool isPaused, CancellationToken ct = default) => Fail();
            public Task StoppedAsync(long positionTicks, CancellationToken ct = default) => Fail();

            private Task Fail()
            {
                Attempts++;
                throw new JellyfinException("The server could not be reached.");
            }
        }

        private static string Xml(string state, int time, int length = 6000) =>
            $"<root><time>{time}</time><length>{length}</length><state>{state}</state></root>";

        /// <summary>
        /// A clock the test moves itself, advanced by the same wait the reporter would really
        /// have taken. Without it every one of these would take a minute of real time.
        /// </summary>
        private sealed class FakeClock
        {
            public TimeSpan Now { get; private set; }

            public TimeSpan Read() => Now;

            public Task Delay(TimeSpan by, CancellationToken ct)
            {
                Now += by;
                return Task.CompletedTask;
            }
        }

        private static PlaybackReporter Reporter(IVlcStatusReader reader, IPlaybackReportSink sink, FakeClock clock) =>
            new(reader,
                sink,
                new PlaybackReportSchedule(
                    interval: TimeSpan.FromSeconds(10),
                    silenceBeforeStopped: TimeSpan.FromSeconds(6),
                    giveUpAfterSilence: TimeSpan.FromSeconds(30)),
                pollInterval: TimeSpan.FromSeconds(2),
                clock: clock.Read,
                delay: clock.Delay);

        [Fact]
        public async Task A_film_is_reported_started_then_progressing_then_stopped_in_that_order()
        {
            var clock = new FakeClock();
            var reader = new ScriptedReader(
                Xml("opening", 0),
                Xml("playing", 0),
                Xml("playing", 2),
                Xml("playing", 4),
                Xml("playing", 6),
                Xml("playing", 8),
                Xml("playing", 10),
                Xml("playing", 12),
                Xml("stopped", 0, 0));

            var sink = new RecordingSink();

            await Reporter(reader, sink, clock).RunAsync();

            Assert.Equal(new[] { "start", "progress", "stopped" }, sink.Calls.ToArray());
            Assert.Equal(PlaybackPosition.SecondsToTicks(10), sink.Positions[1]);
            Assert.Equal(PlaybackPosition.SecondsToTicks(12), sink.Positions[2]);
        }

        [Fact]
        public async Task A_paused_film_is_reported_as_paused_rather_than_stopped()
        {
            var clock = new FakeClock();
            var reader = new ScriptedReader(
                Xml("playing", 0),
                Xml("paused", 2),
                Xml("stopped", 0, 0));

            var sink = new RecordingSink();

            await Reporter(reader, sink, clock).RunAsync();

            Assert.Equal(new[] { "start", "progress(paused)", "stopped" }, sink.Calls.ToArray());
        }

        [Fact]
        public async Task Playback_still_succeeds_when_the_interface_never_answers()
        {
            // Nothing is reported and nothing is shown. The film is already playing; this loop is
            // the only thing that failed, and it is a bonus.
            var clock = new FakeClock();
            var reader = new ScriptedReader();
            var sink = new RecordingSink();

            await Reporter(reader, sink, clock).RunAsync();

            Assert.Empty(sink.Calls);
            Assert.True(reader.Reads > 1);
            Assert.True(clock.Now >= TimeSpan.FromSeconds(30));
        }

        [Fact]
        public async Task An_answer_that_is_not_xml_is_treated_as_no_answer()
        {
            var clock = new FakeClock();
            var sink = new RecordingSink();

            await Reporter(new ScriptedReader("<html>401 Unauthorized</html>"), sink, clock).RunAsync();

            Assert.Empty(sink.Calls);
        }

        [Fact]
        public async Task A_server_that_refuses_every_report_never_reaches_the_viewer()
        {
            var clock = new FakeClock();
            var sink = new ThrowingSink();

            var reader = new ScriptedReader(
                Xml("playing", 0),
                Xml("playing", 2),
                Xml("stopped", 0, 0));

            // The assertion is that this returns at all rather than throwing into a task nobody
            // awaits, which would end the process at whatever moment the finalizer ran.
            await Reporter(reader, sink, clock).RunAsync();

            Assert.True(sink.Attempts >= 2);
        }

        [Fact]
        public async Task Closing_the_app_mid_film_sends_a_last_stop()
        {
            var clock = new FakeClock();
            var sink = new RecordingSink();

            using var cts = new CancellationTokenSource();

            var reader = new ScriptedReader(
                Xml("playing", 0),
                Xml("playing", 1500));

            // Cancelled once the film is under way, which is the window closing on a film that is
            // still running.
            var reporter = new PlaybackReporter(
                new CancellingReader(reader, cts, afterReads: 2),
                sink,
                new PlaybackReportSchedule(),
                pollInterval: TimeSpan.FromSeconds(2),
                clock: clock.Read,
                delay: clock.Delay);

            await reporter.RunAsync(cts.Token);

            Assert.Equal("start", sink.Calls.First());
            Assert.Equal("stopped", sink.Calls.Last());
            Assert.Equal(PlaybackPosition.SecondsToTicks(1500), sink.Positions.Last());
        }

        private sealed class CancellingReader : IVlcStatusReader
        {
            private readonly IVlcStatusReader _inner;
            private readonly CancellationTokenSource _cts;
            private readonly int _afterReads;
            private int _reads;

            public CancellingReader(IVlcStatusReader inner, CancellationTokenSource cts, int afterReads)
            {
                _inner = inner;
                _cts = cts;
                _afterReads = afterReads;
            }

            public async Task<string?> ReadAsync(CancellationToken ct = default)
            {
                var answer = await _inner.ReadAsync(ct);
                if (++_reads >= _afterReads) _cts.Cancel();
                return answer;
            }
        }
    }

    /// <summary>
    /// What actually goes to the server. Driven through a fake handler; nothing here reaches one.
    /// </summary>
    public class JellyfinPlaybackReportTests
    {
        private const string ServerUrl = "http://media.invalid:8096";

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

        private static FakeHttpMessageHandler Handler() => new(request =>
            request.RequestUri!.ToString().Contains("AuthenticateByName", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(AuthJson, System.Text.Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.NoContent));

        [Fact]
        public async Task The_three_reports_go_to_the_three_endpoints()
        {
            var handler = Handler();
            using var client = new JellyfinClient(Settings(), handler: handler);

            await client.ReportPlaybackStartAsync("item1", 0);
            await client.ReportPlaybackProgressAsync("item1", PlaybackPosition.SecondsToTicks(1500), isPaused: false);
            await client.ReportPlaybackStoppedAsync("item1", PlaybackPosition.SecondsToTicks(3000));

            var posted = handler.Requests.Where(r => r.Contains("Sessions/Playing", StringComparison.Ordinal)).ToList();

            Assert.Equal(
                new[]
                {
                    $"{ServerUrl}/Sessions/Playing",
                    $"{ServerUrl}/Sessions/Playing/Progress",
                    $"{ServerUrl}/Sessions/Playing/Stopped"
                },
                posted.ToArray());
        }

        [Fact]
        public async Task A_report_names_the_item_and_the_position()
        {
            var handler = Handler();
            using var client = new JellyfinClient(Settings(), handler: handler);

            await client.ReportPlaybackProgressAsync("item-42", PlaybackPosition.SecondsToTicks(1500), isPaused: true);

            var body = handler.RequestBodies.Last(b => b is not null && b.Contains("PositionTicks", StringComparison.Ordinal))!;

            Assert.Contains("\"ItemId\":\"item-42\"", body, StringComparison.Ordinal);
            Assert.Contains("\"PositionTicks\":15000000000", body, StringComparison.Ordinal);
            Assert.Contains("\"IsPaused\":true", body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_report_is_signed_in_as_the_user_whose_row_it_will_appear_in()
        {
            var handler = Handler();
            using var client = new JellyfinClient(Settings(), handler: handler);

            await client.ReportPlaybackStartAsync("item1", 0);

            var authorization = handler.RawAuthorizationHeaders.Last();

            Assert.NotNull(authorization);
            Assert.Contains("Token=\"issued-session-token\"", authorization!, StringComparison.Ordinal);
        }

        [Fact]
        public void The_body_says_the_film_is_direct_played_because_it_is()
        {
            // The stream URL asks for static=true, so nothing is transcoded and claiming otherwise
            // would put a wrong line in the server's own dashboard.
            var body = JellyfinClient.BuildPlaybackReportBody("item1", 100, isPaused: false);

            Assert.Contains("\"PlayMethod\":\"DirectStream\"", body, StringComparison.Ordinal);
            Assert.Contains("\"MediaSourceId\":\"item1\"", body, StringComparison.Ordinal);
        }

        [Fact]
        public void A_negative_position_never_leaves_the_app()
        {
            Assert.Contains("\"PositionTicks\":0", JellyfinClient.BuildPlaybackReportBody("item1", -5, false), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_film_with_no_id_is_not_reported_at_all()
        {
            var handler = Handler();
            using var client = new JellyfinClient(Settings(), handler: handler);

            await client.ReportPlaybackStartAsync("  ", 100);

            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task A_server_that_refuses_a_report_says_so_rather_than_pretending()
        {
            // The reporter swallows this. The client's job is to be honest about it so the log
            // says which of the two happened.
            var handler = new FakeHttpMessageHandler(request =>
                request.RequestUri!.ToString().Contains("AuthenticateByName", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(AuthJson, System.Text.Encoding.UTF8, "application/json")
                    }
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized));

            using var client = new JellyfinClient(Settings(), handler: handler);

            await Assert.ThrowsAsync<JellyfinException>(() => client.ReportPlaybackStartAsync("item1", 0));
        }

        [Fact]
        public void The_sink_refuses_to_be_built_without_something_to_report_about()
        {
            using var client = new JellyfinClient(Settings());

            Assert.Throws<ArgumentException>(() => new JellyfinPlaybackSink(client, ""));
            Assert.Throws<ArgumentNullException>(() => new JellyfinPlaybackSink(null!, "item1"));
        }

        [Fact]
        public void There_is_nothing_to_follow_without_a_control_interface_a_server_and_an_id()
        {
            using var client = new JellyfinClient(Settings());
            var vlc = new MediaPlayerLauncher.PlayerCandidate("VLC", "/somewhere/VLC");
            var endpoint = new VlcControlEndpoint(51234, "secret");

            Assert.Null(PlaybackTracking.Follow(null, client, "item1"));
            Assert.Null(PlaybackTracking.Follow(new MediaPlayerLauncher.LaunchedPlayer(vlc, null), client, "item1"));
            Assert.Null(PlaybackTracking.Follow(new MediaPlayerLauncher.LaunchedPlayer(vlc, endpoint), null, "item1"));
            Assert.Null(PlaybackTracking.Follow(new MediaPlayerLauncher.LaunchedPlayer(vlc, endpoint), client, null));

            using var unconfigured = new JellyfinClient(new JellyfinSettings());
            Assert.Null(PlaybackTracking.Follow(new MediaPlayerLauncher.LaunchedPlayer(vlc, endpoint), unconfigured, "item1"));
        }
    }
}
