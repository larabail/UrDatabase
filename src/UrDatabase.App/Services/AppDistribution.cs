using System.Reflection;

namespace UrDatabase.Services;

public enum DistributionChannel
{
    Standalone,
    MicrosoftStore
}

public static class AppDistribution
{
    public static DistributionChannel Current { get; } =
        typeof(AppDistribution).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "DistributionChannel").Value switch
        {
            "Standalone" => DistributionChannel.Standalone,
            "MicrosoftStore" => DistributionChannel.MicrosoftStore,
            var channel => throw new InvalidOperationException($"Unknown distribution channel: {channel}")
        };

    public static bool ShouldCheckForUpdates(bool configured, DistributionChannel? channel = null) =>
        configured && (channel ?? Current) == DistributionChannel.Standalone;
}
