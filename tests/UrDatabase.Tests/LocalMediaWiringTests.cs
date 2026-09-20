using System;
using System.IO;
using Xunit;

namespace UrDatabase.Tests;

public sealed class LocalMediaWiringTests
{
    [Fact]
    public void Binding_an_opened_or_enriched_page_starts_on_demand_media_loading()
    {
        var source = Source("MovieDetailsView.axaml.cs");
        Assert.Contains("RefreshLocalMediaAsync(vm)", Method(source, "private void Bind("));
        Assert.Contains("Bind(vm)", Method(source, "public Task ShowAsync("));
        Assert.Contains("Bind(vm)", Method(source, "public void Refresh("));
        Assert.Contains("_mediaLoad?.Dispose()", Method(source, "public void Close("));
    }

    [Fact]
    public void Choosing_a_file_or_finishing_a_download_rebinds_the_media_badges()
    {
        var source = Source("MovieDetailsView.axaml.cs");
        Assert.Contains("Bind(vm)", Method(source, "private async void LinkFile_Click("));
        Assert.Contains("Bind(vm)", Method(source, "private async void Download_Click("));
        Assert.Contains("RefreshLocalMediaAsync(vm, force: true)", Method(source, "private async void Refresh_Click("));
    }

    [Fact]
    public void Initial_local_file_resolution_does_not_overwrite_probed_tracks_with_filename_hints()
    {
        Assert.DoesNotContain("LocalMedia.Describe", Source("MainWindow.axaml.cs"));
    }

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var opening = source.IndexOf('{', start);
        var depth = 1;
        var end = opening + 1;
        while (depth > 0 && end < source.Length)
        {
            if (source[end] == '{') depth++;
            if (source[end] == '}') depth--;
            end++;
        }
        return source[start..end];
    }

    private static string Source(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "UrDatabase.sln")))
                return File.ReadAllText(Path.Combine(dir.FullName, "src", "UrDatabase.App", "Views", name));
        throw new InvalidOperationException("The source tree is required for media wiring tests.");
    }
}
