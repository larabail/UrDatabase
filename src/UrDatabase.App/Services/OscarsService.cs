using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// A film's Academy Awards, with a SQLite-backed cache in front of the lookup.
    /// </summary>
    /// <remarks>
    /// Modelled on <see cref="ImdbRatingService"/>, for the same two reasons and one extra. The
    /// upstream key allows sixty requests a minute, and browsing a library is a great many films
    /// opened in a short time. And the archive changes once a year, in March, so an answer taken
    /// today is still correct in November — the upstream documentation asks callers to cache for
    /// exactly this reason.
    ///
    /// The extra reason is that almost every film in almost every library was never nominated for
    /// anything, and that answer is remembered too: a row in <c>oscar_lookups</c> with no matching
    /// nominations means "asked already, there is nothing". Without it the commonest case would be
    /// the one that hit the network every single time.
    ///
    /// The whole title search is cached, not just the nominations attributed to the film, so that
    /// a corrected release year re-attributes what is already on disk without another request.
    /// </remarks>
    public sealed class OscarsService : IDisposable
    {
        /// <summary>
        /// Stands in for a release year the catalogue does not know. Zero rather than NULL because
        /// this is half of a primary key, and SQLite treats every NULL as distinct — every
        /// unknown-year film would get its own row and none of them would ever be found again.
        /// </summary>
        internal const int UnknownYear = 0;

        private readonly IOscarsLookup _lookup;
        private readonly bool _ownsLookup;

        public OscarsService(IOscarsLookup lookup, bool ownsLookup = false)
        {
            _lookup = lookup;
            _ownsLookup = ownsLookup;
        }

        public bool IsConfigured => _lookup.IsAvailable;

        /// <summary>
        /// What the Academy made of this film. Never throws and never returns null: an install
        /// with no key, a server that cannot be reached and a film nobody nominated all produce
        /// the same empty result, and the screen shows nothing in all three cases.
        /// </summary>
        public async Task<OscarHonours> GetAsync(
            SqliteConnection conn,
            string? title,
            int? year,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(title)) return OscarHonours.None;

            var key = title.Trim();

            if (TryReadCache(conn, key, year, out var cached))
                return OscarMatch.For(cached, year);

            // No key means the feature is off: no request, and no half-remembered answer written
            // to the cache that a configured install would then trust.
            if (!_lookup.IsAvailable) return OscarHonours.None;

            var found = await _lookup.LookupAsync(key, ct);

            // Null is "nobody knows", not "no awards". Writing it would make one rate-limited
            // afternoon permanent.
            if (found is null) return OscarHonours.None;

            WriteCache(conn, key, year, found);
            return OscarMatch.For(found, year);
        }

        /// <summary>
        /// Reads a previous answer. Returns false when this film has never been asked about, which
        /// is the only thing that triggers a request.
        /// </summary>
        internal static bool TryReadCache(
            SqliteConnection conn,
            string title,
            int? year,
            out IReadOnlyList<OscarNomination> nominations)
        {
            nominations = Array.Empty<OscarNomination>();

            try
            {
                using (var asked = conn.CreateCommand())
                {
                    asked.CommandText = "SELECT 1 FROM oscar_lookups WHERE title = @title AND year = @year LIMIT 1";
                    asked.Parameters.AddWithValue("@title", title);
                    asked.Parameters.AddWithValue("@year", year ?? UnknownYear);

                    if (asked.ExecuteScalar() is null) return false;
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT ceremony, category, nominee, detail, won
FROM oscar_nominations
WHERE title = @title AND year = @year
ORDER BY ceremony, rowid";
                cmd.Parameters.AddWithValue("@title", title);
                cmd.Parameters.AddWithValue("@year", year ?? UnknownYear);

                var found = new List<OscarNomination>();

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    found.Add(new OscarNomination
                    {
                        Ceremony = reader.GetInt32(0),
                        Category = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        Nominee = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        Detail = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        Won = !reader.IsDBNull(4) && reader.GetInt64(4) != 0
                    });
                }

                nominations = found;
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("oscars.log", $"awards cache read failed for {title}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Records an answer, including the answer "none". Written in one transaction with the
        /// nominations replaced wholesale, so a re-ask after the archive's March update cannot
        /// leave last year's rows sitting under this year's.
        /// </summary>
        internal static void WriteCache(
            SqliteConnection conn,
            string title,
            int? year,
            IReadOnlyList<OscarNomination> nominations)
        {
            try
            {
                var stored = year ?? UnknownYear;

                using var tx = conn.BeginTransaction();

                using (var clear = conn.CreateCommand())
                {
                    clear.Transaction = tx;
                    clear.CommandText = "DELETE FROM oscar_nominations WHERE title = @title AND year = @year";
                    clear.Parameters.AddWithValue("@title", title);
                    clear.Parameters.AddWithValue("@year", stored);
                    clear.ExecuteNonQuery();
                }

                if (nominations.Count > 0)
                {
                    using var insert = conn.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText = @"
INSERT INTO oscar_nominations (title, year, ceremony, category, nominee, detail, won)
VALUES (@title, @year, @ceremony, @category, @nominee, @detail, @won)";

                    var titleParam = insert.Parameters.Add("@title", SqliteType.Text);
                    var yearParam = insert.Parameters.Add("@year", SqliteType.Integer);
                    var ceremony = insert.Parameters.Add("@ceremony", SqliteType.Integer);
                    var category = insert.Parameters.Add("@category", SqliteType.Text);
                    var nominee = insert.Parameters.Add("@nominee", SqliteType.Text);
                    var detail = insert.Parameters.Add("@detail", SqliteType.Text);
                    var won = insert.Parameters.Add("@won", SqliteType.Integer);

                    foreach (var nomination in nominations)
                    {
                        titleParam.Value = title;
                        yearParam.Value = stored;
                        ceremony.Value = nomination.Ceremony;
                        category.Value = nomination.Category ?? "";
                        nominee.Value = nomination.Nominee ?? "";
                        detail.Value = nomination.Detail ?? "";
                        won.Value = nomination.Won ? 1 : 0;

                        insert.ExecuteNonQuery();
                    }
                }

                using (var asked = conn.CreateCommand())
                {
                    asked.Transaction = tx;
                    asked.CommandText = @"
INSERT INTO oscar_lookups (title, year, fetched_at)
VALUES (@title, @year, @fetched)
ON CONFLICT(title, year) DO UPDATE SET fetched_at = excluded.fetched_at";
                    asked.Parameters.AddWithValue("@title", title);
                    asked.Parameters.AddWithValue("@year", stored);
                    asked.Parameters.AddWithValue("@fetched", DateTime.UtcNow.ToString("o"));
                    asked.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                // A cache that could not be written costs a repeated request, not a broken screen.
                AppLog.Write("oscars.log", $"awards cache write failed for {title}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_ownsLookup && _lookup is IDisposable disposable) disposable.Dispose();
        }
    }
}
