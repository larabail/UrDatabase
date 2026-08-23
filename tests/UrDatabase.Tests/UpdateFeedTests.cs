using System.Collections.Generic;
using System.Runtime.InteropServices;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class UpdateFeedTests
    {
        [Theory]
        [InlineData("UrDatabase-0.11.0-osx-arm64.dmg", "osx-arm64")]
        [InlineData("UrDatabase-0.11.0-osx-x64.dmg", "osx-x64")]
        [InlineData("UrDatabase-0.11.0-win-x64.zip", "win-x64")]
        [InlineData("UrDatabase-0.2.0-osx-arm64.zip", "osx-arm64")]
        public void Recognises_the_builds_the_release_workflow_publishes(string name, string platform)
        {
            Assert.Equal(platform, UpdateFeed.AssetPlatform(name));
        }

        [Fact]
        public void A_checksum_beside_a_build_is_not_a_build()
        {
            // It also ends in "-osx-arm64.dmg" as far as a naive search is concerned, and handing
            // somebody a 90-byte text file as the new version is worse than handing them nothing.
            Assert.Null(UpdateFeed.AssetPlatform("UrDatabase-0.11.0-osx-arm64.dmg.sha256"));
            Assert.Null(UpdateFeed.AssetPlatform("UrDatabase-0.11.0-win-x64.zip.sig"));
        }

        [Fact]
        public void The_intel_mac_build_is_not_mistaken_for_the_windows_one()
        {
            // Matched on the whole "-<rid><container>" tail. Matching on the architecture alone
            // would make "-x64.zip" belong to both, and whichever was listed first would win.
            Assert.Equal("osx-x64", UpdateFeed.AssetPlatform("UrDatabase-0.11.0-osx-x64.zip"));
            Assert.Equal("win-x64", UpdateFeed.AssetPlatform("UrDatabase-0.11.0-win-x64.zip"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("source-code.tar.gz")]
        [InlineData("UrDatabase-0.11.0-linux-x64.tar.gz")]
        public void Anything_else_is_not_a_build(string? name)
        {
            Assert.Null(UpdateFeed.AssetPlatform(name));
        }

        [Fact]
        public void An_apple_silicon_machine_is_offered_the_native_build_even_under_rosetta()
        {
            // The question asked is what this computer is, not what this process is. Asking the
            // process would keep a Rosetta user on the Intel build for ever.
            Assert.Equal("osx-arm64", UpdateFeed.RuntimeIdentifier(true, false, Architecture.Arm64));
            Assert.Equal("osx-x64", UpdateFeed.RuntimeIdentifier(true, false, Architecture.X64));
        }

        [Fact]
        public void Windows_gets_the_only_windows_build_there_is_whatever_it_runs_on()
        {
            Assert.Equal("win-x64", UpdateFeed.RuntimeIdentifier(false, true, Architecture.X64));
            Assert.Equal("win-x64", UpdateFeed.RuntimeIdentifier(false, true, Architecture.Arm64));
        }

        [Fact]
        public void Anywhere_nothing_is_published_for_has_no_runtime_identifier()
        {
            Assert.Null(UpdateFeed.RuntimeIdentifier(false, false, Architecture.X64));
        }

        [Fact]
        public void Picks_the_newest_release_carrying_a_build_for_this_machine()
        {
            var payload = new List<GithubRelease?>
            {
                Release("v0.9.0", "UrDatabase-0.9.0-osx-arm64.dmg"),
                Release("v0.11.0", "UrDatabase-0.11.0-osx-arm64.dmg"),
                Release("v0.10.0", "UrDatabase-0.10.0-osx-arm64.dmg")
            };

            var update = UpdateFeed.Newest(payload, "0.10.0", "osx-arm64");

            Assert.NotNull(update);
            Assert.Equal("0.11.0", update!.Version);
            Assert.Equal("v0.11.0", update.Tag);
            Assert.Equal("UrDatabase-0.11.0-osx-arm64.dmg", update.Asset!.Value.Name);
        }

        [Fact]
        public void Orders_by_version_and_not_by_the_order_the_api_listed_them()
        {
            // GitHub returns releases in the order they were created, which is usually but not
            // reliably version order: a fix tagged after a larger release that was prepared
            // earlier arrives out of sequence, and taking the first would offer a downgrade.
            var payload = new List<GithubRelease?>
            {
                Release("v0.9.1", "UrDatabase-0.9.1-win-x64.zip"),
                Release("v0.10.0", "UrDatabase-0.10.0-win-x64.zip")
            };

            Assert.Equal("0.10.0", UpdateFeed.Newest(payload, "0.9.0", "win-x64")!.Version);
        }

        [Fact]
        public void There_is_no_update_when_the_newest_release_is_the_one_running()
        {
            var payload = new List<GithubRelease?> { Release("v0.11.0", "UrDatabase-0.11.0-win-x64.zip") };

            Assert.Null(UpdateFeed.Newest(payload, "0.11.0", "win-x64"));
            Assert.Null(UpdateFeed.Newest(payload, "0.12.0", "win-x64"));
        }

        [Fact]
        public void A_release_with_no_build_for_this_machine_is_not_announced()
        {
            // Announcing it would send a Mac user to a page that offers them the previous release,
            // which is a worse answer than saying nothing.
            var payload = new List<GithubRelease?>
            {
                Release("v0.11.0", "UrDatabase-0.11.0-win-x64.zip"),
                Release("v0.10.0", "UrDatabase-0.10.0-osx-arm64.dmg")
            };

            Assert.Null(UpdateFeed.Newest(payload, "0.10.0", "osx-arm64"));
        }

        [Fact]
        public void A_platform_this_app_does_not_publish_for_hears_about_the_release_but_gets_no_file()
        {
            // A source build on Linux. The banner is still honest — there is a newer version — and
            // the button opens the website, because there is nothing here to hand it.
            var payload = new List<GithubRelease?> { Release("v0.11.0", "UrDatabase-0.11.0-win-x64.zip") };

            var update = UpdateFeed.Newest(payload, "0.10.0", null);

            Assert.NotNull(update);
            Assert.Equal("0.11.0", update!.Version);
            Assert.Null(update.Asset);
        }

        [Fact]
        public void Drafts_and_pre_releases_are_ignored()
        {
            // A draft is invisible to anyone without push access, so offering it would offer a
            // download that 404s. A pre-release is visible but is not something this pipeline
            // publishes, so one appearing was deliberately not meant for everybody.
            var payload = new List<GithubRelease?>
            {
                Release("v0.13.0", "UrDatabase-0.13.0-win-x64.zip", draft: true),
                Release("v0.12.0", "UrDatabase-0.12.0-win-x64.zip", prerelease: true),
                Release("v0.11.0", "UrDatabase-0.11.0-win-x64.zip")
            };

            Assert.Equal("0.11.0", UpdateFeed.Newest(payload, "0.10.0", "win-x64")!.Version);
        }

        [Fact]
        public void A_tag_that_is_not_a_version_is_not_a_release_to_offer()
        {
            var payload = new List<GithubRelease?>
            {
                Release("nightly", "UrDatabase-9.9.9-win-x64.zip"),
                Release("v0.11.0", "UrDatabase-0.11.0-win-x64.zip")
            };

            Assert.Equal("0.11.0", UpdateFeed.Newest(payload, "0.10.0", "win-x64")!.Version);
        }

        [Fact]
        public void An_asset_that_is_not_served_over_https_is_never_downloaded()
        {
            // The asset URL is data from a server like any other, and it is handed to an HTTP
            // client and then to the operating system.
            var payload = new List<GithubRelease?>
            {
                new()
                {
                    TagName = "v0.11.0",
                    Assets = new List<GithubAsset?>
                    {
                        new()
                        {
                            Name = "UrDatabase-0.11.0-win-x64.zip",
                            BrowserDownloadUrl = "http://example.invalid/build.zip",
                            Size = 10
                        }
                    }
                }
            };

            Assert.Null(UpdateFeed.Newest(payload, "0.10.0", "win-x64"));
        }

        [Fact]
        public void A_release_page_that_is_not_on_github_is_replaced_with_one_that_is()
        {
            // html_url is a string from a server, and it is handed to the machine's URL opener.
            var release = Release("v0.11.0", "UrDatabase-0.11.0-win-x64.zip");
            release.HtmlUrl = "javascript:alert(1)";

            var update = UpdateFeed.Newest(new List<GithubRelease?> { release }, "0.10.0", "win-x64");

            Assert.Equal($"{UpdateFeed.ReleasesPageUrl}/tag/v0.11.0", update!.Page);
        }

        [Fact]
        public void An_empty_or_absent_payload_is_simply_no_update()
        {
            Assert.Null(UpdateFeed.Newest(null, "0.10.0", "win-x64"));
            Assert.Null(UpdateFeed.Newest(new List<GithubRelease?>(), "0.10.0", "win-x64"));
            Assert.Null(UpdateFeed.Newest(new List<GithubRelease?> { null }, "0.10.0", "win-x64"));
        }

        [Fact]
        public void The_feed_and_the_downloads_page_agree_on_which_repository_they_describe()
        {
            // The same three runtime identifiers name a build in the workflow, in the filename,
            // on the downloads site and here. A fourth naming scheme is how they drift apart.
            Assert.Contains(UpdateFeed.Repo, UpdateFeed.ReleasesApiUrl);
            Assert.Contains(UpdateFeed.Repo, UpdateFeed.ReleasesPageUrl);
            Assert.Equal(new[] { "osx-arm64", "osx-x64", "win-x64" }, UpdateFeed.Platforms);
        }

        private static GithubRelease Release(
            string tag,
            string assetName,
            bool draft = false,
            bool prerelease = false) => new()
            {
                TagName = tag,
                HtmlUrl = $"https://github.com/{UpdateFeed.Repo}/releases/tag/{tag}",
                Draft = draft,
                Prerelease = prerelease,
                Assets = new List<GithubAsset?>
                {
                    new()
                    {
                        Name = assetName,
                        BrowserDownloadUrl = $"https://github.com/{UpdateFeed.Repo}/releases/download/{tag}/{assetName}",
                        Size = 83_000_000
                    }
                }
            };
    }
}
