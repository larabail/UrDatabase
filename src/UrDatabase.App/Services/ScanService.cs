using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    public class ScanService
    {
        private readonly IVolumeSnapshotSource _volumes;
        public ScanService(IVolumeSnapshotSource? volumes = null) =>
            _volumes = volumes ?? VolumeSnapshotSource.Shared;
        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".mpg", ".mpeg", ".ts", ".webm"
        };

        public static IReadOnlyCollection<string> SupportedExtensions => VideoExtensions;

        /// <summary>
        /// How many files share a write transaction. Small enough that poster enrichment and the
        /// window's own reads are never locked out for long, large enough that a big library is not
        /// one fsync per file.
        /// </summary>
        private const int FilesPerTransaction = 200;

        /// <summary>What became of one file the scan walked past. The categories do not overlap.</summary>
        private enum FileOutcome
        {
            /// <summary>Already accounted for by this scan, because two watch folders overlap.</summary>
            AlreadySeen,
            Inserted,
            Moved,
            Updated,
            Unchanged,
        }

        /// <summary>
        /// True when the path looks like a movie file. The comparison is deliberately
        /// case-insensitive: <c>.MKV</c> and <c>.mkv</c> are the same file type even on a
        /// case-sensitive filesystem.
        /// </summary>
        public static bool IsVideoFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            var extension = Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension) && VideoExtensions.Contains(extension);
        }

        /// <summary>
        /// Opens a connection, scans, and closes it — in that order.
        ///
        /// The window's click handler used to open a connection, start the scan without awaiting
        /// it and return, disposing the connection while the scan was still writing through it.
        /// Owning the connection here means there is no shape in which a caller can dispose it
        /// early, and the returned Task completes only once every row is committed.
        /// </summary>
        public static async Task<ScanResult> ScanLibraryAsync(string dbPath, IEnumerable<string> folders, IProgress<string>? progress = null, CancellationToken ct = default,
            IVolumeSnapshotSource? volumes = null)
        {
            using var conn = Database.Open(dbPath);
            return await new ScanService(volumes).ScanAsync(conn, folders, progress, ct);
        }

        /// <summary>
        /// Walks the watch folders and brings the catalogue up to date: every video file gets a row
        /// in <c>files</c>, a canonical row in <c>movies</c>, and a link between them.
        ///
        /// The movie row is the whole point. The window reads <c>movies</c> and nothing else, so a
        /// scan that only filled <c>files</c> left the library looking empty however many films
        /// were on disk.
        ///
        /// A scan is also the only thing that can notice a film is gone, and it used to be
        /// incapable of it. Every write was an upsert of a path the walk had just found, so the
        /// catalogue could only grow: a deleted file, an unplugged drive and a folder somebody
        /// renamed all left their rows behind, untouched and indistinguishable, and a film dragged
        /// somewhere else became a second row beside the first. Fixing that needs two things this
        /// method now does. It stamps which scan last saw each row, so "not seen" is something
        /// recorded rather than inferred; and it opens a <c>scans</c> row for that fact to be
        /// about — one that knows whether it ran to the end, because only a scan that looked
        /// everywhere may conclude anything from not having found something.
        ///
        /// No file is deleted here, and that is a decision rather than an omission. From inside
        /// this process a film you deleted and a film on a drive you unplugged are the same
        /// absence, so the scan marks it and leaves the reading to a person. What is deleted is a
        /// film row with no file row at all — see <see cref="OrphanFilms"/> — which neither of
        /// those absences can produce.
        /// </summary>
        public async Task<ScanResult> ScanAsync(SqliteConnection conn, IEnumerable<string> folders, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (folders is null) throw new ArgumentNullException(nameof(folders));
            var requested = folders.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            if (ct.IsCancellationRequested)
            {
                var cancelledId = await ScanSessions.BeginAsync(conn, Array.Empty<string>(), requested, DateTimeOffset.UtcNow);
                var cancelled = ScanResult.Nothing with { ScanId = cancelledId, Status = ScanStatus.Cancelled, SkippedRoots = requested };
                await ScanSessions.FinishAsync(conn, cancelled, DateTimeOffset.UtcNow);
                return cancelled;
            }

            // Split before anything is written, because the difference decides what this scan is
            // allowed to conclude. A folder that is not there was not searched, so nothing under
            // it may be called missing — which is the whole reason unplugging an external drive
            // does not cost somebody their catalogue.
            var storedRoots = FileLocations.Roots(conn);
            var roots = requested.SelectMany(token =>
                new[] { (Token: token, Root: FileLocations.Root(conn, "watch", token)) }
                    .Concat(storedRoots.Where(r => r.Kind == "watch" && r.Token == token && r.ScopePath.Length > 0)
                        .Select(r => (Token: token, Root: (LibraryRoot?)r)))).ToArray();
            var probePaths = requested.Select(PlatformPaths.Expand).Concat(roots.Where(r => r.Root is not null).Select(r => r.Root!.FilePath)).ToArray();
            var snapshot = await _volumes.CaptureAsync(probePaths, ct);
            var walked = new List<string>();
            var verifiedRoots = new Dictionary<(string Token, string Scope), string>();
            var skipped = new List<string>();
            var issues = new List<string>();
            foreach (var (token, root) in roots)
            {
                var location = FileLocations.Resolve(root?.Binding ?? LocationBinding.Legacy(PlatformPaths.Expand(token)),
                    snapshot, Directory.Exists);
                if (location.Available)
                {
                    walked.Add(location.Path!);
                    verifiedRoots[(token, root!.ScopePath)] = location.Path!;
                }
                else
                {
                    skipped.Add(token);
                    issues.Add($"{token}: {location.Message}");
                    progress?.Report(issues[^1]);
                }
            }

            async Task VerifyRootsAsync()
            {
                var current = await _volumes.CaptureAsync(probePaths, ct);
                foreach (var nested in snapshot.Mounts.Where(m => m.Id is not null
                             && walked.Any(root => VolumePaths.TryRelative(root, m.Root, out _, m.PathStyle))))
                {
                    var candidates = current.WithIdentity(nested.Id!);
                    if (!current.Complete || candidates.Count != 1 || candidates[0].Root != nested.Root)
                        throw new IOException("A disk mounted inside a scan folder changed. Scan again; no further files will be marked missing.");
                }
                foreach (var (token, original) in roots.Where(r => r.Root is not null))
                {
                    if (!verifiedRoots.TryGetValue((token, original!.ScopePath), out var walkedPath)) continue;
                    var stored = FileLocations.Root(conn, "watch", token, original.ScopePath);
                    var resolved = FileLocations.Resolve(original!.Binding, current, Directory.Exists);
                    if (stored?.Revision != original.Revision || stored.Binding != original.Binding
                        || !resolved.Available || resolved.Path != walkedPath)
                        throw new IOException("A scan disk or configured folder changed. Nothing further will be marked missing; scan again.");
                }
            }

            var scanId = await ScanSessions.BeginAsync(conn, walked, skipped, DateTimeOffset.UtcNow);

            MovieIndex movies;
            EpisodeCatalogue episodes;
            ScanFileIndex files;
            try
            {
                movies = await LoadMovieIndexAsync(conn);
                episodes = await EpisodeCatalogue.LoadAsync(conn);
                files = await ScanFileIndex.LoadAsync(conn, snapshot, walked);
            }
            catch
            {
                await CloseQuietlyAsync(conn, ScanResult.Nothing with
                { ScanId = scanId, Status = ScanStatus.Failed, Roots = walked, SkippedRoots = skipped });
                throw;
            }
            var knownMovies = movies.Count;
            var knownSeries = episodes.SeriesCount;

            var counts = new ScanCounts();
            var unreadable = new List<string>();
            var status = ScanStatus.Completed;
            var catalogue = new Catalogue(movies, episodes);

            try
            {
                foreach (var folder in walked)
                {
                    if (status == ScanStatus.Cancelled) break;
                    progress?.Report($"Scanning: {folder}");
                    var source = roots.First(r => r.Root is not null &&
                        verifiedRoots.TryGetValue((r.Token, r.Root.ScopePath), out var resolved) && resolved == folder);
                    var parsingRoot = source.Root!.ScopePath.Length > 0
                        ? source.Root.ScopePath : PlatformPaths.Expand(source.Token);

                    if (!await ScanFolderAsync(conn, folder, parsingRoot, scanId, catalogue, files, counts, unreadable, progress, ct, VerifyRootsAsync))
                        status = ScanStatus.Cancelled;
                }

                // Only ever after a scan that finished. A cancelled one stopped somewhere
                // arbitrary, so everything it had not reached yet looks exactly like everything
                // that is gone, and it has no way to tell those apart.
                if (status == ScanStatus.Completed)
                {
                    await VerifyRootsAsync();
                    counts.Missing = await MarkMissingAsync(conn, files, walked, unreadable, DateTimeOffset.UtcNow, VerifyRootsAsync);

                    // A rename sweeps as it happens, and this is the safety net for the debris a
                    // rename never saw: a row left by an older build, or by one on a branch,
                    // pointed at the same catalogue and scanning it without knowing to look for
                    // the alias. The scan is where it belongs — it is the only thing that reads
                    // the whole catalogue, and the only thing that ever created such a row.
                    //
                    // After the walk rather than before it, because a row this scan has just
                    // inserted and linked a file to is not debris, and looking first would judge
                    // it before it had its file.
                    //
                    // And not at all when a file failed to record. That failure is exactly the
                    // shape that makes an ordinary row look like debris — the movie row is
                    // committed and the file that was to be attached to it is not — so a scan
                    // that hit one has no business concluding anything about an empty row.
                    if (counts.Failed == 0)
                    {
                        await SweepDiscardedNamesAsync(conn);

                        // Under the same conditions, and for the same reason: a file that failed to
                        // record leaves exactly a film row with no file, and this would read that
                        // as a leftover. A cancelled scan may have stopped half way through
                        // re-filing a programme, and the next completed one finishes the job.
                        await SweepOrphansAsync(conn);
                    }
                }
            }
            catch (Exception)
            {
                await CloseQuietlyAsync(
                    conn,
                    ScanResult.From(scanId, ScanStatus.Failed, counts, movies.Count - knownMovies, walked, skipped,
                        episodes.SeriesCount - knownSeries));
                throw;
            }

            var result = ScanResult.From(scanId, status, counts, catalogue.Movies.Count - knownMovies, walked, skipped,
                catalogue.Episodes.SeriesCount - knownSeries) with { RootIssues = issues };
            await ScanSessions.FinishAsync(conn, result, DateTimeOffset.UtcNow);

            progress?.Report(result.Summary);
            return result;
        }

        /// <summary>
        /// Walks one folder, committing in batches. Returns false when the scan was cancelled.
        ///
        /// Batched rather than a commit per file, which cost a large library thousands of fsyncs,
        /// and rather than one transaction for a whole folder, which would hold the write lock
        /// long enough to starve poster enrichment on a big library.
        /// </summary>
        private static async Task<bool> ScanFolderAsync(
            SqliteConnection conn,
            string folder,
            string parsingRoot,
            long scanId,
            Catalogue catalogue,
            ScanFileIndex files,
            ScanCounts counts,
            ICollection<string> unreadable,
            IProgress<string>? progress,
            CancellationToken ct,
            Func<Task> verifyRoots)
        {
            IDisposable? lease = null;
            SqliteTransaction? tx = null;
            var completed = false;

            try
            {
                // A turn in the write lane, taken per batch rather than per scan. The poster
                // loader and a Jellyfin sync write to the same file, and holding the lane for a
                // whole library would shut them out for the length of a scan — the starvation
                // FilesPerTransaction already exists to prevent.
                //
                // Not cancellable, deliberately. The lease and the transaction are replaced as a
                // pair, and a cancellation thrown between them would leave the cleanup below
                // committing a transaction that no longer exists. Cancellation is picked up by the
                // enumerator instead, and the wait here is one other writer's turn rather than
                // anything unbounded.
                //
                // Both are taken inside the try, and that is the whole reason this shape is worth
                // the nulls. Opening the transaction can fail — on a connection somebody disposed
                // underneath the scan, most obviously — and taking the lane on the line above a
                // throw meant the lane was never given back. Nothing in the process could write
                // again, including the code trying to record that the scan had failed, so a
                // reported failure became a silent hang instead.
                lease = await DatabaseWriteLane.EnterAsync(conn, CancellationToken.None);
                await verifyRoots();
                tx = conn.BeginTransaction();

                var sinceCommit = 0;

                foreach (var path in EnumerateFilesSafe(folder, ct, unreadable))
                {
                    if (ct.IsCancellationRequested) break;
                    if (!IsVideoFile(path)) continue;

                    try
                    {
                        await conn.ExecuteAsync("SAVEPOINT record_file", transaction: tx);
                        if (!VolumePaths.TryRelative(folder, path, out var withinFolder))
                            throw new LocationChangedException("The scan returned a file outside its selected folder.");
                        var parsingPath = VolumePaths.Join(parsingRoot, withinFolder).Replace('\\', Path.DirectorySeparatorChar);
                        Tally(counts, await RecordFileAsync(conn, tx, catalogue, files, counts, scanId, path, parsingPath));
                        await conn.ExecuteAsync("RELEASE record_file", transaction: tx);
                    }
                    catch (Exception ex)
                    {
                        await conn.ExecuteAsync("ROLLBACK TO record_file; RELEASE record_file", transaction: tx);
                        counts.Failed++;
                        progress?.Report($"Error: {ex.Message} ({path})");
                        if (ex is LocationChangedException or SqliteException { SqliteExtendedErrorCode: 2067 })
                            throw new IOException("The file binding changed or collided during the scan. Resolve the conflict before retrying.", ex);
                        unreadable.Add(path);
                        catalogue.Movies = await LoadMovieIndexAsync(conn);
                        catalogue.Episodes = await EpisodeCatalogue.LoadAsync(conn);
                    }

                    if (++sinceCommit < FilesPerTransaction) continue;

                    tx.Commit();
                    tx.Dispose();
                    tx = null;
                    lease.Dispose();
                    lease = null;

                    lease = await DatabaseWriteLane.EnterAsync(conn, CancellationToken.None);
                    await verifyRoots();
                    tx = conn.BeginTransaction();
                    sinceCommit = 0;
                }

                completed = !ct.IsCancellationRequested;
                tx.Commit();
            }
            catch (OperationCanceledException)
            {
                // Whatever was catalogued before the cancellation is worth keeping: every write
                // here is idempotent, so a resumed scan simply carries on.
                tx?.Commit();
            }
            finally
            {
                tx?.Dispose();
                lease?.Dispose();
            }

            return completed;
        }

        private static void Tally(ScanCounts counts, FileOutcome outcome)
        {
            switch (outcome)
            {
                case FileOutcome.Inserted: counts.Inserted++; break;
                case FileOutcome.Moved: counts.Moved++; break;
                case FileOutcome.Updated: counts.Updated++; break;
                case FileOutcome.Unchanged: counts.Unchanged++; break;
            }
        }

        /// <summary>
        /// Writes a genuinely new binding. A concurrent identity collision is a visible failure,
        /// not permission to overwrite another file's association through its old mount path.
        /// </summary>
        private const string InsertFileSql = @"
INSERT INTO files (movie_id, file_path, size_bytes, created_at, updated_at, last_seen_at, last_seen_scan_id,
location_mode, volume_id, relative_path, relative_key, path_style)
VALUES (@movie_id, @file_path, @size_bytes, @created_at, @updated_at, @last_seen_at, @scan_id,
@mode, @volume, @relative, @key, @style)
RETURNING id;
";

        /// <summary>
        /// Updates the row for a file that already had one, whether it is still where it was or is
        /// being relinked to a path it has moved to.
        ///
        /// <c>movie_id</c> is coalesced rather than assigned: a link a person made by hand is
        /// better evidence than a filename has ever been, and a scan must not overwrite it.
        /// <c>created_at</c> is coalesced the other way round, keeping what was recorded when the
        /// file cannot be stat'd now, because a value that was true once beats a null.
        /// </summary>
        private const string UpdateFileSql = @"
UPDATE files SET
    movie_id          = COALESCE(movie_id, @movie_id),
    file_path         = @file_path,
    size_bytes        = @size_bytes,
    created_at        = COALESCE(@created_at, created_at),
    updated_at        = @updated_at,
    last_seen_at      = @last_seen_at,
    last_seen_scan_id = @scan_id,
    missing_since     = NULL,
    location_mode=@mode, volume_id=@volume, relative_path=@relative, relative_key=@key, path_style=@style,
    location_revision=location_revision+1
WHERE id = @id AND location_revision=@revision
AND movie_id IS @original_movie AND episode_id IS @original_episode AND linked_by_hand=@original_hand;
";

        /// <summary>
        /// Writes one file and the film or episode it is, and says what became of it.
        /// </summary>
        private static async Task<FileOutcome> RecordFileAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            Catalogue catalogue,
            ScanFileIndex files,
            ScanCounts counts,
            long scanId,
            string path,
            string parsingPath)
        {
            var existing = files.ByPath(path);

            // Two watch folders where one contains the other. Doing the work twice would count the
            // same file twice and report a library as double the size it is.
            if (existing is not null && files.WasSeen(existing)) return FileOutcome.AlreadySeen;

            // A moved file is resolved before anything is decided about it, because what it was
            // decides what it is now: a film somebody linked by hand is still that film wherever it
            // went, and an episode moved somewhere its path no longer reads as one is still that
            // episode. Deciding first and finding the old row afterwards let both slip through.
            var moved = false;
            if (existing is null)
            {
                var info = new FileInfo(path);
                existing = files.FindMoved(path, info.Exists ? info.Length : 0L);
                moved = existing is not null;
            }

            if (existing is { LinkedByHand: true, EpisodeId: long chosenEpisode })
                return await RecordEpisodeFileAsync(
                    conn, tx, files, counts, scanId, path, existing, moved, chosenEpisode);
            if (!moved && existing?.EpisodeId is long sameEpisode)
                return await RecordEpisodeFileAsync(
                    conn, tx, files, counts, scanId, path, existing, moved, sameEpisode);

            // An episode first, because the film parser has no idea what one is: handed
            // "Show.S01E02.mkv" it produces a film called something like "Show", and every episode
            // of the programme lands on that one oddly named film. EpisodeParser is conservative —
            // it declines anything it cannot read with confidence — so a decline falls through to
            // exactly what happened to every file before it existed.
            //
            // A file a person linked to a film by hand stays that film. Their say-so is better
            // evidence than a filename, which is the rule the film path already keeps.
            if (existing is not { LinkedByHand: true, MovieId: not null }
                && EpisodeParser.TryParse(parsingPath, out var parsed))
            {
                var episodeId = await EpisodeForFileAsync(conn, tx, catalogue.Episodes, parsed);
                return await RecordEpisodeFileAsync(
                    conn, tx, files, counts, scanId, path, existing, moved, episodeId);
            }

            // Filed as an episode by an earlier scan, and no longer read as one — a season folder
            // flattened, or a change to the parser. The episode keeps it, as a film keeps a file
            // whose name stopped matching it: inventing a film for it would make it both.
            if (existing?.EpisodeId is long kept)
                return await RecordEpisodeFileAsync(
                    conn, tx, files, counts, scanId, path, existing, moved, kept);

            return await RecordFilmFileAsync(conn, tx, catalogue.Movies, files, scanId, path, existing, moved, parsingPath);
        }

        /// <summary>
        /// Writes one file and the movie it belongs to, and says which of those it was.
        ///
        /// Every path this scan walks past is stamped with <c>last_seen_at</c> and the scan's id,
        /// including one that has not changed since the last scan. That is the point of the
        /// column: a row nobody stamped is a row nothing found, and a file that is exactly as it
        /// was is still very much there.
        /// </summary>
        private static async Task<FileOutcome> RecordFilmFileAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            MovieIndex movies,
            ScanFileIndex files,
            long scanId,
            string path,
            ScanFileRow? existing,
            bool moved,
            string? parsingPath = null)
        {
            var info = new FileInfo(path);
            var size = info.Exists ? info.Length : 0L;
            var created = info.Exists ? info.CreationTimeUtc.ToString("o") : null;
            var modified = info.Exists ? info.LastWriteTimeUtc.ToString("o") : null;
            var seenAt = ScanSessions.Timestamp(DateTimeOffset.UtcNow);

            // A moved file is consulted about its link exactly as one still at its own path is. It
            // used to be resolved by name alone, and a name that no longer matched the row it was
            // linked to — an old noisy title, a corrected one — inserted a new row for it while the
            // update below kept the file on the old one, so every such move left an empty film.
            var movieId = await MovieForFileAsync(conn, tx, movies, existing, FilenameParser.Parse(parsingPath ?? path));

            var outcome = FileOutcome.Unchanged;
            if (moved)
            {
                outcome = FileOutcome.Moved;
            }
            else if (existing is not null && existing.DiffersFrom(size, modified, movieId))
            {
                outcome = FileOutcome.Updated;
            }

            var binding = files.BindingFor(path);
            var parameters = new
            {
                id = existing?.Id ?? 0L,
                movie_id = movieId,
                file_path = path,
                size_bytes = size,
                created_at = created,
                updated_at = modified,
                last_seen_at = seenAt,
                scan_id = scanId,
                mode = binding.Mode, volume = binding.VolumeId, relative = binding.RelativePath,
                key = binding.RelativeKey, style = binding.PathStyle, revision = existing?.Revision ?? 0,
                original_movie = existing?.MovieId, original_episode = existing?.EpisodeId, original_hand = existing?.LinkedByHand ?? false,
            };

            if (existing is null)
            {
                var id = await conn.ExecuteScalarAsync<long>(InsertFileSql, parameters, tx);

                files.Add(new ScanFileRow
                {
                    Id = id,
                    MovieId = movieId,
                    FilePath = path,
                    SizeBytes = size,
                    UpdatedAt = modified,
                    Mode = binding.Mode, VolumeId = binding.VolumeId, RelativePath = binding.RelativePath,
                    RelativeKey = binding.RelativeKey, PathStyle = binding.PathStyle, Resolved = true,
                });

                return FileOutcome.Inserted;
            }

            if (await conn.ExecuteAsync(UpdateFileSql, parameters, tx) != 1)
                throw new LocationChangedException("The file binding changed during the scan.");
            existing.Revision++;

            files.SetBinding(existing, binding);

            existing.MovieId ??= movieId;
            existing.SizeBytes = size;
            existing.UpdatedAt = modified;
            existing.MissingSince = null;
            files.MarkSeen(existing);

            return outcome;
        }

        /// <summary>The two in-memory indexes a scan files against, carried as one.</summary>
        private sealed class Catalogue(MovieIndex movies, EpisodeCatalogue episodes)
        {
            public MovieIndex Movies { get; set; } = movies;
            public EpisodeCatalogue Episodes { get; set; } = episodes;
        }

        /// <summary>
        /// Writes a new episode file with the same identity constraints as a film file.
        /// </summary>
        /// <remarks>
        /// <c>movie_id</c> is cleared rather than coalesced, because a file is a film or an episode
        /// and this one has just been read as an episode. The only film link that may survive that
        /// is one a person made, and the caller does not come here for one of those.
        /// </remarks>
        private const string InsertEpisodeFileSql = @"
INSERT INTO files (movie_id, episode_id, file_path, size_bytes, created_at, updated_at, last_seen_at, last_seen_scan_id,
location_mode, volume_id, relative_path, relative_key, path_style)
VALUES (NULL, @episode_id, @file_path, @size_bytes, @created_at, @updated_at, @last_seen_at, @scan_id,
@mode, @volume, @relative, @key, @style)
RETURNING id;
";

        /// <summary>
        /// Updates the row for a file read as an episode, whether it was already one, was a film
        /// until now, or has moved. The caller preserves a manual assignment before asking the
        /// parser, so only automatically filed episodes follow a different programme's folder.
        /// </summary>
        private const string UpdateEpisodeFileSql = @"
UPDATE files SET
    movie_id          = NULL,
    episode_id        = @episode_id,
    file_path         = @file_path,
    size_bytes        = @size_bytes,
    created_at        = COALESCE(@created_at, created_at),
    updated_at        = @updated_at,
    last_seen_at      = @last_seen_at,
    last_seen_scan_id = @scan_id,
    missing_since     = NULL,
    location_mode=@mode, volume_id=@volume, relative_path=@relative, relative_key=@key, path_style=@style,
    location_revision=location_revision+1
WHERE id = @id AND location_revision=@revision
AND movie_id IS @original_movie AND episode_id IS @original_episode AND linked_by_hand=@original_hand;
";

        /// <summary>
        /// Writes a file that is an episode, re-filing it when an earlier scan made it a film.
        /// </summary>
        /// <remarks>
        /// The re-filing is how a library scanned before television existed gets its programmes
        /// back, and it is done here rather than in <see cref="Database.Migrate"/> because only a
        /// scan reads paths. A schema migration that parsed every path in <c>files</c> would be a
        /// scan that ran without being asked, on a drive that may not be plugged in.
        ///
        /// The film row the file leaves is not judged here. A film keeps every other file it has,
        /// and once the walk is over <see cref="SweepOrphansAsync"/> clears whatever row this left
        /// with none at all, unless a person put something into it.
        /// </remarks>
        private static async Task<FileOutcome> RecordEpisodeFileAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            ScanFileIndex files,
            ScanCounts counts,
            long scanId,
            string path,
            ScanFileRow? existing,
            bool moved,
            long episodeId)
        {
            var info = new FileInfo(path);
            var size = info.Exists ? info.Length : 0L;
            var created = info.Exists ? info.CreationTimeUtc.ToString("o") : null;
            var modified = info.Exists ? info.LastWriteTimeUtc.ToString("o") : null;
            var seenAt = ScanSessions.Timestamp(DateTimeOffset.UtcNow);

            var outcome = FileOutcome.Unchanged;
            if (moved)
            {
                outcome = FileOutcome.Moved;
            }
            else if (existing is not null && existing.DiffersFromEpisode(size, modified, episodeId))
            {
                outcome = FileOutcome.Updated;
            }

            var binding = files.BindingFor(path);
            var parameters = new
            {
                id = existing?.Id ?? 0L,
                episode_id = episodeId,
                file_path = path,
                size_bytes = size,
                created_at = created,
                updated_at = modified,
                last_seen_at = seenAt,
                scan_id = scanId,
                mode = binding.Mode, volume = binding.VolumeId, relative = binding.RelativePath,
                key = binding.RelativeKey, style = binding.PathStyle, revision = existing?.Revision ?? 0,
                original_movie = existing?.MovieId, original_episode = existing?.EpisodeId, original_hand = existing?.LinkedByHand ?? false,
            };

            if (existing is null)
            {
                var id = await conn.ExecuteScalarAsync<long>(InsertEpisodeFileSql, parameters, tx);

                files.Add(new ScanFileRow
                {
                    Id = id,
                    EpisodeId = episodeId,
                    FilePath = path,
                    SizeBytes = size,
                    UpdatedAt = modified,
                    Mode = binding.Mode, VolumeId = binding.VolumeId, RelativePath = binding.RelativePath,
                    RelativeKey = binding.RelativeKey, PathStyle = binding.PathStyle, Resolved = true,
                });

                return FileOutcome.Inserted;
            }

            if (await conn.ExecuteAsync(UpdateEpisodeFileSql, parameters, tx) != 1)
                throw new LocationChangedException("The file binding changed during the scan.");
            existing.Revision++;

            if (existing.MovieId is not null) counts.Refiled++;

            files.SetBinding(existing, binding);

            existing.MovieId = null;
            existing.EpisodeId = episodeId;
            existing.SizeBytes = size;
            existing.UpdatedAt = modified;
            existing.MissingSince = null;
            files.MarkSeen(existing);

            return outcome;
        }

        /// <summary>
        /// The episode a parsed path is, creating the programme and the episode when the catalogue
        /// has neither yet. Both are resolved in memory first, so a season of twenty-four files is
        /// one programme insert and twenty-four episode inserts, not a query per file each.
        /// </summary>
        private static async Task<long> EpisodeForFileAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            EpisodeCatalogue catalogue,
            ParsedEpisode parsed)
        {
            var seriesId = await EnsureSeriesAsync(conn, tx, catalogue, parsed);
            var title = string.IsNullOrWhiteSpace(parsed.EpisodeTitle) ? null : parsed.EpisodeTitle.Trim();

            if (catalogue.TryResolveEpisode(seriesId, parsed.SeasonNumber, parsed.EpisodeNumber, out var existing))
            {
                // A second copy of an episode sometimes names it where the first did not. Filled in
                // once, never overwritten: the first title a scan found is as good as the second.
                if (title is not null && !catalogue.HasTitle(existing))
                {
                    await conn.ExecuteAsync(
                        "UPDATE episodes SET title = @title WHERE id = @id AND (title IS NULL OR title = '')",
                        new { title, id = existing }, tx);
                    catalogue.MarkTitled(existing);
                }

                return existing;
            }

            var id = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO episodes (series_id, season_number, episode_number, title)
VALUES (@series_id, @season, @episode, @title)
ON CONFLICT DO UPDATE SET title = COALESCE(episodes.title, excluded.title)
RETURNING id",
                new { series_id = seriesId, season = parsed.SeasonNumber, episode = parsed.EpisodeNumber, title }, tx);

            catalogue.AddEpisode(id, seriesId, parsed.SeasonNumber, parsed.EpisodeNumber, title is not null);
            return id;
        }

        private static async Task<long> EnsureSeriesAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            EpisodeCatalogue catalogue,
            ParsedEpisode parsed)
        {
            if (catalogue.TryResolveSeries(parsed, out var existing, out var yearIsNew))
            {
                if (yearIsNew && parsed.SeriesYear is int year)
                {
                    await conn.ExecuteAsync(
                        "UPDATE series SET year = @year WHERE id = @id AND year IS NULL",
                        new { year, id = existing }, tx);
                    catalogue.SetSeriesYear(existing, parsed.SeriesTitle, year);
                }

                return existing;
            }

            // RETURNING for the same reason as the movies insert: the FTS trigger writes after it.
            var id = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO series (title, year, created_at) VALUES (@title, @year, @created_at) RETURNING id",
                new
                {
                    title = parsed.SeriesTitle,
                    year = parsed.SeriesYear,
                    created_at = ScanSessions.Timestamp(DateTimeOffset.UtcNow)
                }, tx);

            catalogue.AddSeries(id, parsed.SeriesTitle, parsed.SeriesYear);
            return id;
        }

        /// <summary>
        /// Marks every row a completed scan did not see, under a folder it actually walked, and
        /// returns how many that was.
        ///
        /// Marked, never deleted. The issue this closes is explicit about it and it is the right
        /// call: this process cannot tell a film somebody deleted from a film on a drive that is
        /// not plugged in, and only one of those readings is recoverable from. The mark is also
        /// what gives an eventual prune something to count from — a row missing since March across
        /// several scans is a very different claim from a row missing since a minute ago.
        ///
        /// Batched the same way the walk is, and for the same reason: a folder that lost a
        /// thousand files should not hold the write lane for as long as it takes to say so.
        /// </summary>
        private static async Task<int> MarkMissingAsync(
            SqliteConnection conn,
            ScanFileIndex files,
            IReadOnlyCollection<string> walked,
            IReadOnlyCollection<string> unreadable,
            DateTimeOffset markedAt,
            Func<Task> verifyRoots)
        {
            var gone = files.Unseen(walked, unreadable);
            if (gone.Count == 0) return 0;

            const string sql = "UPDATE files SET missing_since = @missing_since WHERE id = @id AND missing_since IS NULL AND location_revision=@revision;";
            var stamp = ScanSessions.Timestamp(markedAt);

            foreach (var batch in gone.Chunk(FilesPerTransaction))
            {
                await DatabaseWriteLane.RunAsync(
                    conn,
                    async _ =>
                    {
                        await verifyRoots();
                        using var tx = conn.BeginTransaction();
                        var changed = await conn.ExecuteAsync(
                            sql,
                            batch.Select(row => new { id = row.Id, missing_since = stamp, revision = row.Revision }),
                            tx);
                        if (changed != batch.Length)
                            throw new LocationChangedException("File locations changed before missing-file marking. Scan again.");
                        tx.Commit();
                    },
                    CancellationToken.None);
            }

            foreach (var row in gone) row.MissingSince = stamp;

            return gone.Count;
        }

        /// <summary>
        /// Clears out any row that is only another row's discarded name, through the write lane
        /// and in its own transaction.
        /// </summary>
        /// <remarks>
        /// Best effort, and not counted in the result. A scan's job is to reconcile the catalogue
        /// with the disk, which it has already done by the time this runs; failing the whole scan
        /// over a tidy-up would turn a library that is now correct into an error message. The next
        /// completed scan tries again.
        ///
        /// Its own lane turn rather than one of the walk's, for the same reason the walk takes one
        /// per batch: a sweep is a short write and has no business holding the lane open across
        /// anything else.
        /// </remarks>
        private static async Task SweepDiscardedNamesAsync(SqliteConnection conn)
        {
            try
            {
                await DatabaseWriteLane.RunAsync(
                    conn,
                    token => DiscardedNames.SweepAsync(conn, tx: null, token),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Swallowed, but not silently. A sweep that fails every time is a bug that would
                // otherwise leave no trace anywhere, because its whole job is to change nothing
                // visible when there is nothing to clear up.
                AppLog.Write("scan.log", $"could not sweep discarded names: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears out every film row with no file at all and nothing a person put into it, through
        /// the write lane and in its own transaction. See <see cref="OrphanFilms"/>.
        /// </summary>
        /// <remarks>
        /// This is how an episode an earlier scan filed as a film stops being a film once it has
        /// been re-filed under its programme. It used to be decided file by file as each one was
        /// re-filed, and the rule there kept a row for having a TMDB id or a <c>scan_title</c> —
        /// both of which the app writes on its own, so the rows it kept were mostly the very ones
        /// it had misread. Deciding once, after the walk, also catches a row emptied some other
        /// way, and one an older scan created without a file in the first place.
        ///
        /// Best effort, like the sweep above, and for the same reason.
        /// </remarks>
        private static async Task SweepOrphansAsync(SqliteConnection conn)
        {
            try
            {
                await DatabaseWriteLane.RunAsync(
                    conn,
                    _ =>
                    {
                        using var tx = conn.BeginTransaction();
                        OrphanFilms.Sweep(conn, tx);
                        tx.Commit();
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLog.Write("scan.log", $"could not clear film rows with no file: {ex.Message}");
            }
        }

        /// <summary>
        /// Records how a scan ended when it is already on its way out with an exception.
        ///
        /// Best effort by design. The usual reason a scan throws is that the database went away
        /// underneath it, in which case this cannot work either — and letting it throw would
        /// replace the caller's exception with a worse one that hides what actually happened.
        /// </summary>
        private static async Task CloseQuietlyAsync(SqliteConnection conn, ScanResult result)
        {
            try
            {
                await ScanSessions.FinishAsync(conn, result, DateTimeOffset.UtcNow);
            }
            catch (Exception)
            {
                // Deliberately swallowed; see above.
            }
        }

        /// <summary>
        /// Which movie a walked file belongs to.
        /// </summary>
        /// <remarks>
        /// A file already linked to a row keeps that link — the update coalesces it — so a parsed
        /// name that no row answers to must not create one. That happens whenever the name read
        /// from disk differs from the row's: a file linked by hand, a file renamed on disk, and
        /// above all a library scanned before the parser learned to cut a title at its first
        /// release tag. Inserting would leave an empty row beside the real one on every one of
        /// them.
        ///
        /// The last case is also worth repairing rather than merely not making worse: the row is
        /// called "Origen MicroHD", and that name is why TMDB never found it. When the row's own
        /// title holds an unambiguous release tag, cleans down to exactly what the file now
        /// parses as, and nothing has identified it yet, the row takes the clean title and keeps
        /// the old one in <c>scan_title</c> — the same place a corrected match keeps the name it
        /// replaced, so the rename is recorded and the old name still resolves. A title that only
        /// ends in an ambiguous word ("The Rip", "Charlotte's Web") is never touched: it is far
        /// likelier to be right than noisy. Nor is a title a person or a corrected match chose, or
        /// one TMDB already identified. An earlier automatic cleanup may be refined when its
        /// original scanned name still cleans to the same title; title-keyed journal entries move
        /// with it in the scan transaction.
        /// </remarks>
        private static async Task<long> MovieForFileAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            MovieIndex movies,
            ScanFileRow? existing,
            ParsedMedia parsed)
        {
            if (existing?.MovieId is not long linked || movies.TryResolve(parsed, out _, out _))
                return await EnsureMovieAsync(conn, tx, movies, parsed);
            if (existing is { LinkedByHand: true }) return linked;

            var row = await conn.QuerySingleOrDefaultAsync<LinkedMovieRow>(
                "SELECT title AS Title, year AS Year, tmdb_id AS TmdbId, scan_title AS ScanTitle, matched_by_hand AS MatchedByHand FROM movies WHERE id = @id",
                new { id = linked }, tx);

            if (row is not null && IsStaleScannedTitle(row, parsed))
            {
                // Keep the first scanned name even if another parser update cleans it further.
                var renamed = await conn.ExecuteAsync(
                    @"UPDATE movies SET scan_title = COALESCE(scan_title, title), title = @title, year = COALESCE(year, @year)
                      WHERE id = @id AND tmdb_id IS NULL AND matched_by_hand = 0",
                    new { title = parsed.Title, year = parsed.Year, id = linked }, tx);

                // An alias rather than a new entry: the index counts films, and a scan reports how
                // many it added from that count, so a rename must not read as a film added.
                if (renamed > 0)
                {
                    FilmJournalStore.RenameUnidentified(
                        conn, tx, row.Title, row.Year, parsed.Title, row.Year ?? parsed.Year);
                    movies.AddAlias(linked, parsed.Title, row.Year ?? parsed.Year);
                }
            }

            return linked;
        }

        private static bool IsStaleScannedTitle(LinkedMovieRow row, ParsedMedia parsed)
        {
            if (row.TmdbId is not null || row.MatchedByHand) return false;
            if (string.IsNullOrWhiteSpace(row.Title) || string.IsNullOrWhiteSpace(parsed.Title)) return false;
            if (string.Equals(row.Title, parsed.Title, StringComparison.Ordinal)) return false;
            if (row.Year.HasValue && parsed.Year.HasValue && row.Year != parsed.Year) return false;
            if (!FilenameParser.CarriesReleaseTag(row.Title)) return false;

            var cleaned = MovieIndex.NormalizeTitle(FilenameParser.CleanText(row.Title));
            if (!string.IsNullOrWhiteSpace(row.ScanTitle)
                && (!FilenameParser.CarriesReleaseTag(row.ScanTitle)
                    || MovieIndex.NormalizeTitle(FilenameParser.CleanText(row.ScanTitle)) != cleaned))
                return false;
            return cleaned.Length > 0 && cleaned == MovieIndex.NormalizeTitle(parsed.Title);
        }

        private sealed class LinkedMovieRow
        {
            public string? Title { get; set; }
            public int? Year { get; set; }
            public long? TmdbId { get; set; }
            public string? ScanTitle { get; set; }
            public bool MatchedByHand { get; set; }
        }

        /// <summary>
        /// Returns the id of the movie a parsed filename belongs to, creating the row when nothing
        /// matches. Matching happens in <see cref="MovieIndex"/> rather than in SQL so that two
        /// spellings of one title collapse onto a single row.
        /// </summary>
        private static async Task<long> EnsureMovieAsync(SqliteConnection conn, SqliteTransaction tx, MovieIndex index, ParsedMedia parsed)
        {
            if (string.IsNullOrWhiteSpace(parsed.Title))
                throw new InvalidOperationException("The filename produced no usable title.");

            if (index.TryResolve(parsed, out var existingId, out var yearIsNewInformation))
            {
                if (yearIsNewInformation && parsed.Year.HasValue)
                {
                    await conn.ExecuteAsync(
                        "UPDATE movies SET year = @year WHERE id = @id AND year IS NULL",
                        new { year = parsed.Year.Value, id = existingId }, tx);

                    index.SetYear(existingId, parsed.Title, parsed.Year.Value);
                }

                return existingId;
            }

            // RETURNING rather than last_insert_rowid(): the FTS triggers on movies write to the
            // index straight after this row, and last_insert_rowid() would report that write.
            var id = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO movies (title, year) VALUES (@title, @year) RETURNING id",
                new { title = parsed.Title, year = parsed.Year }, tx);

            index.Add(id, parsed.Title, parsed.Year);
            return id;
        }

        /// <summary>
        /// Catalogues a single file that arrived outside a scan — today, a film downloaded from
        /// the Jellyfin server. Returns the id of the movie row it belongs to.
        ///
        /// It exists so a download is playable the instant it finishes rather than after the user
        /// works out that a scan is what makes a film appear. It writes through the same binding
        /// and title indexes a scan uses, so a later scan of the download folder agrees with it:
        /// volume-relative identity prevents a second file row, and the title
        /// resolves through <see cref="MovieIndex"/>, so a download of a film already in the
        /// library links to the row that is already there rather than forking it.
        ///
        /// <c>last_seen_scan_id</c> is left null because no scan found this file — a download is
        /// not a scan, and borrowing an id from one would put this row in a session that never
        /// walked past it. <c>last_seen_at</c> is stamped, because the file is demonstrably there.
        /// </summary>
        public static async Task<long> RecordSingleFileAsync(SqliteConnection conn, string path,
            IVolumeSnapshotSource? volumes = null, CancellationToken ct = default)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", nameof(path));

            var snapshot = await (volumes ?? VolumeSnapshotSource.Shared).CaptureAsync(new[] { path }, ct);
            return await DatabaseWriteLane.RunAsync(conn, async _ =>
            {
                var movies = await LoadMovieIndexAsync(conn);
                var files = await ScanFileIndex.LoadAsync(conn, snapshot);
                using var tx = conn.BeginTransaction();
                await RecordFilmFileAsync(conn, tx, movies, files, 0, path, files.ByPath(path), false);
                var file = files.ByPath(path) ?? throw new InvalidOperationException("The downloaded file was not recorded.");
                await conn.ExecuteAsync("UPDATE files SET last_seen_scan_id=NULL WHERE id=@Id", new { file.Id }, tx);
                tx.Commit();
                return file.MovieId ?? throw new InvalidOperationException("The download has no movie association.");
            }, ct);
        }

        /// <summary>
        /// Loads the catalogue into memory once per scan. A personal library is small enough that
        /// this costs less than a query per file, and it lets the matching rules stay pure.
        /// </summary>
        private static async Task<MovieIndex> LoadMovieIndexAsync(SqliteConnection conn)
        {
            var index = new MovieIndex();

            var rows = await conn.QueryAsync<MovieRow>(
                "SELECT id AS Id, title AS Title, year AS Year, scan_title AS ScanTitle FROM movies ORDER BY id");

            foreach (var row in rows)
            {
                index.Add(row.Id, row.Title, row.Year);

                // A film whose displayed title was corrected still arrives from disk under the name
                // the scan gave it. Filed under both, the re-scan finds the row it already made
                // instead of creating a second one beside it.
                if (!string.IsNullOrWhiteSpace(row.ScanTitle))
                    index.AddAlias(row.Id, row.ScanTitle, row.Year);
            }

            return index;
        }

        private sealed class MovieRow
        {
            public long Id { get; set; }
            public string? Title { get; set; }
            public int? Year { get; set; }

            /// <summary>Null unless the film has been renamed by a corrected TMDB match.</summary>
            public string? ScanTitle { get; set; }
        }

        /// <summary>
        /// Walks <paramref name="root"/> depth-first, skipping directories the OS refuses to read.
        /// macOS in particular denies access to folders such as ~/Library without a TCC grant, so a
        /// single unreadable subtree must not abort the whole scan.
        /// </summary>
        /// <param name="unreadable">
        /// Collects the directories that were refused, when a caller passes one. Skipping them
        /// quietly is right for the walk and wrong for what follows it: a folder nobody was
        /// allowed to open holds files that were not seen and are not gone, and marking a library
        /// missing the first time macOS withholds a TCC grant would be a spectacular way to answer
        /// a permission prompt.
        /// </param>
        public static IEnumerable<string> EnumerateFilesSafe(string root, CancellationToken ct = default, ICollection<string>? unreadable = null)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();

                var refused = false;

                string[] subDirs = Array.Empty<string>();
                try { subDirs = Directory.GetDirectories(dir); } catch { refused = true; }
                foreach (var sd in subDirs)
                {
                    if ((File.GetAttributes(sd) & FileAttributes.ReparsePoint) != 0)
                    {
                        unreadable?.Add(sd);
                        continue;
                    }
                    stack.Push(sd);
                }

                string[] files = Array.Empty<string>();
                try { files = Directory.GetFiles(dir); } catch { refused = true; }
                foreach (var f in files) yield return f;

                // A root that fails is usually a root that is simply not there, which the caller
                // has already checked for and reports as a skipped folder rather than a refusal.
                if (refused && unreadable is not null && Directory.Exists(dir)) unreadable.Add(dir);
            }
        }
    }
}
