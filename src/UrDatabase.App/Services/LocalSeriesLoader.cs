using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Reads one local programme's episodes out of the catalogue.
    /// </summary>
    /// <remarks>
    /// The local counterpart of <see cref="SeriesLoader"/>, and much the smaller: there is no
    /// server to ask and no cache to keep, because the catalogue already is the record. A scan
    /// wrote it; this reads it back.
    ///
    /// Out of the view for the usual reason — a query only a window can run cannot be tested — and
    /// synchronous for the reason <see cref="MovieRepository"/> gives: SQLite has no asynchronous
    /// I/O underneath, so the caller decides which thread pays.
    /// </remarks>
    public sealed class LocalSeriesLoader
    {
        /// <summary>
        /// Every episode with at least one file, and the best copy of each that the last scan
        /// still found — largest first, then most recently written, then by path, which is the
        /// order <see cref="PlayTargetResolver"/> picks a film's print in.
        /// </summary>
        private const string EpisodesSql = @"
SELECT e.id AS Id, e.series_id AS SeriesId, e.season_number AS SeasonNumber,
       e.episode_number AS EpisodeNumber, e.title AS Title,
       (SELECT f.file_path FROM files f
        WHERE f.episode_id = e.id AND f.missing_since IS NULL
        ORDER BY COALESCE(f.size_bytes, 0) DESC, COALESCE(f.updated_at, '') DESC, f.file_path ASC
        LIMIT 1) AS FilePath,
       (SELECT f.file_path FROM files f WHERE f.episode_id = e.id ORDER BY f.file_path ASC LIMIT 1) AS AnyPath
FROM episodes e
WHERE e.series_id = @series_id
  AND EXISTS (SELECT 1 FROM files f WHERE f.episode_id = e.id)";

        private readonly string _dbPath;

        public LocalSeriesLoader(string? dbPath)
        {
            _dbPath = dbPath ?? "";
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
    }
}
