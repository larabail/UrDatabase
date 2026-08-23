using System;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// What the Upload button offers, refuses and says afterwards. It lives outside the window for
    /// the reason <see cref="PlayPromptsTests"/> gives: nothing reachable only from a window can
    /// be tested without a UI thread, and wording that cannot be tested is wording that quietly
    /// stops being true.
    ///
    /// One sentence here is load-bearing. Jellyfin's scan is asynchronous, so an upload finishing
    /// is not the film appearing; saying "uploaded" and leaving somebody staring at an unchanged
    /// library is how a working feature gets reported as broken.
    /// </summary>
    public class UploadPromptsTests
    {
        private static MovieDetailsVm Local(string? path = "/films/arrival.mkv") => new()
        {
            Title = "Arrival",
            Year = 2016,
            FilePath = path,
            FileMatch = PlayTargetKind.Linked
        };

        [Fact]
        public void A_local_film_with_a_linked_file_may_be_uploaded()
        {
            Assert.Null(UploadPrompts.DescribeRefusal(Local(), _ => true));
            Assert.True(Local().CanUpload);
        }

        [Fact]
        public void A_film_with_no_file_says_how_to_give_it_one()
        {
            var vm = Local(path: null);

            Assert.False(vm.CanUpload);
            Assert.Contains("Link File", UploadPrompts.DescribeRefusal(vm, _ => true)!, StringComparison.Ordinal);
        }

        [Fact]
        public void A_film_that_only_lives_on_the_server_has_nothing_to_send()
        {
            var vm = Local();
            vm.IsRemote = true;

            Assert.False(vm.CanUpload);
            Assert.NotNull(UploadPrompts.DescribeRefusal(vm, _ => true));
        }

        /// <summary>
        /// The film in both places, which the app shows as one card. Uploading it again would put
        /// a second copy beside the one already there.
        /// </summary>
        [Fact]
        public void A_film_the_server_already_has_is_not_offered_again()
        {
            var vm = Local();
            vm.IsOnServer = true;

            Assert.False(vm.CanUpload);
            Assert.Contains("already has", UploadPrompts.DescribeRefusal(vm, _ => true)!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_file_that_is_not_a_video_is_refused_with_the_reason()
        {
            var vm = Local("/films/arrival.txt");

            Assert.Contains("not a video file", UploadPrompts.DescribeRefusal(vm, _ => true)!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_file_that_has_been_moved_since_the_screen_opened_is_refused()
        {
            Assert.NotNull(UploadPrompts.DescribeRefusal(Local(), _ => false));
        }

        /// <summary>
        /// A guessed match is not good enough to put in somebody's server library, where — unlike
        /// a mistaken Play — it becomes a mess for other people on other devices to find.
        /// </summary>
        [Fact]
        public void A_guessed_file_is_confirmed_before_it_is_sent()
        {
            var vm = Local();
            vm.FileMatch = PlayTargetKind.Suggested;

            Assert.True(UploadPrompts.NeedsConfirmation(vm));

            var question = UploadPrompts.ConfirmationQuestion(vm);
            Assert.Contains("arrival.mkv", question, StringComparison.Ordinal);
            Assert.Contains("Arrival", question, StringComparison.Ordinal);
            Assert.Contains("may be a different film", question, StringComparison.Ordinal);
        }

        [Fact]
        public void A_linked_file_is_sent_without_a_question()
        {
            Assert.False(UploadPrompts.NeedsConfirmation(Local()));
        }

        [Fact]
        public void A_finished_upload_says_the_film_is_not_there_yet()
        {
            var message = UploadPrompts.Describe(
                new JellyfinUploadResult("movies/Arrival (2016)/Arrival (2016).mkv", 1024, false, LibraryRefreshed: true));

            Assert.Contains("movies/Arrival (2016)/Arrival (2016).mkv", message, StringComparison.Ordinal);
            Assert.Contains("scanning", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_upload_Jellyfin_was_not_told_about_says_when_the_film_will_appear()
        {
            var message = UploadPrompts.Describe(
                new JellyfinUploadResult("movies/Arrival (2016)/Arrival (2016).mkv", 1024, false, LibraryRefreshed: false));

            Assert.Contains("next scan", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_film_that_was_already_there_says_nothing_was_uploaded()
        {
            var message = UploadPrompts.Describe(
                new JellyfinUploadResult("movies/Arrival (2016)/Arrival (2016).mp4", 900, true, LibraryRefreshed: false));

            Assert.Contains("Nothing was uploaded", message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The reason to stop a transfer is usually a suspicion that it has made a mess, so the
        /// sentence has to answer that. It promises what can actually be kept — the rename never
        /// happened, so the library was never touched — rather than that the server's disk is
        /// spotless, which cannot be guaranteed when the connection is what failed.
        /// </summary>
        [Fact]
        public void Stopping_says_what_it_can_actually_promise()
        {
            Assert.Contains("Nothing was added to your Jellyfin library", UploadPrompts.Cancelled, StringComparison.Ordinal);
            Assert.DoesNotContain("removed from the server", UploadPrompts.Cancelled, StringComparison.Ordinal);
        }

        [Fact]
        public void Progress_reads_as_a_size_rather_than_a_byte_count()
        {
            var line = UploadPrompts.Progress(new JellyfinUploadProgress(524_288, 1_048_576));

            Assert.Contains("512 KB", line, StringComparison.Ordinal);
            Assert.Contains("1.0 MB", line, StringComparison.Ordinal);
            Assert.Contains("50%", line, StringComparison.Ordinal);
        }

        [Fact]
        public void Progress_with_no_total_does_not_invent_a_percentage()
        {
            var report = new JellyfinUploadProgress(1024, null);

            Assert.Null(report.Fraction);
            Assert.DoesNotContain("%", UploadPrompts.Progress(report), StringComparison.Ordinal);
        }

        [Fact]
        public void A_null_film_is_a_programming_error_rather_than_a_message()
        {
            Assert.Throws<ArgumentNullException>(() => UploadPrompts.DescribeRefusal(null!));
            Assert.Throws<ArgumentNullException>(() => UploadPrompts.NeedsConfirmation(null!));
            Assert.Throws<ArgumentNullException>(() => UploadPrompts.ConfirmationQuestion(null!));
        }
    }
}
