using System.Xml.Linq;
using Xunit;

namespace UrDatabase.Tests;

public sealed class DiskRecoveryWiringTests
{
    [Fact]
    public void Recovery_is_available_from_drives_settings_and_local_movie_details()
    {
        foreach (var name in new[] { "DrivesWindow", "SetupWindow", "MovieDetailsView" })
            Assert.Contains(XDocument.Load(Source(name + ".axaml")).Descendants(),
                e => (string?)e.Attribute("Click") == "Relocate_Click");
    }

    [Fact]
    public void Recovery_confirms_before_commit_and_never_saves_configuration()
    {
        var source = File.ReadAllText(Source("DiskRecoveryWindow.axaml.cs"));
        Assert.True(source.IndexOf("MessageBoxWindow.ConfirmAsync(", StringComparison.Ordinal)
            < source.IndexOf("_recovery.CommitAsync(", StringComparison.Ordinal));
        Assert.Contains("DiskRecovery.FromFile(", source);
        Assert.Contains("_recovery.PreviewAsync(", source);
        Assert.DoesNotContain("ConfigStore.Save", source);
        Assert.DoesNotContain("File.Move", source);
        Assert.DoesNotContain("File.Delete", source);
    }

    [Fact]
    public void Playback_and_media_probing_use_the_shared_identity_resolver()
    {
        var movie = File.ReadAllText(Source("MovieDetailsView.axaml.cs"));
        var series = File.ReadAllText(Source("SeriesDetailsView.axaml.cs"));
        Assert.Contains("RefreshFileLocationAsync(current)", movie);
        Assert.Contains("FileLocations.ResolveFileAsync", movie);
        Assert.Contains("validateSource:", movie);
        Assert.Contains("validateDestination:", movie);
        Assert.Contains("_localLoader.ResolveFileAsync", series);
        Assert.Contains("await RecoverFile(fileId)", series);
        Assert.DoesNotContain("DescribeLinkRefusal(episode.FilePath)", series);
    }

    private static string Source(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UrDatabase.sln"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository not found."),
            "src/UrDatabase.App/Views", file);
    }
}
