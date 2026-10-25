using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    /// <summary>The TMDB programme a person picked in the Wrong series? window.</summary>
    /// <param name="Name">What TMDB calls it, or null when it gave no name. Becomes the programme's title.</param>
    /// <param name="PosterPath">TMDB's own relative poster path from the search result, or null.</param>
    public sealed record SeriesChoice(int TmdbId, string? Name, string? PosterPath);

    /// <summary>What correcting a programme came to.</summary>
    /// <param name="Details">The catalogue row as it now reads.</param>
    /// <param name="RenamedTo">The programme's new title, or null when it kept the one it had.</param>
    /// <param name="Described">
    /// False when the identification was saved but TMDB's description of the programme could not
    /// be fetched. The row is then left for the background pass to describe by id, without
    /// searching, and the screen says so rather than looking as though nothing happened.
    /// </param>
    public sealed record SeriesCorrection(LocalSeriesDetails Details, string? RenamedTo, bool Described);

    /// <summary>
    /// Records which TMDB programme a series on this disk is, when a person has said so with
    /// <b>Wrong series?</b>, and rewrites everything that described the programme it was taken for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart of what <c>MovieDetailsView</c> does for a film, but out of the view so it
    /// can be tested against a temporary catalogue and a fake network. It is also the only way to
    /// identify a programme the background pass refused: that pass never guesses between two
    /// programmes of one name, so "The Office" without a year waits here for somebody to choose.
    /// </para>
    /// <para>
    /// It overwrites where <see cref="LocalSeriesLookup.IdentifyAsync"/> only fills in. That pass
    /// runs on every launch and must not overrule a better answer, so it writes genres only onto a
    /// programme that has none and a poster only onto one that has no artwork. Here the genres and
    /// the poster already stored are precisely what is wrong, so both are replaced — the poster
    /// under a file name that carries the TMDB id, because a download returns an existing file
    /// untouched and the old programme's <c>series-{id}.jpg</c> would otherwise come straight back.
    /// </para>
    /// <para>
    /// The order is the film correction's. The identification, the poster and the name are saved
    /// first, in one transaction, and only then is TMDB asked for the description: a request that
    /// fails or a screen left mid-way keeps the person's answer, and an identified programme with
    /// no description yet is one the background pass describes by id on its next run.
    /// </para>
    /// <para>
    /// Every episode's TMDB description is dropped, even when the same programme is chosen again.
    /// Those names were fetched for whatever the programme was taken for, and choosing it by hand
    /// is the moment to ask for them afresh; the seasons are asked for the next time the programme
    /// is opened, which costs one request each.
    /// </para>
    /// <para>
    /// The programme takes TMDB's name, and the scan's is kept in <c>scan_title</c> the first time
    /// it is replaced. <see cref="EpisodeCatalogue"/> resolves the next scan's episodes by that,
    /// so renaming cannot fork the programme — and nothing a scan writes touches
    /// <c>tmdb_id</c>, the poster, the genres or the title of a programme it already has, so a
    /// rescan cannot undo any of this.
    /// </para>
    /// </remarks>
    public static class SeriesMatchCorrection
    {
        /// <summary>
        /// Where a chosen programme's poster is cached. Apart from <see cref="LocalSeriesLookup.PosterFileName"/>
        /// and from every other programme the same row could be corrected to; see the type remarks.
        /// </summary>
        public static string PosterFileName(long seriesId, int tmdbId) =>
            $"series-{seriesId.ToString(CultureInfo.InvariantCulture)}-{tmdbId.ToString(CultureInfo.InvariantCulture)}.jpg";

        /// <summary>
        /// Identifies <paramref name="seriesId"/> as <paramref name="choice"/>, renames it to
        /// TMDB's name, and describes it.
        /// </summary>
        /// <param name="downloadPosters">
        /// Whether the poster is cached to disk. When it is not, or the download fails, TMDB's URL
        /// is stored instead, so the card still shows the right poster over the network.
        /// </param>
        /// <param name="onRecorded">
        /// Called once the identification, poster and name are committed — before TMDB is asked
        /// for the description, which may fail or be cancelled. From that moment the catalogue
        /// holds the correction whatever happens next, so this, and not the method returning, is
        /// what tells a caller the library has to be read again.
        /// </param>
        /// <exception cref="InvalidOperationException">There is no such programme, or no TMDB key.</exception>
        public static async Task<SeriesCorrection> ApplyAsync(
            TmdbService tmdb,
            SqliteConnection conn,
            long seriesId,
            SeriesChoice choice,
            bool downloadPosters,
            CancellationToken ct,
            Action? onRecorded = null)
        {
            if (!tmdb.HasApiKey) throw new InvalidOperationException("Choosing a programme needs a TMDB key.");
            if (choice.TmdbId <= 0) throw new ArgumentOutOfRangeException(nameof(choice), "Not a TMDB programme.");

            var known = LocalSeriesLookup.Read(conn, seriesId)
                ?? throw new InvalidOperationException("This programme is no longer in the catalogue.");

            var poster = await ResolvePosterAsync(tmdb, seriesId, choice.TmdbId, choice.PosterPath, downloadPosters, ct)
                .ConfigureAwait(false);
            var renamed = MovieMatch.RenameTo(known.Title, choice.Name);

            await RecordAsync(conn, seriesId, choice.TmdbId, poster, renamed, ct).ConfigureAwait(false);
            onRecorded?.Invoke();

            var described = await DescribeAsync(tmdb, conn, seriesId, choice.TmdbId, poster is null, downloadPosters, ct)
                .ConfigureAwait(false);

            return new SeriesCorrection(LocalSeriesLookup.Read(conn, seriesId)!, renamed, described);
        }

        /// <summary>
        /// Writes the identification, and clears everything that described the programme it was
        /// taken for, in one transaction.
        /// </summary>
        /// <param name="posterPath">Replaces the stored poster. Null clears it: the old one is the wrong programme's.</param>
        /// <param name="title">The new name, or null to keep the current one.</param>
        public static Task RecordAsync(
            SqliteConnection conn,
            long seriesId,
            int tmdbId,
            string? posterPath,
            string? title,
            CancellationToken ct = default) =>
            DatabaseWriteLane.RunAsync(conn, token =>
            {
                token.ThrowIfCancellationRequested();
                using var tx = conn.BeginTransaction();

                conn.Execute(@"
UPDATE episodes
SET tmdb_title = NULL, overview = NULL, runtime_minutes = NULL, air_date = NULL, still_path = NULL,
    tmdb_fetched_at = NULL
WHERE series_id = @id", new { id = seriesId }, tx);

                // SQLite evaluates every SET expression against the old row, so scan_title takes
                // the name being replaced — and keeps the scan's on a second rename, because
                // COALESCE leaves one already there alone.
                var rename = title is null ? "" : "scan_title = COALESCE(scan_title, title), title = @title,";

                conn.Execute($@"
UPDATE series
SET tmdb_id = @tmdb, poster_path = @poster, {rename}
    genres = NULL, overview = NULL, backdrop_path = NULL, rating = NULL, imdb_id = NULL,
    cast_list = NULL, crew_list = NULL, tmdb_fetched_at = NULL
WHERE id = @id",
                    new { id = seriesId, tmdb = tmdbId, poster = posterPath, title }, tx);

                tx.Commit();
                return Task.CompletedTask;
            }, ct);

        /// <summary>
        /// Asks TMDB for the chosen programme's description and writes it. False, with the
        /// failure logged, when TMDB did not answer: the identification is already saved, and a
        /// correction is not undone by the request that follows it failing.
        /// </summary>
        private static async Task<bool> DescribeAsync(
            TmdbService tmdb,
            SqliteConnection conn,
            long seriesId,
            int tmdbId,
            bool needsPoster,
            bool downloadPosters,
            CancellationToken ct)
        {
            TmdbService.TmdbSeriesDetails? details;
            try
            {
                details = await tmdb.GetSeriesDetailsAsync(tmdbId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Write("posters.log", $"seriesId={seriesId} chosen tmdb {tmdbId}: could not describe: {ex.Message}");
                return false;
            }

            if (details is null)
            {
                AppLog.Write("posters.log", $"seriesId={seriesId} chosen tmdb {tmdbId}: TMDB has no such programme");
                return false;
            }

            // The search result had no poster, but the record may: TMDB's search and its details
            // are not always in step.
            var poster = needsPoster
                ? await ResolvePosterAsync(tmdb, seriesId, tmdbId, details.PosterPath, downloadPosters, ct).ConfigureAwait(false)
                : null;

            var genres = TelevisionGenres.Join(details.Genres?.Select(genre => genre.Name));

            // Genres are filled in only when empty, and RecordAsync has just emptied them, so this
            // writes the chosen programme's. Guarded like the background pass: a second correction
            // landing while this one waited must not be overwritten by it.
            return await LocalSeriesLookup.SaveDescriptionAsync(
                    conn, seriesId, LocalSeriesLookup.Describe(tmdbId, details, poster, genres), ct,
                    keepOtherIdentification: true)
                .ConfigureAwait(false);
        }

        private static async Task<string?> ResolvePosterAsync(
            TmdbService tmdb, long seriesId, int tmdbId, string? posterPath, bool download, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(posterPath)) return null;

            var url = tmdb.BuildImageUrlPublic(posterPath);
            if (!download) return url;

            try
            {
                return await tmdb.DownloadForPublic(url, PosterFileName(seriesId, tmdbId), ct).ConfigureAwait(false) ?? url;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The URL shows the right poster over the network, which beats keeping the wrong
                // one from disk.
                AppLog.Write("posters.log", $"seriesId={seriesId} chosen tmdb {tmdbId}: poster download failed: {ex.Message}");
                return url;
            }
        }
    }
}
