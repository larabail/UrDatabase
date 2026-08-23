using System;
using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The details screen's primary button on a film somebody is part way through: what it says,
    /// where it opens the film, and the cases where it must not promise to resume at all.
    /// </summary>
    /// <remarks>
    /// The label and the offset are deliberately two readings of one rule, so most of these assert
    /// both at once. A button that names what it will do and then does something else is worse
    /// than one that never offered.
    /// </remarks>
    public class ResumePlaybackTests
    {
        private const string Stream = "http://media.invalid/Videos/item1/stream?static=true&api_key=abc123";

        private static MovieDetailsVm PartWatched(int positionSeconds = 1500) => new()
        {
            Title = "The Long Afternoon",
            IsRemote = true,
            RemoteId = "item1",
            StreamUrl = Stream,
            ResumePositionTicks = PlaybackPosition.SecondsToTicks(positionSeconds),
            ResumeNote = "42 MIN LEFT"
        };

        // ---------- when resume may be offered ----------

        [Fact]
        public void A_part_watched_stream_on_a_machine_with_vlc_offers_to_continue()
        {
            var vm = PartWatched();

            Assert.True(PlayPrompts.CanResume(vm, playerCanSeek: true));
            Assert.Equal(PlayPrompts.ContinueLabel, PlayPrompts.PlayButtonLabel(vm, true));
            Assert.Equal(PlaybackPosition.SecondsToTicks(1500), PlayPrompts.ResumeFrom(vm, true));
        }

        [Fact]
        public void A_film_nobody_has_started_just_plays()
        {
            var vm = PartWatched();
            vm.ResumePositionTicks = 0;

            Assert.False(PlayPrompts.CanResume(vm, true));
            Assert.Equal(PlayPrompts.PlayLabel, PlayPrompts.PlayButtonLabel(vm, true));
            Assert.Equal(0, PlayPrompts.ResumeFrom(vm, true));
        }

        [Fact]
        public void A_position_of_under_a_second_is_not_a_film_to_continue()
        {
            // Same floor as the row itself: a player opened and shut reports a position before
            // anybody watched anything.
            var vm = PartWatched();
            vm.ResumePositionTicks = 5_000_000;

            Assert.False(vm.HasResumePosition);
            Assert.False(PlayPrompts.CanResume(vm, true));
        }

        [Fact]
        public void Without_a_player_that_can_seek_it_does_not_claim_to_resume()
        {
            // The IINA case, and the no-player-at-all case. The row still shows the film as
            // part-watched, because the server still says so.
            var vm = PartWatched();

            Assert.False(PlayPrompts.CanResume(vm, playerCanSeek: false));
            Assert.Equal(PlayPrompts.PlayLabel, PlayPrompts.PlayButtonLabel(vm, false));
            Assert.Equal(0, PlayPrompts.ResumeFrom(vm, false));
        }

        [Fact]
        public void A_downloaded_copy_plays_rather_than_continues()
        {
            // It is opened with the system's own opener, which takes a path and nothing else.
            var vm = PartWatched();
            vm.DownloadedPath = "/films/The Long Afternoon (1994).mkv";

            Assert.False(PlayPrompts.CanResume(vm, true));
            Assert.Equal(PlayPrompts.PlayLabel, PlayPrompts.PlayButtonLabel(vm, true));
        }

        [Fact]
        public void With_the_server_unreachable_there_is_nothing_to_resume_into()
        {
            var vm = PartWatched();
            vm.StreamUrl = null;

            Assert.False(PlayPrompts.CanResume(vm, true));
        }

        [Fact]
        public void A_local_film_is_never_offered_a_resume()
        {
            // Opened from disk with the system opener, and the server's position is not about
            // this copy anyway.
            var vm = new MovieDetailsVm
            {
                Title = "The Long Afternoon",
                IsRemote = false,
                FilePath = "/films/afternoon.mkv",
                FileMatch = PlayTargetKind.Linked,
                ResumePositionTicks = PlaybackPosition.SecondsToTicks(1500)
            };

            Assert.False(PlayPrompts.CanResume(vm, true));
            Assert.Equal(PlayPrompts.PlayLabel, PlayPrompts.PlayButtonLabel(vm, true));
        }

        [Fact]
        public void The_rule_refuses_a_missing_film_rather_than_guessing()
        {
            Assert.Throws<ArgumentNullException>(() => PlayPrompts.CanResume(null!, true));
        }

        // ---------- what the line under the buttons says ----------

        [Fact]
        public void A_resumable_film_says_how_far_through_it_is_and_that_it_reports_back()
        {
            var note = PlayPrompts.FileNote(PartWatched(), playerCanSeek: true);

            Assert.Contains("42 min left", note, StringComparison.Ordinal);
            Assert.Contains("Continue watching resumes there", note, StringComparison.Ordinal);
            Assert.Contains("back to the server", note, StringComparison.Ordinal);

            // The action bar shares its row with the attribution, so this line has to stay short
            // enough not to wrap into it. The longest note that predates resume is 99 characters.
            Assert.True(note.Length <= 99, $"the note is {note.Length} characters and will wrap: {note}");
        }

        [Fact]
        public void A_part_watched_film_on_iina_explains_why_it_will_start_over()
        {
            // Otherwise the row says "part way through" and the button starts from the beginning,
            // with nothing anywhere connecting the two.
            var note = PlayPrompts.FileNote(PartWatched(), playerCanSeek: false);

            Assert.Contains("42 min left", note, StringComparison.Ordinal);
            Assert.Contains("starts at the beginning", note, StringComparison.Ordinal);
            Assert.Contains("VLC", note, StringComparison.Ordinal);
            Assert.True(note.Length <= 99, $"the note is {note.Length} characters and will wrap: {note}");
        }

        [Fact]
        public void A_film_nobody_has_started_reads_exactly_as_it_did_before()
        {
            var vm = PartWatched();
            vm.ResumePositionTicks = 0;

            Assert.Equal(
                "Streams from your Jellyfin server. Play opens it in VLC or IINA. Download keeps a copy for offline.",
                PlayPrompts.FileNote(vm, playerCanSeek: true));
        }

        [Fact]
        public void A_downloaded_films_line_is_untouched_by_any_of_this()
        {
            var vm = PartWatched();
            vm.DownloadedPath = "/films/afternoon.mkv";

            Assert.Equal(
                "Downloaded to /films/afternoon.mkv. Plays with the server switched off.",
                PlayPrompts.FileNote(vm, playerCanSeek: true));
        }

        [Fact]
        public void An_unreachable_server_still_says_so_first()
        {
            var vm = PartWatched();
            vm.StreamUrl = null;

            Assert.Contains("could not be reached", PlayPrompts.FileNote(vm, playerCanSeek: true));
        }

        // ---------- the launch itself ----------

        private static MediaPlayerLauncher.PlayerCandidate Vlc() => new("VLC", "/somewhere/VLC");
        private static MediaPlayerLauncher.PlayerCandidate Iina() => new("IINA", "/somewhere/IINA");

        [Fact]
        public void Vlc_is_told_where_to_open_the_film_in_seconds()
        {
            // Verified against VLC 3.0.23: --start-time seeks an HTTP input that supports ranges,
            // which is what Jellyfin serves for static=true, and the position is then what its own
            // control interface reports.
            var psi = MediaPlayerLauncher.BuildStartInfo(
                Vlc(), Stream, control: null, startAtTicks: PlaybackPosition.SecondsToTicks(1500));

            var index = psi.ArgumentList.IndexOf("--start-time");

            Assert.True(index >= 0, "--start-time was not passed");
            Assert.Equal("1500", psi.ArgumentList[index + 1]);
        }

        [Fact]
        public void A_fractional_position_keeps_its_fraction_and_its_full_stop()
        {
            // Written invariant on purpose: a machine whose locale writes decimals with a comma
            // would otherwise hand VLC a number it reads as something else, or as nothing.
            var psi = MediaPlayerLauncher.BuildStartInfo(
                Vlc(), Stream, control: null, startAtTicks: PlaybackPosition.SecondsToTicks(90.5));

            Assert.Contains("90.5", psi.ArgumentList);
            Assert.DoesNotContain("90,5", psi.ArgumentList);
        }

        [Fact]
        public void Starting_at_the_beginning_passes_no_offset_at_all()
        {
            var psi = MediaPlayerLauncher.BuildStartInfo(Vlc(), Stream, control: null, startAtTicks: 0);

            Assert.Equal(new[] { Stream }, psi.ArgumentList.ToArray());
        }

        [Fact]
        public void Iina_is_never_handed_an_offset_it_would_ignore()
        {
            var psi = MediaPlayerLauncher.BuildStartInfo(
                Iina(), Stream, control: null, startAtTicks: PlaybackPosition.SecondsToTicks(1500));

            Assert.Equal(new[] { Stream }, psi.ArgumentList.ToArray());
        }

        [Fact]
        public void The_offset_and_the_control_interface_coexist()
        {
            // A resumed film is also a reported one, and the two sets of arguments must not
            // displace each other.
            var psi = MediaPlayerLauncher.BuildStartInfo(
                Vlc(),
                Stream,
                new VlcControlEndpoint(51234, "s3cret"),
                PlaybackPosition.SecondsToTicks(1500));

            Assert.Equal(Stream, psi.ArgumentList[0]);
            Assert.Contains("--extraintf", psi.ArgumentList);
            Assert.Contains("--start-time", psi.ArgumentList);
            Assert.Contains("1500", psi.ArgumentList);
        }

        [Fact]
        public void Seeking_and_reporting_are_asked_of_the_player_separately()
        {
            // Both currently mean "is it VLC", and are two questions so that a future player
            // answering one does not silently imply the other.
            Assert.True(Vlc().CanStartAtAnOffset);
            Assert.True(Vlc().CanReportProgress);
            Assert.False(Iina().CanStartAtAnOffset);
            Assert.False(Iina().CanReportProgress);
        }
    }
}
