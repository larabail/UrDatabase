using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// What the details window says about a file, and whether it asks before opening one.
    ///
    /// These rules used to live in the window's code-behind, out of reach of any test, which is
    /// how "Play will open nothing" came to sit under a button that opened something.
    /// </summary>
    public class PlayPromptsTests
    {
        private static MovieDetailsVm Local(PlayTargetKind kind, string? path, string title = "It") =>
            new() { Title = title, FilePath = path, FileMatch = kind };

        [Fact]
        public void A_linked_file_is_named_and_played_without_a_question()
        {
            var vm = Local(PlayTargetKind.Linked, "/movies/It (2017).mkv");

            Assert.Equal("File: It (2017).mkv", PlayPrompts.FileNote(vm));
            Assert.False(PlayPrompts.NeedsConfirmation(vm));
        }

        [Fact]
        public void A_suggestion_says_it_is_a_guess_and_is_never_played_unasked()
        {
            var vm = Local(PlayTargetKind.Suggested, "/movies/It Follows (2014).mkv");

            var note = PlayPrompts.FileNote(vm);

            Assert.Contains("No file is linked", note);
            Assert.Contains("It Follows (2014).mkv", note);
            Assert.Contains("will ask", note);
            Assert.True(PlayPrompts.NeedsConfirmation(vm));
        }

        [Fact]
        public void The_question_names_the_film_the_file_and_the_risk()
        {
            var vm = Local(PlayTargetKind.Suggested, "/movies/It Follows (2014).mkv");

            var question = PlayPrompts.ConfirmationQuestion(vm);

            Assert.Contains("It", question);
            Assert.Contains("It Follows (2014).mkv", question);
            Assert.Contains("may be a different film", question);
        }

        [Fact]
        public void A_film_with_no_file_says_what_to_do_about_it()
        {
            var vm = Local(PlayTargetKind.None, null);

            Assert.Equal("No file is linked to this film. Use Link File… to choose one.", PlayPrompts.FileNote(vm));
            Assert.False(PlayPrompts.NeedsConfirmation(vm));
        }

        /// <summary>
        /// A kind without a path is a contradiction, and the safe reading of one is "nothing to
        /// play" rather than a sentence with a blank where the filename should be.
        /// </summary>
        [Fact]
        public void A_missing_path_is_treated_as_nothing_to_play()
        {
            Assert.Equal(PlayPrompts.NothingToPlay, PlayPrompts.FileNote(Local(PlayTargetKind.Linked, "   ")));
            Assert.Equal(PlayPrompts.NothingToPlay, PlayPrompts.FileNote(Local(PlayTargetKind.Suggested, null)));
            Assert.False(PlayPrompts.NeedsConfirmation(Local(PlayTargetKind.Suggested, null)));
        }

        [Fact]
        public void A_server_film_talks_about_streaming_and_not_about_files()
        {
            var vm = new MovieDetailsVm { IsRemote = true, StreamUrl = "http://server/stream?api_key=secret" };

            var note = PlayPrompts.FileNote(vm);

            Assert.Contains("Jellyfin", note);
            Assert.DoesNotContain("api_key", note);
            Assert.False(PlayPrompts.NeedsConfirmation(vm));
        }

        [Fact]
        public void An_unreachable_server_says_so()
        {
            var vm = new MovieDetailsVm { IsRemote = true, StreamUrl = null };

            Assert.Contains("could not be reached", PlayPrompts.FileNote(vm));
        }

        /// <summary>
        /// A server film is streamed, never opened from disk, so a stale local path on the same
        /// view model must not turn into a question about a file.
        /// </summary>
        [Fact]
        public void A_server_film_is_never_asked_about_as_a_local_guess()
        {
            var vm = new MovieDetailsVm
            {
                IsRemote = true,
                StreamUrl = "http://server/stream",
                FilePath = "/movies/Something Else.mkv",
                FileMatch = PlayTargetKind.Suggested
            };

            Assert.False(PlayPrompts.NeedsConfirmation(vm));
        }

        [Fact]
        public void A_film_with_no_title_still_produces_a_readable_question()
        {
            var vm = Local(PlayTargetKind.Suggested, "/movies/mystery.mkv", title: "");

            Assert.Contains("this film", PlayPrompts.ConfirmationQuestion(vm));
        }

        /// <summary>
        /// The check that stands between the Play button and the operating system's launcher. It
        /// is deliberately not the same check as the one guarding the link: the row can change
        /// between being written and being used.
        /// </summary>
        [Fact]
        public void A_linked_video_that_is_there_is_not_refused()
        {
            var vm = Local(PlayTargetKind.Linked, "/movies/It (2017).mkv");

            Assert.Null(PlayPrompts.DescribeRefusal(vm, _ => true));
        }

        [Fact]
        public void A_path_that_is_not_a_video_is_refused_before_opening()
        {
            var vm = Local(PlayTargetKind.Linked, "/movies/evil.command");

            var refusal = PlayPrompts.DescribeRefusal(vm, _ => true);

            Assert.NotNull(refusal);
            Assert.Contains("not a video file", refusal);
        }

        /// <summary>
        /// The dangerous shape specifically: a row that says it is a vouched-for link, naming
        /// something the OS would execute. Existing on disk must not be enough to open it.
        /// </summary>
        [Theory]
        [InlineData("/movies/run.sh")]
        [InlineData("/movies/run.command")]
        [InlineData("/movies/setup.exe")]
        [InlineData("/movies/payload.bat")]
        [InlineData("/movies/It (2017).mkv.command")]
        public void A_linked_row_naming_an_executable_is_refused_however_it_got_there(string path)
        {
            Assert.NotNull(PlayPrompts.DescribeRefusal(Local(PlayTargetKind.Linked, path), _ => true));
        }

        [Fact]
        public void A_file_that_has_gone_is_refused_before_opening()
        {
            var vm = Local(PlayTargetKind.Linked, "/movies/It (2017).mkv");

            var refusal = PlayPrompts.DescribeRefusal(vm, _ => false);

            Assert.NotNull(refusal);
            Assert.Contains("no longer there", refusal);
        }

        [Fact]
        public void A_film_with_no_file_at_all_is_refused_before_opening()
        {
            Assert.Equal(PlayPrompts.NothingToPlay, PlayPrompts.DescribeRefusal(Local(PlayTargetKind.None, null), _ => true));
        }

        [Fact]
        public void A_reachable_server_film_is_not_refused_and_an_unreachable_one_is()
        {
            var reachable = new MovieDetailsVm { IsRemote = true, StreamUrl = "http://server/stream" };
            var unreachable = new MovieDetailsVm { IsRemote = true, StreamUrl = null };

            Assert.Null(PlayPrompts.DescribeRefusal(reachable, _ => false));
            Assert.Contains("could not be reached", PlayPrompts.DescribeRefusal(unreachable, _ => false));
        }
    }
}
