using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// What the catalogue knows about a programme on this disk beyond what the scan named it.
    /// </summary>
    /// <param name="Described">
    /// True once TMDB's description of the programme has been written: its overview, people and
    /// IMDb id. False for a programme never identified, and for one whose identification was
    /// corrected and has still to be described again.
    /// </param>
    public sealed record LocalSeriesDetails(
        long Id,
        string Title,
        int? Year,
        int? TmdbId,
        string? Genres,
        string? PosterPath,
        string? Overview,
        string? BackdropPath,
        double? Rating,
        string? ImdbId,
        IReadOnlyList<string> Cast,
        IReadOnlyList<string> Crew,
        bool Described);

    /// <summary>What identifying one programme came to.</summary>
    /// <param name="Matched">False when TMDB held no result that could be confirmed as this programme.</param>
    /// <param name="Found">The artwork and genres to put on the card. Empty when nothing matched.</param>
    public sealed record SeriesLookupResult(bool Matched, Enrichment Found)
    {
        public static SeriesLookupResult Unmatched { get; } = new(false, Enrichment.None);
    }

    /// <summary>
    /// Identifies a programme on this disk against TMDB's television catalogue, writes down what
    /// TMDB says it is, and fills in its episodes a season at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Once per programme, never per episode. A series is one search and one description — the
    /// description carries the genres, the cast and the IMDb id, so nothing else is asked — and
    /// its episodes' names and plots are one request per season, made only when somebody opens
    /// the programme. A library of forty programmes and two thousand episodes costs eighty
    /// requests in the background rather than two thousand.
    /// </para>
    /// <para>
    /// The match is judged by <see cref="TmdbService.FindSeriesAsync"/>, which applies the film
    /// rules unchanged. A refusal writes nothing: the programme keeps the name the scan gave it
    /// and sits uncategorised until somebody chooses its match by hand, which is a smaller wrong
    /// than another programme's poster on it.
    /// </para>
    /// <para>
    /// The scan's <c>title</c> is never rewritten. It is the identity <see cref="EpisodeCatalogue"/>
    /// resolves the next scan's episodes against, and renaming it to TMDB's spelling would fork
    /// the programme the next time a file named the old way was found.
    /// </para>
    /// <para>
    /// Out of the window and out of <see cref="PosterAutoLoader"/>, which only schedules it, so
    /// the rules can be tested against a temporary catalogue and a fake network.
    /// </para>
    /// </remarks>
    public static class LocalSeriesLookup
    {
        /// <summary>
        /// The name a programme's poster is cached under. Apart from a film's <c>{id}.jpg</c>,
        /// because <c>series</c> numbers its rows from one exactly as <c>movies</c> does, and
        /// series 3 must not find film 3's poster already on disk and take it.
        /// </summary>
        public static string PosterFileName(long seriesId) =>
            $"series-{seriesId.ToString(CultureInfo.InvariantCulture)}.jpg";

        /// <summary>The size a programme's backdrop is shown at, which is the size a film's still is.</summary>
        public const string BackdropSize = FilmStills.FullSize;

        /// <summary>A stored backdrop path as something an image loader can fetch. Null for none.</summary>
        public static string? BackdropUrl(string? backdropPath) =>
            string.IsNullOrWhiteSpace(backdropPath)
                ? null
                : TmdbService.BuildSizedImageUrl(BackdropSize, backdropPath);

        /// <summary>What the catalogue holds for one programme, or null when there is no such row.</summary>
        public static LocalSeriesDetails? Read(SqliteConnection conn, long seriesId)
        {
            var row = conn.QuerySingleOrDefault<SeriesRow>(@"
SELECT id AS Id, title AS Title, year AS Year, tmdb_id AS TmdbId, genres AS Genres,
       poster_path AS PosterPath, overview AS Overview, backdrop_path AS BackdropPath,
       rating AS Rating, imdb_id AS ImdbId, cast_list AS CastList, crew_list AS CrewList,
       tmdb_fetched_at AS FetchedAt
FROM series WHERE id = @id", new { id = seriesId });

            if (row is null) return null;

            int? tmdbId = row.TmdbId is long id && id > 0 && id <= int.MaxValue ? (int)id : null;

            return new LocalSeriesDetails(
                row.Id,
                row.Title ?? "",
                row.Year,
                tmdbId,
                row.Genres,
                row.PosterPath,
                row.Overview,
                row.BackdropPath,
                row.Rating,
                row.ImdbId,
                JellyfinCache.SplitCredits(row.CastList),
                JellyfinCache.SplitCredits(row.CrewList),
                tmdbId is not null && !string.IsNullOrWhiteSpace(row.FetchedAt));
        }

        /// <summary>
        /// True when there is nothing left for the background pass to ask about: the programme is
        /// identified, described, and its artwork is where the catalogue says it is.
        /// </summary>
        /// <remarks>
        /// Artwork as well as the description, so a poster cache somebody emptied is filled in
        /// again — by the identification already stored, with one request rather than a search.
        /// A programme TMDB holds no poster for is therefore asked about once per launch, which
        /// is one request, and the price of noticing when TMDB gains one.
        /// </remarks>
        public static bool IsComplete(LocalSeriesDetails? known) =>
            known is { Described: true } && PosterAutoLoader.HasArtwork(known.PosterPath);

        /// <summary>
        /// Identifies one programme, describes it and writes the description down.
        /// </summary>
        /// <param name="downloadPosters">
        /// Whether the poster is cached to disk, as for films. When it is not, or the download
        /// fails, TMDB's URL is stored instead and the card loads it from there.
        /// </param>
        /// <returns>
        /// <see cref="SeriesLookupResult.Unmatched"/> when TMDB has nothing that can be confirmed
        /// as this programme. A request that failed throws: an outage is not an answer, and the
        /// caller must not record it as one.
        /// </returns>
        public static async Task<SeriesLookupResult> IdentifyAsync(
            TmdbService tmdb,
            SqliteConnection conn,
            long seriesId,
            bool downloadPosters,
            CancellationToken ct)
        {
            var known = Read(conn, seriesId);
            if (known is null || !tmdb.HasApiKey) return SeriesLookupResult.Unmatched;

            // An identification already stored is kept rather than re-derived from the title: it
            // may be one a person chose, and searching again could choose a different programme
            // from the one its poster shows.
            var tmdbId = known.TmdbId ?? (await tmdb.FindSeriesAsync(known.Title, known.Year, ct).ConfigureAwait(false))?.Id;
            if (tmdbId is not int id || id <= 0) return SeriesLookupResult.Unmatched;

            var details = await tmdb.GetSeriesDetailsAsync(id, ct).ConfigureAwait(false);
            if (details is null)
                throw new HttpRequestException("TMDB did not return this programme. Press Refresh to retry.");

            var hadArtwork = PosterAutoLoader.HasArtwork(known.PosterPath);
            string? poster = hadArtwork ? known.PosterPath : null;

            if (!hadArtwork && !string.IsNullOrWhiteSpace(details.PosterPath))
            {
                var url = tmdb.BuildImageUrlPublic(details.PosterPath!);
                poster = downloadPosters
                    ? await tmdb.DownloadForPublic(url, PosterFileName(seriesId), ct).ConfigureAwait(false) ?? url
                    : url;
            }

            var genres = TelevisionGenres.Join(details.Genres?.Select(genre => genre.Name));

            var saved = await SaveDescriptionAsync(
                    conn, seriesId, Describe(id, details, hadArtwork ? null : poster, genres), ct,
                    keepOtherIdentification: true)
                .ConfigureAwait(false);

            // Somebody chose a different programme by hand while this was asking about the one it
            // read. Nothing was written, and nothing found here belongs on the card either.
            if (!saved) return SeriesLookupResult.Unmatched;

            return new SeriesLookupResult(true, new Enrichment(
                poster,
                string.IsNullOrWhiteSpace(known.Genres) ? genres : known.Genres)
            {
                ArtworkRepaired = !hadArtwork && poster is not null
                    && string.Equals(known.PosterPath, poster, StringComparison.Ordinal)
            });
        }

        /// <summary>What TMDB's record says, reduced to the columns the catalogue keeps.</summary>
        public static SeriesDescription Describe(
            int tmdbId, TmdbService.TmdbSeriesDetails details, string? posterPath, string? genres)
        {
            var crew = (details.CreatedBy ?? new List<TmdbService.TmdbCreator>())
                .Select(creator => creator?.Name?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(name => $"Creator: {name}")
                .ToList();

            return new SeriesDescription(
                tmdbId,
                posterPath,
                genres,
                string.IsNullOrWhiteSpace(details.Overview) ? null : details.Overview.Trim(),
                string.IsNullOrWhiteSpace(details.BackdropPath) ? null : details.BackdropPath,
                FilmFactsStore.NormaliseRating(details.VoteAverage),
                string.IsNullOrWhiteSpace(details.ExternalIds?.ImdbId) ? null : details.ExternalIds!.ImdbId!.Trim(),
                CreditLine.Cast(details.Credits),
                crew);
        }

        /// <summary>
        /// Writes a programme's identification and description.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Everything that describes the programme is replaced, because it all describes whichever
        /// programme <paramref name="description"/> names. Two things are only filled in: the
        /// genres, which a person may have set and which this must not overrule on every launch,
        /// and the poster, which is written only when one is given — null leaves the column alone.
        /// </para>
        /// <para>
        /// When the identification changes, every episode's TMDB description is cleared in the
        /// same transaction. Those names and plots belong to the programme it used to be, and a
        /// season marked as already fetched would otherwise never be asked about again.
        /// </para>
        /// </remarks>
        /// <param name="keepOtherIdentification">
        /// Writes nothing when the row has meanwhile been identified as a different programme.
        /// The background pass sets it: it read the row before asking TMDB, and a person may have
        /// chosen the programme with Wrong series? while it was waiting for the answer. Their
        /// choice is the one that stands.
        /// </param>
        /// <returns>False when <paramref name="keepOtherIdentification"/> refused the write, or there is no such row.</returns>
        public static Task<bool> SaveDescriptionAsync(
            SqliteConnection conn,
            long seriesId,
            SeriesDescription description,
            CancellationToken ct = default,
            bool keepOtherIdentification = false) =>
            DatabaseWriteLane.RunAsync(conn, token =>
            {
                token.ThrowIfCancellationRequested();
                using var tx = conn.BeginTransaction();

                if (keepOtherIdentification)
                {
                    var other = conn.ExecuteScalar<long>(
                        "SELECT COUNT(*) FROM series WHERE id = @id AND tmdb_id IS NOT NULL AND tmdb_id <> @tmdb",
                        new { id = seriesId, tmdb = description.TmdbId }, tx);
                    if (other > 0) return Task.FromResult(false);
                }

                conn.Execute(@"
UPDATE episodes
SET tmdb_title = NULL, overview = NULL, runtime_minutes = NULL, air_date = NULL, still_path = NULL,
    tmdb_fetched_at = NULL
WHERE series_id = @id
  AND EXISTS (SELECT 1 FROM series s WHERE s.id = @id AND s.tmdb_id IS NOT NULL AND s.tmdb_id <> @tmdb)",
                    new { id = seriesId, tmdb = description.TmdbId }, tx);

                var poster = description.PosterPath is null ? "" : "poster_path = @poster,";

                var written = conn.Execute($@"
UPDATE series
SET tmdb_id = @tmdb, {poster}
    genres = CASE WHEN genres IS NULL OR trim(genres, ' ' || char(9) || char(10) || char(13)) = ''
                  THEN @genres ELSE genres END,
    overview = @overview, backdrop_path = @backdrop, rating = @rating, imdb_id = @imdb,
    cast_list = @cast, crew_list = @crew, tmdb_fetched_at = @fetched
WHERE id = @id",
                    new
                    {
                        id = seriesId,
                        tmdb = description.TmdbId,
                        poster = description.PosterPath,
                        genres = description.Genres,
                        overview = description.Overview,
                        backdrop = description.BackdropPath,
                        rating = description.Rating,
                        imdb = description.ImdbId,
                        cast = NullIfEmpty(JellyfinCache.JoinCredits(description.Cast)),
                        crew = NullIfEmpty(JellyfinCache.JoinCredits(description.Crew)),
                        fetched = Now()
                    }, tx);

                tx.Commit();
                return Task.FromResult(written > 0);
            }, ct);

        /// <summary>
        /// Asks TMDB for the seasons of an identified programme whose episodes have not been
        /// described yet — or for every season, when <paramref name="force"/> is set — and writes
        /// what it says onto the episodes this disk holds.
        /// </summary>
        /// <returns>How many seasons were asked about. Zero when the programme is not identified.</returns>
        /// <remarks>
        /// Only seasons with a number, because TMDB has nothing to call an unnumbered one; never
        /// season 0, whose numbering TMDB assigns arbitrarily so a number says nothing about which
        /// special a file is; and only seasons with a file, because a season nobody can play is
        /// not worth a request. Every episode of an asked-about season is marked as fetched,
        /// including one TMDB does not list or whose title disagrees — this disk's numbering and
        /// TMDB's do not always agree, and such an episode would otherwise cost a request on every
        /// open, for ever.
        ///
        /// A season that fails stops the pass and throws, leaving the seasons already written
        /// written and the rest unmarked, so they are asked about again next time.
        /// </remarks>
        public static async Task<int> RefreshSeasonsAsync(
            TmdbService tmdb,
            SqliteConnection conn,
            long seriesId,
            bool force,
            CancellationToken ct)
        {
            if (!tmdb.HasApiKey) return 0;

            var known = Read(conn, seriesId);
            if (known?.TmdbId is not int tmdbId) return 0;

            var seasons = conn.Query<int>(@"
SELECT DISTINCT e.season_number FROM episodes e
WHERE e.series_id = @id AND e.season_number IS NOT NULL AND e.season_number <> 0
  AND (@force OR e.tmdb_fetched_at IS NULL)
  AND EXISTS (SELECT 1 FROM files f WHERE f.episode_id = e.id)
ORDER BY e.season_number", new { id = seriesId, force }).ToList();

            var asked = 0;
            foreach (var season in seasons)
            {
                ct.ThrowIfCancellationRequested();

                var episodes = await tmdb.GetSeasonAsync(tmdbId, season, ct).ConfigureAwait(false);
                await SaveSeasonAsync(conn, seriesId, tmdbId, season, episodes, ct).ConfigureAwait(false);
                asked++;
            }

            return asked;
        }

        /// <summary>
        /// Writes one season's descriptions onto the episodes this disk holds of it, matched on
        /// the episode number and then checked against the title the file gave — a number alone
        /// does not say which episode a file is (see <see cref="TitlesAgree"/>), and an episode
        /// whose title disagrees is stored with no description at all rather than another's.
        /// Written only while the programme is still the one TMDB was asked about, so a
        /// correction landing mid-fetch is not overwritten with the old programme's episodes.
        /// </summary>
        public static Task SaveSeasonAsync(
            SqliteConnection conn,
            long seriesId,
            int tmdbId,
            int season,
            IReadOnlyList<TmdbService.TmdbEpisode> episodes,
            CancellationToken ct = default) =>
            DatabaseWriteLane.RunAsync(conn, token =>
            {
                token.ThrowIfCancellationRequested();
                using var tx = conn.BeginTransaction();

                var still = conn.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM series WHERE id = @id AND tmdb_id = @tmdb",
                    new { id = seriesId, tmdb = tmdbId }, tx);
                if (still == 0) return Task.CompletedTask;

                // Keyed on the episode number alone: the response is already one season's.
                var byNumber = new Dictionary<int, TmdbService.TmdbEpisode>();
                foreach (var episode in episodes)
                    byNumber.TryAdd(episode.EpisodeNumber, episode);

                var rows = conn.Query<(long Id, int Number, string? Title)>(
                    "SELECT id, episode_number, title FROM episodes WHERE series_id = @id AND season_number = @season",
                    new { id = seriesId, season }, tx).ToList();

                var fetched = Now();
                foreach (var (rowId, number, fileTitle) in rows)
                {
                    if (!byNumber.TryGetValue(number, out TmdbService.TmdbEpisode? described)
                        || !Describes(season, fileTitle, described.Name))
                        described = null;

                    conn.Execute(@"
UPDATE episodes
SET tmdb_title = @title, overview = @overview, runtime_minutes = @runtime, air_date = @air,
    still_path = @still, tmdb_fetched_at = @fetched
WHERE id = @id",
                        new
                        {
                            id = rowId,
                            title = Blank(described?.Name),
                            overview = Blank(described?.Overview),
                            runtime = described?.Runtime is int minutes && minutes > 0 ? minutes : (int?)null,
                            air = Blank(described?.AirDate),
                            still = Blank(described?.StillPath),
                            fetched
                        }, tx);
                }

                tx.Commit();
                return Task.CompletedTask;
            }, ct);

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>
        /// Whether TMDB's name for an episode number is the episode the file says it is.
        /// </summary>
        /// <remarks>
        /// TMDB is asked by number, and numbers are not an identity: Firefly on DVD opens with
        /// "Serenity", TMDB's aired order with "The Train Job". A file that names itself is
        /// believed over a number, so TMDB's description is only taken when the two names agree
        /// once case, punctuation, a leading article and spacing are set aside — or when the file
        /// names nothing, and the number is all there is to go on.
        /// </remarks>
        public static bool TitlesAgree(string? fileTitle, string? tmdbTitle)
        {
            var file = Fold(fileTitle);
            return file.Length == 0 || file == Fold(tmdbTitle);

            static string Fold(string? title)
            {
                var normalized = MovieIndex.NormalizeTitle(title);
                foreach (var article in new[] { "the ", "a ", "an " })
                    if (normalized.StartsWith(article, StringComparison.Ordinal))
                    {
                        normalized = normalized[article.Length..];
                        break;
                    }

                return normalized.Replace(" ", "", StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Whether TMDB's description may be shown on, or stored against, a local episode. Never
        /// for season 0, whose numbering TMDB assigns as it pleases; otherwise when the titles
        /// agree. All of TMDB's description or none of it: a row never mixes two episodes.
        /// </summary>
        public static bool Describes(int? season, string? fileTitle, string? tmdbTitle) =>
            season is int number && number != SeriesGrouping.SpecialsSeasonNumber
            && TitlesAgree(fileTitle, tmdbTitle);

        private static object? NullIfEmpty(string value) => value.Length == 0 ? null : value;

        private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        private sealed class SeriesRow
        {
            public long Id { get; set; }
            public string? Title { get; set; }
            public int? Year { get; set; }
            public long? TmdbId { get; set; }
            public string? Genres { get; set; }
            public string? PosterPath { get; set; }
            public string? Overview { get; set; }
            public string? BackdropPath { get; set; }
            public double? Rating { get; set; }
            public string? ImdbId { get; set; }
            public string? CastList { get; set; }
            public string? CrewList { get; set; }
            public string? FetchedAt { get; set; }
        }
    }

    /// <summary>
    /// A programme's identification and description, as <see cref="LocalSeriesLookup.SaveDescriptionAsync"/>
    /// writes it. Built from TMDB's record by <see cref="LocalSeriesLookup.Describe"/>, and the
    /// shape a correction chosen by hand would write too.
    /// </summary>
    /// <param name="PosterPath">Null leaves the stored poster alone.</param>
    /// <param name="Genres">Written only onto a programme that has none.</param>
    public sealed record SeriesDescription(
        int TmdbId,
        string? PosterPath,
        string? Genres,
        string? Overview,
        string? BackdropPath,
        double? Rating,
        string? ImdbId,
        IReadOnlyList<string> Cast,
        IReadOnlyList<string> Crew);
}
