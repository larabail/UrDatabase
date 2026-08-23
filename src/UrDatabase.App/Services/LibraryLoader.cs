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
    /// same words, fold the two together and describe the result.
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
            var matched = match is null ? server : JellyfinLibrary.Search(server, query);

            // Already deduplicated by identity and ordered newest first, which is what both the
            // grouped view and the flat search results want. Sorting it a second time per view is
            // how the search path used to spend the UI thread twice over for one answer.
            var all = JellyfinLibrary.Merge(local, matched);

            var status = failure ?? LibraryStatus.Describe(
                localCount: local.Count,
                localWithPosters: local.Count(x => !string.IsNullOrWhiteSpace(x.PosterPath)),
                remoteCount: matched.Count,
                hasLocalDatabase: _repository.Exists,
                databasePath: _repository.DatabasePath);

            return new LibraryView(query, local, matched, all, status);
        }
    }
}
