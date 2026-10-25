using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// <see cref="FilmJournalStore"/> for the film page: given a database path rather than a
    /// connection, off the UI thread, and with writes queued in <see cref="DatabaseWriteLane"/>
    /// behind whatever a scan or a poster fetch is doing.
    /// </summary>
    /// <remarks>
    /// The path is passed in rather than worked out, which is what lets a test point it at a
    /// temporary file and be certain nothing reaches the real catalogue.
    /// </remarks>
    public sealed class FilmJournal
    {
        private readonly Func<DateTime> _utcNow;

        public FilmJournal(string databasePath, Func<DateTime>? utcNow = null)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("A database path is required.", nameof(databasePath));

            DatabasePath = databasePath;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public string DatabasePath { get; }

        public Task<FilmJournalEntry> LoadAsync(FilmIdentity film, CancellationToken ct = default) =>
            Task.Run(() =>
            {
                if (!film.CanBeJournalled) return FilmJournalEntry.Empty;

                using var conn = Database.Open(DatabasePath);
                ct.ThrowIfCancellationRequested();
                return FilmJournalStore.Load(conn, film);
            }, ct);

        public Task SaveRatingAsync(FilmIdentity film, int? stars, string? review, CancellationToken ct = default) =>
            WriteAsync(conn =>
            {
                FilmJournalStore.SaveRating(conn, film, stars, review, _utcNow());
                return true;
            }, ct);

        public Task<long> AddViewingAsync(
            FilmIdentity film,
            DateOnly watchedOn,
            IEnumerable<string?>? companions,
            string? note,
            CancellationToken ct = default) =>
            WriteAsync(conn => FilmJournalStore.AddViewing(conn, film, watchedOn, companions, note, _utcNow()), ct);

        public Task<bool> RemoveViewingAsync(long id, CancellationToken ct = default) =>
            WriteAsync(conn => FilmJournalStore.RemoveViewing(conn, id), ct);

        /// <summary>See <see cref="FilmJournalStore.Adopt"/>.</summary>
        public Task AdoptAsync(string? fromKey, FilmIdentity film, CancellationToken ct = default) =>
            WriteAsync(conn =>
            {
                FilmJournalStore.Adopt(conn, fromKey, film);
                return true;
            }, ct);

        /// <summary>See <see cref="FilmJournalStore.Correct"/>.</summary>
        public Task CorrectAsync(string? fromKey, FilmIdentity film, CancellationToken ct = default) =>
            WriteAsync(conn =>
            {
                FilmJournalStore.Correct(conn, fromKey, film);
                return true;
            }, ct);

        private Task<T> WriteAsync<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> write, CancellationToken ct) =>
            Task.Run(async () =>
            {
                using var conn = Database.Open(DatabasePath);
                return await DatabaseWriteLane.RunAsync(conn, _ => Task.FromResult(write(conn)), ct)
                    .ConfigureAwait(false);
            }, ct);
    }
}
