using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>What a series is made of: its seasons, and its episodes.</summary>
    public sealed record SeriesContents(
        IReadOnlyList<JellyfinSeason> Seasons,
        IReadOnlyList<JellyfinEpisode> Episodes)
    {
        public static SeriesContents Empty { get; } =
            new(Array.Empty<JellyfinSeason>(), Array.Empty<JellyfinEpisode>());

        public bool IsEmpty => Seasons.Count == 0 && Episodes.Count == 0;
    }

    /// <summary>
    /// Fetches one series' seasons and episodes, and remembers them.
    ///
    /// Lazily, and that is the entire design. A sync pulls films and series and stops: a library of
    /// two hundred shows is several thousand episodes, and walking them all on every sync would
    /// spend minutes of somebody's evening filling in a screen they have not opened. This runs
    /// when a show is actually opened.
    ///
    /// Cached once fetched, for the same reason the films are cached: a laptop that is nowhere
    /// near the server can still read what it has already seen. So opening a show shows whatever
    /// was cached immediately and asks the server in the background, rather than showing a
    /// spinner over something the app already knows.
    ///
    /// Out of the window, like <see cref="LibraryLoader"/>, because both halves of that — reading
    /// SQLite and deciding what an unreachable server means — are rules rather than rendering.
    /// </summary>
    public sealed class SeriesLoader
    {
        private readonly string _dbPath;
        private readonly JellyfinClient? _client;
        private readonly Action<string>? _onFailure;

        /// <param name="client">
        /// The server. Null when none is configured, which makes this a cache reader and nothing
        /// else rather than something that fails.
        /// </param>
        /// <param name="onFailure">
        /// Where to send a failure that is not worth a dialog — an unreachable server, a database
        /// that would not open. The cached answer is still returned, so the screen stays usable.
        /// </param>
        public SeriesLoader(string dbPath, JellyfinClient? client = null, Action<string>? onFailure = null)
        {
            _dbPath = dbPath ?? "";
            _client = client;
            _onFailure = onFailure;
        }

        /// <summary>
        /// What the cache already holds for this series. Synchronous and cheap; the screen is
        /// filled from this before anything is asked of the network.
        /// </summary>
        public SeriesContents LoadCached(string? seriesId)
        {
            if (string.IsNullOrWhiteSpace(seriesId) || string.IsNullOrWhiteSpace(_dbPath))
                return SeriesContents.Empty;

            try
            {
                using var conn = Database.Open(_dbPath);
                return new SeriesContents(
                    JellyfinCache.LoadSeasons(conn, seriesId),
                    JellyfinCache.LoadEpisodes(conn, seriesId));
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", $"could not read the cached episodes of {seriesId}: {ex.Message}");
                return SeriesContents.Empty;
            }
        }

        /// <summary>
        /// Asks the server what this series is made of, writes the answer to the cache and returns
        /// it. Returns whatever was cached when the server cannot be reached, which is what keeps
        /// a show browsable on a train.
        /// </summary>
        public async Task<SeriesContents> RefreshAsync(string? seriesId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(seriesId)) return SeriesContents.Empty;
            if (_client is null) return LoadCached(seriesId);

            try
            {
                var seasons = await _client.GetSeasonsAsync(seriesId, ct);
                var episodes = await _client.GetEpisodesAsync(seriesId, ct);

                ct.ThrowIfCancellationRequested();

                Remember(seriesId, seasons, episodes);

                return new SeriesContents(seasons, episodes);
            }
            catch (OperationCanceledException)
            {
                // The screen was closed, or the window is. Not a failure and not ours to report.
                throw;
            }
            catch (JellyfinException ex)
            {
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"could not list {seriesId}: {ex.Message}"));

                var cached = LoadCached(seriesId);

                // Only worth saying when there is nothing to show. A screen already listing last
                // week's episodes does not need a sentence about the server every time it opens.
                if (cached.IsEmpty) _onFailure?.Invoke(ex.Message);

                return cached;
            }
        }

        /// <summary>
        /// Writes what the server said, and treats failing to write as unimportant. The episodes
        /// are on screen either way; a cache that could not be updated costs the next visit a
        /// request, not the user an error.
        /// </summary>
        private void Remember(
            string seriesId,
            IReadOnlyList<JellyfinSeason> seasons,
            IReadOnlyList<JellyfinEpisode> episodes)
        {
            if (string.IsNullOrWhiteSpace(_dbPath)) return;

            try
            {
                using var conn = Database.Open(_dbPath);
                JellyfinCache.ReplaceEpisodes(conn, seriesId, seasons, episodes);
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", $"could not cache the episodes of {seriesId}: {ex.Message}");
            }
        }
    }
}
