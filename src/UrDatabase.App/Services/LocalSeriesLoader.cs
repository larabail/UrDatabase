using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Reads one local programme's episodes out of the catalogue, and fills in what TMDB says
    /// about them.
    /// </summary>
    /// <remarks>
    /// The local counterpart of <see cref="SeriesLoader"/>. The catalogue is the record — a scan
    /// wrote the episodes, and the background pass wrote what TMDB says the programme is — so the
    /// screen reads it back first and only then asks TMDB about seasons nobody has described yet,
    /// writing the answer into the same rows so the next visit asks nothing.
    ///
    /// Out of the view for the usual reason — a query only a window can run cannot be tested — and
    /// the reads are synchronous for the reason <see cref="MovieRepository"/> gives: SQLite has no
    /// asynchronous I/O underneath, so the caller decides which thread pays.
    /// </remarks>
    public sealed class LocalSeriesLoader
    {
        /// <summary>
        /// Every episode with at least one file, and the best copy of each that the last scan
        /// still found — largest first, then most recently written, then by path, which is the
        /// order <see cref="PlayTargetResolver"/> picks a film's print in — with whatever TMDB has
        /// said about it.
        /// </summary>
        private const string EpisodesSql = @"
SELECT e.id AS Id, e.series_id AS SeriesId, e.season_number AS SeasonNumber,
       e.episode_number AS EpisodeNumber, e.title AS Title,
       e.tmdb_title AS TmdbTitle, e.overview AS Overview, e.runtime_minutes AS RuntimeMinutes,
       e.air_date AS AirDate,
       (SELECT f.file_path FROM files f
        WHERE f.episode_id = e.id AND f.missing_since IS NULL
        ORDER BY COALESCE(f.size_bytes, 0) DESC, COALESCE(f.updated_at, '') DESC, f.file_path ASC
        LIMIT 1) AS FilePath,
       (SELECT f.file_path FROM files f WHERE f.episode_id = e.id ORDER BY f.file_path ASC LIMIT 1) AS AnyPath
FROM episodes e
WHERE e.series_id = @series_id
  AND EXISTS (SELECT 1 FROM files f WHERE f.episode_id = e.id)";

        private readonly string _dbPath;
        private readonly Func<TmdbService?>? _tmdb;

        /// <param name="tmdb">
        /// Builds a TMDB client for one refresh, or returns null when no key is configured. Null
        /// itself makes this a catalogue reader and nothing else, which is what it was before
        /// programmes could be identified.
        /// </param>
        public LocalSeriesLoader(string? dbPath, Func<TmdbService?>? tmdb = null)
        {
            _dbPath = dbPath ?? "";
            _tmdb = tmdb;
        }

        /// <summary>
        /// The episodes of one programme, in no particular order —
        /// <see cref="SeriesGrouping.GroupLocal"/> decides that. Throws whatever SQLite raises:
        /// the screen says the list could not be read rather than that the programme is empty.
        /// </summary>
        public IReadOnlyList<LocalEpisode> Load(long seriesId)
        {
            if (seriesId <= 0 || string.IsNullOrWhiteSpace(_dbPath)) return Array.Empty<LocalEpisode>();

            using var conn = Database.Open(_dbPath);
            return conn.Query<LocalEpisode>(EpisodesSql, new { series_id = seriesId }).ToList();
        }

        /// <summary>
        /// What the catalogue knows about the programme itself — TMDB's description of it, when it
        /// has been identified. Null when there is no such row.
        /// </summary>
        public LocalSeriesDetails? LoadDetails(long seriesId)
        {
            if (seriesId <= 0 || string.IsNullOrWhiteSpace(_dbPath)) return null;

            using var conn = Database.Open(_dbPath);
            return LocalSeriesLookup.Read(conn, seriesId);
        }

        /// <summary>
        /// Asks TMDB about the seasons of an identified programme that have not been described
        /// yet — every season, when <paramref name="force"/> is set — and returns the episodes as
        /// they now read. Null when nothing was asked: no key, no identification, or every season
        /// already described, which is the ordinary second visit and costs no request at all.
        /// </summary>
        /// <remarks>
        /// The second half of the shape <see cref="SeriesLoader"/> has: the screen shows
        /// <see cref="Load"/> first and this afterwards, so a programme opened before is complete
        /// the moment it opens and a new one fills its episode names in as they arrive.
        /// </remarks>
        public async Task<IReadOnlyList<LocalEpisode>?> RefreshEpisodesAsync(
            long seriesId, bool force, CancellationToken ct)
        {
            if (_tmdb is null) return null;

            using var tmdb = _tmdb();
            if (tmdb is null) return null;

            return await RefreshEpisodesAsync(tmdb, seriesId, force, ct).ConfigureAwait(false);
        }

        /// <summary><see cref="RefreshEpisodesAsync(long, bool, CancellationToken)"/> with the client supplied.</summary>
        public async Task<IReadOnlyList<LocalEpisode>?> RefreshEpisodesAsync(
            TmdbService tmdb, long seriesId, bool force, CancellationToken ct)
        {
            if (seriesId <= 0 || string.IsNullOrWhiteSpace(_dbPath) || !tmdb.HasApiKey) return null;

            using var conn = Database.Open(_dbPath);
            var asked = await LocalSeriesLookup.RefreshSeasonsAsync(tmdb, conn, seriesId, force, ct).ConfigureAwait(false);
            if (asked == 0) return null;

            return conn.Query<LocalEpisode>(EpisodesSql, new { series_id = seriesId }).ToList();
        }

        /// <summary>
        /// Records the programme a person chose with Wrong series? and describes it. Null when no
        /// TMDB key is configured, which the screen has already refused to get this far without.
        /// </summary>
        /// <remarks>See <see cref="SeriesMatchCorrection"/> for what it writes and why.</remarks>
        /// <param name="onRecorded">See <see cref="SeriesMatchCorrection.ApplyAsync"/>.</param>
        public async Task<SeriesCorrection?> CorrectAsync(
            long seriesId, SeriesChoice choice, bool downloadPosters, CancellationToken ct, Action? onRecorded = null)
        {
            if (_tmdb is null || seriesId <= 0 || string.IsNullOrWhiteSpace(_dbPath)) return null;

            using var tmdb = _tmdb();
            if (tmdb is null || !tmdb.HasApiKey) return null;

            using var conn = Database.Open(_dbPath);
            return await SeriesMatchCorrection.ApplyAsync(tmdb, conn, seriesId, choice, downloadPosters, ct, onRecorded)
                .ConfigureAwait(false);
        }
    }
}
