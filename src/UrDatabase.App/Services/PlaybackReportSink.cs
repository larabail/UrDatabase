using System;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>
    /// Where a playback report goes. Implemented against Jellyfin by
    /// <see cref="JellyfinPlaybackSink"/>, and by a recorder in the tests.
    /// </summary>
    public interface IPlaybackReportSink
    {
        Task StartedAsync(long positionTicks, CancellationToken ct = default);
        Task ProgressAsync(long positionTicks, bool isPaused, CancellationToken ct = default);
        Task StoppedAsync(long positionTicks, CancellationToken ct = default);
    }

    /// <summary>
    /// One film's reports, addressed to the Jellyfin item they are about.
    /// </summary>
    /// <remarks>
    /// A thin binding of an item id to the client that already holds the token, rather than a
    /// second HTTP client of its own. The reports have to be signed in as the same user whose
    /// Continue watching row they are going to appear in, so building anything separate here would
    /// mean a second sign-in and a second place for the credential to live.
    /// </remarks>
    public sealed class JellyfinPlaybackSink : IPlaybackReportSink
    {
        private readonly JellyfinClient _client;
        private readonly string _itemId;

        public JellyfinPlaybackSink(JellyfinClient client, string itemId)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));

            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("A Jellyfin item id is required.", nameof(itemId));

            _itemId = itemId.Trim();
        }

        public Task StartedAsync(long positionTicks, CancellationToken ct = default) =>
            _client.ReportPlaybackStartAsync(_itemId, positionTicks, ct);

        public Task ProgressAsync(long positionTicks, bool isPaused, CancellationToken ct = default) =>
            _client.ReportPlaybackProgressAsync(_itemId, positionTicks, isPaused, ct);

        public Task StoppedAsync(long positionTicks, CancellationToken ct = default) =>
            _client.ReportPlaybackStoppedAsync(_itemId, positionTicks, ct);
    }
}
