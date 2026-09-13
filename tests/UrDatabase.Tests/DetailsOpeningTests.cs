using System;
using System.IO;
using System.Linq;
using Xunit;

namespace UrDatabase.Tests
{

    public class DetailsOpeningTests
    {
        [Theory]
        [InlineData("ShowLocalDetailsAsync(UiMovie m)", "await ShowDetailsAsync(")]
        [InlineData("ShowRemoteDetailsAsync(UiMovie m)", "await ShowDetailsAsync(")]
        [InlineData("ShowProgrammeAsync(string? seriesId", "await loading.ShowAsync(")]
        public void Opening_a_page_does_not_await_network_requests_before_showing_it(string method, string show)
        {
            var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "UrDatabase.App",
                "Views", "MainWindow.axaml.cs"));
            var start = source.IndexOf("private async Task " + method, StringComparison.Ordinal);
            Assert.True(start >= 0);
            var end = source.IndexOf("\n        private ", start + 1, StringComparison.Ordinal);
            var body = source[start..end];

            // A cached page used to sit behind TMDB, credits, OMDb, awards and recommendations,
            // all sharing twelve seconds. Even a successful plot was discarded if an extra timed out.
            var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
            Assert.True(firstAwait >= 0);
            Assert.StartsWith(show, body[firstAwait..]);
        }

        [Fact]
        public void The_view_releases_its_loading_callback_before_the_app_lifetime_disposes_it()
        {
            var closed = File.ReadLines(Path.Combine(RepositoryRoot(), "src", "UrDatabase.App",
                "Views", "MainWindow.axaml.cs")).Single(line => line.Contains("Closed +="));
            Assert.True(closed.IndexOf("DetailsView.Close()", StringComparison.Ordinal)
                        < closed.IndexOf("_cts.Cancel()", StringComparison.Ordinal));
        }

        [Fact]
        public void Metadata_refreshes_playback_controls_even_while_transfer_progress_owns_the_note()
        {
            var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "UrDatabase.App",
                "Views", "MovieDetailsView.axaml.cs"));
            var start = source.IndexOf("private void Bind(MovieDetailsVm vm)", StringComparison.Ordinal);
            var end = source.IndexOf("\n        private ", start + 1, StringComparison.Ordinal);
            var bind = source[start..end];
            Assert.True(bind.IndexOf("UpdatePlaybackControls();", StringComparison.Ordinal) >= 0);
            Assert.True(bind.IndexOf("UpdatePlaybackControls();", StringComparison.Ordinal)
                        < bind.IndexOf("if (_downloadCts is null", StringComparison.Ordinal));
        }

        private static string RepositoryRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "UrDatabase.sln"))) return dir.FullName;

            throw new InvalidOperationException("The source tree is required for the details wiring test.");
        }
    }
}
