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
    }
}
