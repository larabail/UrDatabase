using System;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class AppVersionTests
    {
        [Theory]
        [InlineData("0.11.0", 0, 11, 0)]
        [InlineData("v0.11.0", 0, 11, 0)]
        [InlineData("V1.2.3", 1, 2, 3)]
        [InlineData(" 0.11.0 ", 0, 11, 0)]
        [InlineData("0.11", 0, 11, 0)]
        [InlineData("2", 2, 0, 0)]
        public void Reads_the_shapes_a_tag_or_a_version_actually_takes(string raw, int major, int minor, int patch)
        {
            Assert.Equal((major, minor, patch), AppVersion.Parse(raw));
        }

        [Fact]
        public void Drops_the_commit_the_compiler_appends_to_the_informational_version()
        {
            Assert.Equal((0, 11, 0), AppVersion.Parse("0.11.0+2f9c1ab"));
        }

        [Fact]
        public void Drops_a_preview_tail_because_it_takes_no_part_in_ordering()
        {
            Assert.Equal((0, 11, 0), AppVersion.Parse("0.11.0-preview.2"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("v")]
        [InlineData("nightly")]
        [InlineData("0.11.x")]
        [InlineData("0.11.0px")]
        [InlineData("0..1")]
        public void Refuses_anything_that_is_not_a_version(string? raw)
        {
            Assert.Null(AppVersion.Parse(raw));
        }

        [Fact]
        public void Refuses_a_four_part_number_rather_than_silently_truncating_it()
        {
            // An assembly version, not a release. Reading it as 0.11.0 would make two different
            // things compare equal.
            Assert.Null(AppVersion.Parse("0.11.0.4"));
        }

        [Fact]
        public void The_tenth_minor_release_is_newer_than_the_ninth()
        {
            // The whole reason versions are compared number by number: as text, "0.9.0" sorts
            // after "0.10.0" and everybody on the newest build is told they are behind.
            Assert.True(AppVersion.IsNewer("0.10.0", "0.9.0"));
            Assert.False(AppVersion.IsNewer("0.9.0", "0.10.0"));
        }

        [Theory]
        [InlineData("0.11.0", "0.10.0", true)]
        [InlineData("1.0.0", "0.99.99", true)]
        [InlineData("0.10.1", "0.10.0", true)]
        [InlineData("0.10.0", "0.10.0", false)]
        [InlineData("0.10.0", "0.10.1", false)]
        [InlineData("v0.11.0", "0.11.0", false)]
        public void Compares_a_candidate_against_what_is_running(string candidate, string running, bool newer)
        {
            Assert.Equal(newer, AppVersion.IsNewer(candidate, running));
        }

        [Fact]
        public void A_version_nobody_can_read_is_never_newer_and_is_never_behind()
        {
            // Both halves matter: a build with no usable version must not be told every release is
            // an upgrade, and a tag nobody can parse must not be offered as one.
            Assert.False(AppVersion.IsNewer("0.11.0", "nightly"));
            Assert.False(AppVersion.IsNewer("nightly", "0.11.0"));
            Assert.False(AppVersion.IsNewer(null, null));
        }

        [Fact]
        public void Normalises_to_the_three_part_form_the_rest_of_the_app_shows()
        {
            Assert.Equal("0.11.0", AppVersion.Text("v0.11"));
            Assert.Equal("1.2.3", AppVersion.Text("1.2.3+abc"));
            Assert.Null(AppVersion.Text("not a version"));
        }

        [Fact]
        public void The_running_version_comes_from_the_assembly_and_is_a_version()
        {
            // Not asserted as a literal: Directory.Build.props is the only place the number is
            // allowed to live, and a test naming it would have to be edited at every release.
            Assert.NotNull(AppVersion.Parse(AppVersion.Current));
            Assert.Equal(AppVersion.Current, AppVersion.Text(AppVersion.Current));
        }

        [Fact]
        public void The_running_version_prefers_the_informational_one_the_build_wrote()
        {
            Assert.Equal("0.11.0", AppVersion.Resolve("0.11.0+2f9c1ab", new Version(0, 11, 0, 0)));
        }

        [Fact]
        public void Falls_back_to_the_assembly_version_cut_to_three_parts()
        {
            // A four part number is what an assembly version always is, and what Parse refuses.
            // Handed over whole it would resolve to the unknown version on every build.
            Assert.Equal("0.11.0", AppVersion.Resolve(null, new Version(0, 11, 0, 4)));
            Assert.Equal("0.11.0", AppVersion.Resolve("not a version", new Version(0, 11, 0, 4)));
        }

        [Fact]
        public void An_assembly_with_nothing_usable_on_it_resolves_to_the_unknown_version()
        {
            // Which parses as nothing, so such a build is never told it is behind. Announcing an
            // upgrade over a version nobody established is a guess dressed as a fact.
            Assert.Equal(AppVersion.Unknown, AppVersion.Resolve(null, null));
            Assert.Null(AppVersion.Parse(AppVersion.Unknown));
            Assert.False(AppVersion.IsNewer("99.0.0", AppVersion.Resolve(null, null)));
        }
    }
}
