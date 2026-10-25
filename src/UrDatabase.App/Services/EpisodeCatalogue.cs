using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// The local programmes and their episodes, in memory for the length of one scan, so a file
    /// read as an episode can be filed without a query per file.
    /// </summary>
    /// <remarks>
    /// The same bargain <see cref="MovieIndex"/> strikes for films, and it reuses that index for the
    /// programmes themselves: "The Office", "the office" and "The.Office" are one programme by
    /// exactly the rules that make them one film, and a folder named <c>Fargo (2014)</c> fills in
    /// the year of a programme first catalogued as plain <c>Fargo</c> rather than forking it.
    ///
    /// Pure apart from <see cref="LoadAsync"/>, so the filing rules are testable without SQLite.
    /// </remarks>
    public sealed class EpisodeCatalogue
    {
        private readonly MovieIndex _series = new();
        private readonly Dictionary<(long SeriesId, int Season, int Episode), long> _episodes = new();
        private readonly HashSet<long> _titled = new();

        /// <summary>How many programmes the catalogue knows, including any this scan added.</summary>
        public int SeriesCount => _series.Count;

        /// <summary>Registers a programme the catalogue already holds. Expected in id order.</summary>
        public void AddSeries(long id, string? title, int? year) => _series.Add(id, title, year);

        /// <summary>
        /// Files a programme the catalogue already holds under a second name — the one a person
        /// gave it by choosing its TMDB match. See <see cref="MovieIndex.AddAlias"/>.
        /// </summary>
        public void AddSeriesAlias(long id, string? title, int? year) => _series.AddAlias(id, title, year);

        /// <summary>Registers an episode the catalogue already holds.</summary>
        public void AddEpisode(long id, long seriesId, int? season, int episode, bool hasTitle)
        {
            _episodes.TryAdd((seriesId, SeasonKey(season), episode), id);
            if (hasTitle) _titled.Add(id);
        }

        /// <summary>
        /// The programme a parsed episode belongs to, when there is one already.
        /// </summary>
        /// <param name="yearIsNewInformation">
        /// Set when the match had no year and this path supplies one; the caller writes it back.
        /// </param>
        public bool TryResolveSeries(ParsedEpisode episode, out long id, out bool yearIsNewInformation) =>
            _series.TryResolve(new ParsedMedia(episode.SeriesTitle, episode.SeriesYear), out id, out yearIsNewInformation);

        /// <summary>Keeps the index in step with a year the caller has just written.</summary>
        public void SetSeriesYear(long id, string title, int year) => _series.SetYear(id, title, year);

        public bool TryResolveEpisode(long seriesId, int? season, int episode, out long id) =>
            _episodes.TryGetValue((seriesId, SeasonKey(season), episode), out id);

        /// <summary>True when the episode row already carries a title, so a later file need not supply one.</summary>
        public bool HasTitle(long episodeId) => _titled.Contains(episodeId);

        public void MarkTitled(long episodeId) => _titled.Add(episodeId);

        /// <summary>
        /// Folds a missing season onto -1, as the unique index on <c>episodes</c> does, so the two
        /// agree about which unnumbered episodes are the same episode.
        /// </summary>
        private static int SeasonKey(int? season) => season ?? -1;

        /// <summary>Reads both tables once. Cheap for a personal library; see the type remarks.</summary>
        /// <remarks>
        /// A programme is registered under the name the scan gave it, which is <c>scan_title</c>
        /// once somebody has renamed it with Wrong series? and <c>title</c> until then: the files
        /// still say what they always said, and resolving them against TMDB's spelling would fork
        /// the programme on the next scan. TMDB's name is filed afterwards, as an alias, so a
        /// folder later renamed to match it still lands on the same row — and so a renamed
        /// programme can never take another programme's scan name away from it, whatever order
        /// the two were catalogued in.
        /// </remarks>
        public static async Task<EpisodeCatalogue> LoadAsync(SqliteConnection conn)
        {
            var catalogue = new EpisodeCatalogue();

            var series = (await conn.QueryAsync<SeriesRow>(
                "SELECT id AS Id, title AS Title, year AS Year, scan_title AS ScanTitle FROM series ORDER BY id")).ToList();
            foreach (var row in series)
                catalogue.AddSeries(row.Id, string.IsNullOrWhiteSpace(row.ScanTitle) ? row.Title : row.ScanTitle, row.Year);
            foreach (var row in series.Where(row => !string.IsNullOrWhiteSpace(row.ScanTitle)))
                catalogue.AddSeriesAlias(row.Id, row.Title, row.Year);

            var episodes = await conn.QueryAsync<EpisodeRowData>(@"
SELECT id AS Id, series_id AS SeriesId, season_number AS SeasonNumber, episode_number AS EpisodeNumber,
       (title IS NOT NULL AND title <> '') AS HasTitle
FROM episodes ORDER BY id");
            foreach (var row in episodes)
                catalogue.AddEpisode(row.Id, row.SeriesId, row.SeasonNumber, row.EpisodeNumber, row.HasTitle);

            return catalogue;
        }

        private sealed class SeriesRow
        {
            public long Id { get; set; }
            public string? Title { get; set; }
            public int? Year { get; set; }
            public string? ScanTitle { get; set; }
        }

        private sealed class EpisodeRowData
        {
            public long Id { get; set; }
            public long SeriesId { get; set; }
            public int? SeasonNumber { get; set; }
            public int EpisodeNumber { get; set; }
            public bool HasTitle { get; set; }
        }
    }
}
