using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services;

public sealed class LocalSeriesTarget
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string Label => Year is int year ? $"{Title} ({year})" : Title;
}

public sealed record EpisodeAssignment(long FileId, int? Season, int Episode, long? MovieId, long? EpisodeId);

public sealed class SeriesGroupingFile
{
    public long Id { get; set; }
    public long? MovieId { get; set; }
    public long? EpisodeId { get; set; }
    public string FilePath { get; set; } = "";
    public string? MovieTitle { get; set; }
    public string? SeriesTitle { get; set; }
    public string? EpisodeTitle { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public bool IsSelected { get; set; }
    public string SeasonText { get; set; } = "";
    public string EpisodeText { get; set; } = "";
    public string Label => SeriesTitle ?? MovieTitle ?? Path.GetFileNameWithoutExtension(FilePath);

    public EpisodeAssignment Assignment()
    {
        int? season = null;
        if (!string.IsNullOrWhiteSpace(SeasonText))
        {
            if (!int.TryParse(SeasonText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                throw new ArgumentException($"Enter a whole season number for {Path.GetFileName(FilePath)}, or leave it empty.");
            season = number;
        }
        if (!int.TryParse((EpisodeText ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var episode))
            throw new ArgumentException($"Enter a whole episode number for {Path.GetFileName(FilePath)}.");
        return new EpisodeAssignment(Id, season, episode, MovieId, EpisodeId);
    }
}

public static class LocalSeriesGrouping
{
    public static IReadOnlyList<LocalSeriesTarget> Targets(SqliteConnection conn) =>
        conn.Query<LocalSeriesTarget>("SELECT id AS Id, title AS Title, year AS Year FROM series ORDER BY title, year").ToList();

    public static IReadOnlyList<SeriesGroupingFile> Files(SqliteConnection conn)
    {
        var files = conn.Query<SeriesGroupingFile>(@"
SELECT f.id AS Id, f.movie_id AS MovieId, f.episode_id AS EpisodeId, f.file_path AS FilePath,
       m.title AS MovieTitle, s.title AS SeriesTitle, e.title AS EpisodeTitle,
       e.season_number AS SeasonNumber, e.episode_number AS EpisodeNumber
FROM files f
LEFT JOIN movies m ON m.id = f.movie_id
LEFT JOIN episodes e ON e.id = f.episode_id
LEFT JOIN series s ON s.id = e.series_id
ORDER BY COALESCE(s.title, m.title, f.file_path), e.season_number, e.episode_number, f.file_path").ToList();
        foreach (var file in files)
        {
            var season = file.SeasonNumber;
            var episode = file.EpisodeNumber;
            if (episode is null && EpisodeParser.TryParse(file.FilePath, out var parsed))
            {
                season = parsed.SeasonNumber;
                episode = parsed.EpisodeNumber;
            }
            file.SeasonText = season?.ToString(CultureInfo.InvariantCulture) ?? "";
            file.EpisodeText = episode?.ToString(CultureInfo.InvariantCulture) ?? "";
        }
        return files;
    }

    // The caller holds DatabaseWriteLane. The transaction also rejects stale file selections.
    public static long Assign(SqliteConnection conn, long? seriesId, string? newTitle,
        IReadOnlyList<EpisodeAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(assignments);
        if (assignments.Count == 0) throw new ArgumentException("Select at least one file.");
        if (assignments.Any(a => a.FileId <= 0 || a.Season < 0 || a.Episode < 0)
            || assignments.Select(a => a.FileId).Distinct().Count() != assignments.Count)
            throw new ArgumentException("Each file must be selected once, with valid season and episode numbers.");

        using var tx = conn.BeginTransaction();
        long target;
        if (seriesId is long existing && existing > 0)
        {
            if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM series WHERE id = @existing", new { existing }, tx) == 0)
                throw new InvalidOperationException("The selected series no longer exists. Reopen Organise series.");
            target = existing;
        }
        else
        {
            var title = (newTitle ?? "").Trim();
            var normalized = MovieIndex.NormalizeTitle(title);
            if (normalized.Length == 0) throw new ArgumentException("Enter a title for the new series.");
            if (conn.Query<string>("SELECT title FROM series", transaction: tx)
                .Any(name => MovieIndex.NormalizeTitle(name) == normalized))
                throw new ArgumentException("A series with that title already exists. Select it from the list.");
            target = conn.ExecuteScalar<long>(
                "INSERT INTO series (title, created_at) VALUES (@title, @now) RETURNING id",
                new { title, now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }, tx);
        }

        foreach (var assignment in assignments)
        {
            var file = conn.QuerySingleOrDefault<SeriesGroupingFile>(@"
SELECT f.id AS Id, f.movie_id AS MovieId, f.episode_id AS EpisodeId,
       f.file_path AS FilePath, e.title AS EpisodeTitle
FROM files f LEFT JOIN episodes e ON e.id = f.episode_id WHERE f.id = @id",
                new { id = assignment.FileId }, tx);
            if (file is null || file.MovieId != assignment.MovieId || file.EpisodeId != assignment.EpisodeId)
                throw new InvalidOperationException("A selected file changed in the catalogue. Reopen Organise series and select it again.");

            var episode = conn.QuerySingleOrDefault<long?>(@"
SELECT id FROM episodes WHERE series_id = @target AND season_number IS @season AND episode_number = @number",
                new { target, season = assignment.Season, number = assignment.Episode }, tx);
            if (episode is null)
            {
                episode = conn.ExecuteScalar<long>(@"
INSERT INTO episodes (series_id, season_number, episode_number, title)
VALUES (@target, @season, @number, @title) RETURNING id",
                    new
                    {
                        target, season = assignment.Season, number = assignment.Episode,
                        title = file.EpisodeTitle ?? (EpisodeParser.TryParse(file.FilePath, out var parsed)
                            ? parsed.EpisodeTitle : Path.GetFileNameWithoutExtension(file.FilePath))
                    }, tx);
            }

            conn.Execute(@"
UPDATE files SET movie_id = NULL, episode_id = @episode, linked_by_hand = 1 WHERE id = @id",
                new { episode, id = file.Id }, tx);
        }

        foreach (var oldEpisode in assignments.Select(a => a.EpisodeId).OfType<long>().Distinct())
            conn.Execute("DELETE FROM episodes WHERE id = @id AND NOT EXISTS (SELECT 1 FROM files WHERE episode_id = @id)",
                new { id = oldEpisode }, tx);
        OrphanFilms.Sweep(conn, tx, assignments.Select(a => a.MovieId).OfType<long>().ToHashSet());
        tx.Commit();
        return target;
    }
}
