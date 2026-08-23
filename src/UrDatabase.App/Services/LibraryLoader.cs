using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Builds one <see cref="LibraryView"/>: query the catalogue, narrow the server's films to the
    /// same words, fold the two together, retire whatever a scan can no longer find, and describe
    /// the result.
    ///
    /// This is what <c>MainWindow.LoadMovies</c> used to do inline, on the UI thread, on every
    /// keystroke. It is out here for two reasons. The first is that all of it — opening SQLite,
    /// materialising thousands of rows, merging and sorting them — is work the dispatcher should
    /// never see, and one class that does the whole read means one hop off the UI thread rather
    /// than an await between each step that hands the sorting straight back to it. The second is
    /// that a rule living in a window cannot be tested without a UI thread, which is why this had
    /// no test while it was in there.
    /// </summary>
    public sealed class LibraryLoader
    {
        private readonly MovieRepository _repository;
        private readonly Action<Exception>? _onQueryFailed;

        /// <param name="onQueryFailed">
        /// Called when the catalogue could not be read at all. The view still comes back, carrying
        /// an empty local half and a status line that says what went wrong, because a Jellyfin
        /// library has to stay browsable when the local one is missing or was written by an older
        /// schema. This callback exists so the failure also reaches a log the user can send on.
        /// </param>
        public LibraryLoader(MovieRepository repository, Action<Exception>? onQueryFailed = null)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _onQueryFailed = onQueryFailed;
        }

        /// <summary>
        /// The same read, off the calling thread.
        /// </summary>
        /// <param name="remote">
        /// The server's whole library, unfiltered. Copied here, on the caller's thread, because
        /// the list belongs to the window and a Jellyfin sync may replace it while this read is
        /// still running.
        /// </param>
        public Task<LibraryView> LoadAsync(
            string? query,
            IReadOnlyList<UiMovie>? remote = null,
            CancellationToken ct = default)
        {
            var snapshot = remote is null || remote.Count == 0
                ? Array.Empty<UiMovie>()
                : remote.ToArray();

            return Task.Run(() => Load(query, snapshot, ct), ct);
        }

        /// <summary>
        /// The read itself. Synchronous, so a test can assert on it without scheduling anything,
        /// and so the caller decides which thread pays for it.
        /// </summary>
        public LibraryView Load(
            string? query,
            IReadOnlyList<UiMovie>? remote = null,
            CancellationToken ct = default)
        {
            // Built once so both halves agree on what counts as a search: text with no word in it
            // at all is not one, and showing the whole local library while hiding every server
            // film would look like the server had dropped out.
            var match = FtsQuery.Build(query);

            IReadOnlyList<UiMovie> local;
            string? failure = null;

            try
            {
                local = _repository.Query(match, ct);
            }
            catch (OperationCanceledException)
            {
                // Superseded, or the window is closing. Not a failure and not this class's to report.
                throw;
            }
            catch (Exception ex)
            {
                // A database from an older build may lack the tables this query needs. Report it
                // instead of taking the window down.
                _onQueryFailed?.Invoke(ex);
                local = Array.Empty<UiMovie>();
                failure = $"Could not read the library: {ex.Message}";
            }

            ct.ThrowIfCancellationRequested();

            var server = remote ?? Array.Empty<UiMovie>();

            // Folded against the whole server library rather than against the films the query
            // matched, and that ordering is load-bearing. The two sources routinely disagree about
            // a name — a file catalogued as "El Drama" is "The Drama" on the server, folded onto
            // it by TMDB id — so narrowing the server half first meant a search for "El" found the
            // local row, failed to find the server's copy of it, and concluded that nowhere else
            // had the film. Whether a film is in the library at all must not depend on what
            // somebody typed.
            //
            // Already deduplicated by identity and ordered newest first, which is what both the
            // grouped view and the flat search results want. Sorting it a second time per view is
            // how the search path used to spend the UI thread twice over for one answer.
            var merged = JellyfinLibrary.Merge(local, server);

            var matched = match is null ? server : JellyfinLibrary.Search(server, query);

            // The server half narrowed to the query, now that the folding is done. A card built
            // from a local row is kept whatever the server half says: it is in this list because
            // the catalogue's own index matched it, and a server twin that happens to be spelled
            // differently is not a reason to drop it.
            var narrowed = match is null ? merged : OnlyMatching(merged, matched);

            // After the merge and never before it. Whether a film whose file is gone leaves the
            // library or degrades to a server film turns on whether anywhere else has it, and a
            // local row only learns that when the server's copy is folded onto it.
            var all = MissingFilms.Retire(narrowed);

            // The catalogue as it now stands, rather than as the query returned it. A film that
            // has just been taken out of the library must not still be counted in the line under
            // it, or the wall shows nine films and the status line insists there are ten. A film
            // that stayed on as a server film is not counted either: it no longer plays from this
            // disk, which is the whole of what this half of the line is about.
            var shown = all.Where(m => m.IsOnThisComputer).ToList();

            var status = failure ?? LibraryStatus.Describe(
                localCount: shown.Count,
                localWithPosters: shown.Count(x => !string.IsNullOrWhiteSpace(x.PosterPath)),
                remoteCount: matched.Count,
                hasLocalDatabase: _repository.Exists,
                databasePath: _repository.DatabasePath);

            return new LibraryView(query, shown, matched, all, status);
        }

        /// <summary>
        /// The merged library with the server-only cards the query did not match taken out.
        /// Matched on <see cref="UiMovie.Key"/> rather than on the object, because a server film
        /// folded onto a local row is no longer a card of its own and must not be looked for.
        /// </summary>
        private static IReadOnlyList<UiMovie> OnlyMatching(
            IReadOnlyList<UiMovie> merged,
            IReadOnlyList<UiMovie> matched)
        {
            var wanted = new HashSet<string>(matched.Select(m => m.Key), StringComparer.Ordinal);

            return merged
                .Where(m => m.Source == MovieSource.Local || wanted.Contains(m.Key))
                .ToList();
        }
    }
}
