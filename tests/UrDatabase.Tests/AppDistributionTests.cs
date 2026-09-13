using System.Reflection;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public sealed class AppDistributionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urdb-distribution-" + Guid.NewGuid().ToString("N"));
    private readonly TempLog _log = new();

    [Theory]
    [InlineData(DistributionChannel.Standalone, true, true)]
    [InlineData(DistributionChannel.Standalone, false, false)]
    [InlineData(DistributionChannel.MicrosoftStore, true, false)]
    [InlineData(DistributionChannel.MicrosoftStore, false, false)]
    public void Store_updates_cannot_be_enabled_by_configuration(
        DistributionChannel channel, bool configured, bool expected)
    {
        Assert.Equal(expected, AppDistribution.ShouldCheckForUpdates(configured, channel));
    }

    [Fact]
    public void Build_channel_is_embedded_in_the_app_not_read_from_user_configuration()
    {
        var expected = typeof(AppDistributionTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "ExpectedDistributionChannel").Value;
        Assert.Equal(expected, AppDistribution.Current.ToString());
        Assert.Equal(expected == "Standalone", AppDistribution.ShouldCheckForUpdates(true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_does_not_request_or_offer_a_cached_github_update(bool cached)
    {
        var path = Path.Combine(_dir, UpdateState.FileName);
        if (cached)
        {
            Directory.CreateDirectory(_dir);
            var handler = FakeHttpMessageHandler.Json("""
                [{"tag_name":"v99.0.0","draft":false,"prerelease":false,"assets":[
                  {"name":"UrDatabase-99.0.0-win-x64.zip",
                   "browser_download_url":"https://github.com/larabail/UrDatabase/releases/download/v99.0.0/UrDatabase-99.0.0-win-x64.zip",
                   "size":1024}]}]
                """);
            using var standalone = new UpdateService("0.1.0", "win-x64", handler, path,
                distribution: DistributionChannel.Standalone);
            Assert.NotNull(await standalone.CheckAsync());
        }

        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var noRequests = FakeHttpMessageHandler.Json("[]");
        using var store = new UpdateService("0.1.0", "win-x64", noRequests, path,
            distribution: DistributionChannel.MicrosoftStore);

        Assert.Null(await store.CheckAsync());
        Assert.Equal(0, noRequests.CallCount);
        Assert.Equal(before, File.Exists(path) ? File.ReadAllBytes(path) : null);
        if (!cached) Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public async Task Current_build_channel_controls_the_service_by_default()
    {
        var handler = FakeHttpMessageHandler.Json("[]");
        using var service = new UpdateService(handler: handler, statePath: Path.Combine(_dir, UpdateState.FileName));
        await service.CheckAsync();
        Assert.Equal(AppDistribution.Current == DistributionChannel.Standalone ? 1 : 0, handler.CallCount);
    }

    [Fact]
    public void Store_first_launch_does_not_read_or_replace_the_standalone_catalogue_or_config()
    {
        var standalone = PlatformPaths.DefaultAppDataRoot(_dir, DistributionChannel.Standalone);
        var store = PlatformPaths.DefaultAppDataRoot(_dir, DistributionChannel.MicrosoftStore);
        Assert.Equal(Path.Combine(_dir, "UrDatabase"), standalone);
        Assert.Equal(Path.Combine(_dir, "UrDatabase.Store"), store);
        Directory.CreateDirectory(standalone);
        var config = Path.Combine(standalone, AppConfig.FileName);
        var catalogue = Path.Combine(standalone, "movies.db");
        File.WriteAllText(config, """{"SetupCompleted":true,"WatchFolders":["private-folder"]}""");
        File.WriteAllText(catalogue, "existing catalogue sentinel");
        var before = File.ReadAllBytes(config);
        var app = Path.Combine(_dir, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, AppConfig.ExampleFileName), """{"WatchFolders":[]}""");

        var loaded = AppConfig.Load(null, store, app);

        Assert.False(loaded.SetupCompleted);
        Assert.DoesNotContain("private-folder", loaded.WatchFolders);
        Assert.Equal(Path.Combine(store, AppConfig.FileName), loaded.SourcePath);
        Assert.Equal(before, File.ReadAllBytes(config));
        Assert.Equal("existing catalogue sentinel", File.ReadAllText(catalogue));
        Assert.False(File.Exists(Path.Combine(store, "movies.db")));
        Assert.True(File.Exists(Path.Combine(store, AppConfig.FileName)));
    }

    public void Dispose()
    {
        _log.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
