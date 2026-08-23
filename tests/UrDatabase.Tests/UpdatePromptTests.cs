using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class UpdatePromptTests
    {
        private static readonly UpdateAsset Build = new(
            "UrDatabase-0.11.0-osx-arm64.dmg",
            "https://github.com/larabail/UrDatabase/releases/download/v0.11.0/UrDatabase-0.11.0-osx-arm64.dmg",
            83_886_080);

        private static AvailableUpdate Update(string version = "0.11.0", UpdateAsset? asset = null) =>
            new(version, $"v{version}", $"{UpdateFeed.ReleasesPageUrl}/tag/v{version}", asset ?? Build);

        [Fact]
        public void Nothing_found_means_no_banner()
        {
            Assert.False(UpdatePrompt.ShouldShow(null, null));
            Assert.False(UpdatePrompt.ShouldShow(null, "0.9.0"));
        }

        [Fact]
        public void An_update_nobody_has_dismissed_is_shown()
        {
            Assert.True(UpdatePrompt.ShouldShow(Update(), null));
        }

        [Fact]
        public void A_version_that_was_dismissed_stays_dismissed()
        {
            Assert.False(UpdatePrompt.ShouldShow(Update("0.11.0"), "0.11.0"));
        }

        [Fact]
        public void A_newer_version_gets_through_a_dismissal()
        {
            Assert.True(UpdatePrompt.ShouldShow(Update("0.12.0"), "0.11.0"));
        }

        [Fact]
        public void An_older_release_reappearing_at_the_top_of_the_feed_does_not_get_through()
        {
            // Compared against what was skipped rather than merely tested for equality with it, so
            // somebody who dismissed 0.12.0 is not shown 0.11.9 the day an old release is edited.
            Assert.False(UpdatePrompt.ShouldShow(Update("0.11.9"), "0.12.0"));
        }

        [Fact]
        public void The_headline_names_the_app_and_the_version_and_needs_no_context()
        {
            Assert.Equal("UrDatabase 0.11.0 is available", UpdatePrompt.Headline(Update()));
        }

        [Fact]
        public void The_detail_says_what_is_running_the_file_and_its_size()
        {
            var detail = UpdatePrompt.Detail(Update(), "0.10.0");

            Assert.Contains("You have 0.10.0", detail);
            Assert.Contains("UrDatabase-0.11.0-osx-arm64.dmg", detail);
            Assert.Contains("80 MB", detail);
        }

        [Fact]
        public void The_detail_admits_that_installing_it_is_not_something_the_app_does()
        {
            // The running app cannot replace itself on either platform, and a prompt that implies
            // otherwise is one whose next appearance is believed less.
            Assert.Contains("yours to do", UpdatePrompt.Detail(Update(), "0.10.0"));
        }

        [Fact]
        public void A_running_version_nobody_can_read_is_left_out_rather_than_printed_raw()
        {
            var detail = UpdatePrompt.Detail(Update(), "nightly");

            Assert.DoesNotContain("nightly", detail);
            Assert.StartsWith("Downloads ", detail);
        }

        [Fact]
        public void With_no_build_for_this_machine_the_button_and_the_words_both_say_website()
        {
            var update = new AvailableUpdate("0.11.0", "v0.11.0", UpdateFeed.ReleasesPageUrl, null);

            Assert.Equal(UpdatePrompt.WebsiteAction, UpdatePrompt.ActionText(update));
            Assert.Contains("downloads page", UpdatePrompt.Detail(update, "0.10.0"));
        }

        [Fact]
        public void With_a_build_the_button_offers_to_fetch_it()
        {
            Assert.Equal(UpdatePrompt.DownloadAction, UpdatePrompt.ActionText(Update()));
        }

        [Fact]
        public void An_asset_of_unknown_size_simply_says_nothing_about_the_size()
        {
            var noSize = Update(asset: new UpdateAsset("UrDatabase-0.11.0-win-x64.zip", Build.Url, 0));

            var detail = UpdatePrompt.Detail(noSize, "0.10.0");

            Assert.Contains("UrDatabase-0.11.0-win-x64.zip", detail);
            Assert.DoesNotContain("(0 B)", detail);
        }

        [Fact]
        public void The_progress_line_is_short_because_it_is_rewritten_several_times_a_second()
        {
            Assert.Equal("Downloading… 1.0 KB of 2.0 KB (50%)", UpdatePrompt.Downloading(new UpdateProgress(1024, 2048)));
        }

        [Fact]
        public void Once_it_has_landed_the_words_name_the_file_and_the_next_step()
        {
            var landed = UpdatePrompt.Downloaded("/Users/someone/Downloads/UrDatabase-0.11.0-osx-arm64.dmg");

            Assert.Contains("/Users/someone/Downloads/UrDatabase-0.11.0-osx-arm64.dmg", landed);
            Assert.Contains("Quit UrDatabase", landed);
        }

        [Fact]
        public void A_failed_fetch_always_ends_somewhere_the_build_can_still_be_got()
        {
            // A failure that leaves somebody with nothing to press is a dead end, and the website
            // is where they would have gone had the app never offered.
            Assert.Contains("website", UpdatePrompt.DownloadFailed("That download was interrupted."));
            Assert.Contains("That download was interrupted.", UpdatePrompt.DownloadFailed("That download was interrupted."));
            Assert.Contains("website", UpdatePrompt.DownloadFailed(null));
        }
    }
}
