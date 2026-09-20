using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public sealed class LocalMediaFallbackTests
{
    [Fact]
    public void Filename_codecs_and_languages_are_not_presented_as_measured_tracks()
    {
        var info = LocalMedia.Describe("Film.2020.2160p.HDR10.HEVC.TrueHD.7.1.ENGLISH.mkv", _ => 2048);
        var flags = MediaFlags.For(info);

        Assert.Contains("filename", flags.Single(flag => flag.Text == "HEVC").Tip);
        Assert.Contains("filename", flags.Single(flag => flag.Kind == MediaFlagKind.Sound).Tip);
        Assert.Contains("filename", flags.Single(flag => flag.Kind == MediaFlagKind.Language).Tip);
        Assert.Contains("filename", flags.Single(flag => flag.Text == "HDR10").Tip);
        Assert.DoesNotContain("filename", flags.Single(flag => flag.Text == "2 KB").Tip);
    }

    [Theory]
    [InlineData(1920, null)]
    [InlineData(null, 1080)]
    public void Partially_measured_dimensions_are_not_claimed_to_come_from_a_filename(int? width, int? height)
    {
        var flags = MediaFlags.For(new MediaInfo { Width = width, Height = height });
        Assert.DoesNotContain("filename", Assert.Single(flags).Tip);
    }
}
