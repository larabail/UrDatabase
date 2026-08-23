using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// The server's library, remembered locally. Without this the window would be empty until the
    /// server answered, and would stay empty for good on a laptop that is out of the house — which
    /// is precisely when someone wants to look at what they own.
    ///
    /// Metadata only. No film is ever copied here.
    /// </summary>
    public static class JellyfinCache
    {
        /// <summary>
        /// Replaces the cached library with what the server just reported, in one transaction, so
        /// a sync that fails part way leaves the previous library intact rather than a half of it.
        /// Rows the server no longer lists are dropped: a film deleted upstairs should not linger
        /// as an item that cannot play.
        /// </summary>
        public static int Replace(SqliteConnection conn, IEnumerable<JellyfinMovie> movies)
        {
            using var tx = conn.BeginTransaction();
            var written = ReplaceMovies(conn, tx, movies);
            tx.Commit();

            return written;
        }

        /// <summary>
        /// Replaces both halves of the cached library — the films and the television — in a single
        /// transaction.
        /// </summary>
        /// <remarks>
        /// One transaction and not two. Two would mean a sync that failed between them left the
        /// films from this minute beside the series from last week, which is a state no code here
        /// would ever expect to read and nothing on screen would admit to.
        ///
        /// Seasons and episodes are deliberately untouched. They are written when a series is
        /// opened, and a sync has not asked the server about them, so clearing them here would
        /// throw away a cache the server never contradicted — and would empty the episode list of
        /// a show on a laptop that is nowhere near the server.
        /// </remarks>
        public static int Replace(SqliteConnection conn, JellyfinLibraryContents contents)
        {
            if (contents is null) throw new ArgumentNullException(nameof(contents));

            using var tx = conn.BeginTransaction();

            var written = ReplaceMovies(conn, tx, contents.Movies) + ReplaceSeries(conn, tx, contents.Series);

            tx.Commit();
            return written;
        }

        private static int ReplaceMovies(SqliteConnection conn, SqliteTransaction tx, IEnumerable<JellyfinMovie>? movies)
        {
            var list = movies?.Where(m => !string.IsNullOrWhiteSpace(m.ItemId)).ToList() ?? new List<JellyfinMovie>();

            using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM jellyfin_movies";
                clear.ExecuteNonQuery();
            }

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = @"
INSERT INTO jellyfin_movies
    (item_id, title, year, genres, overview, runtime_minutes, community_rating, imdb_id, tmdb_id, cast_list, crew_list, image_tag, synced_at)
VALUES
    (@item, @title, @year, @genres, @overview, @runtime, @rating, @imdb, @tmdb, @cast, @crew, @tag, @synced)
ON CONFLICT(item_id) DO UPDATE SET
    title            = excluded.title,
    year             = excluded.year,
    genres           = excluded.genres,
    overview         = excluded.overview,
    runtime_minutes  = excluded.runtime_minutes,
    community_rating = excluded.community_rating,
    imdb_id          = excluded.imdb_id,
    tmdb_id          = excluded.tmdb_id,
    cast_list        = excluded.cast_list,
    crew_list        = excluded.crew_list,
    image_tag        = excluded.image_tag,
    synced_at        = excluded.synced_at;";

                var item = insert.Parameters.Add("@item", SqliteType.Text);
                var title = insert.Parameters.Add("@title", SqliteType.Text);
                var year = insert.Parameters.Add("@year", SqliteType.Integer);
                var genres = insert.Parameters.Add("@genres", SqliteType.Text);
                var overview = insert.Parameters.Add("@overview", SqliteType.Text);
                var runtime = insert.Parameters.Add("@runtime", SqliteType.Integer);
                var rating = insert.Parameters.Add("@rating", SqliteType.Real);
                var imdb = insert.Parameters.Add("@imdb", SqliteType.Text);
                var tmdb = insert.Parameters.Add("@tmdb", SqliteType.Text);
                var cast = insert.Parameters.Add("@cast", SqliteType.Text);
                var crew = insert.Parameters.Add("@crew", SqliteType.Text);
                var tag = insert.Parameters.Add("@tag", SqliteType.Text);
                var synced = insert.Parameters.Add("@synced", SqliteType.Text);

                var now = DateTime.UtcNow.ToString("o");

                foreach (var movie in list)
                {
                    item.Value = movie.ItemId;
                    title.Value = movie.Title ?? "";
                    year.Value = (object?)movie.Year ?? DBNull.Value;
                    genres.Value = movie.Genres ?? "";
                    overview.Value = movie.Overview ?? "";
                    runtime.Value = (object?)movie.RuntimeMinutes ?? DBNull.Value;
                    rating.Value = (object?)movie.CommunityRating ?? DBNull.Value;
                    imdb.Value = (object?)movie.ImdbId ?? DBNull.Value;
                    tmdb.Value = (object?)movie.TmdbId ?? DBNull.Value;
                    cast.Value = JoinCredits(movie.Cast);
                    crew.Value = JoinCredits(movie.Crew);
                    tag.Value = (object?)movie.ImageTag ?? DBNull.Value;
                    synced.Value = now;

                    insert.ExecuteNonQuery();
                }
            }

            return list.Count;
        }

        private static int ReplaceSeries(SqliteConnection conn, SqliteTransaction tx, IEnumerable<JellyfinSeries>? series)
        {
            var list = series?.Where(s => !string.IsNullOrWhiteSpace(s.ItemId)).ToList() ?? new List<JellyfinSeries>();

            using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM jellyfin_series";
                clear.ExecuteNonQuery();
            }

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = @"
INSERT INTO jellyfin_series
    (item_id, title, year, genres, overview, community_rating, imdb_id, tmdb_id, cast_list, crew_list, image_tag, season_count, episode_count, synced_at)
VALUES
    (@item, @title, @year, @genres, @overview, @rating, @imdb, @tmdb, @cast, @crew, @tag, @seasons, @episodes, @synced)
ON CONFLICT(item_id) DO UPDATE SET
    title            = excluded.title,
    year             = excluded.year,
    genres           = excluded.genres,
    overview         = excluded.overview,
    community_rating = excluded.community_rating,
    imdb_id          = excluded.imdb_id,
    tmdb_id          = excluded.tmdb_id,
    cast_list        = excluded.cast_list,
    crew_list        = excluded.crew_list,
    image_tag        = excluded.image_tag,
    season_count     = excluded.season_count,
    episode_count    = excluded.episode_count,
    synced_at        = excluded.synced_at;";

                var item = insert.Parameters.Add("@item", SqliteType.Text);
                var title = insert.Parameters.Add("@title", SqliteType.Text);
                var year = insert.Parameters.Add("@year", SqliteType.Integer);
                var genres = insert.Parameters.Add("@genres", SqliteType.Text);
                var overview = insert.Parameters.Add("@overview", SqliteType.Text);
                var rating = insert.Parameters.Add("@rating", SqliteType.Real);
                var imdb = insert.Parameters.Add("@imdb", SqliteType.Text);
                var tmdb = insert.Parameters.Add("@tmdb", SqliteType.Text);
                var cast = insert.Parameters.Add("@cast", SqliteType.Text);
                var crew = insert.Parameters.Add("@crew", SqliteType.Text);
                var tag = insert.Parameters.Add("@tag", SqliteType.Text);
                var seasons = insert.Parameters.Add("@seasons", SqliteType.Integer);
                var episodes = insert.Parameters.Add("@episodes", SqliteType.Integer);
                var synced = insert.Parameters.Add("@synced", SqliteType.Text);

                var now = DateTime.UtcNow.ToString("o");

                foreach (var show in list)
                {
                    item.Value = show.ItemId;
                    title.Value = show.Title ?? "";
                    year.Value = (object?)show.Year ?? DBNull.Value;
                    genres.Value = show.Genres ?? "";
                    overview.Value = show.Overview ?? "";
                    rating.Value = (object?)show.CommunityRating ?? DBNull.Value;
                    imdb.Value = (object?)show.ImdbId ?? DBNull.Value;
                    tmdb.Value = (object?)show.TmdbId ?? DBNull.Value;
                    cast.Value = JoinCredits(show.Cast);
                    crew.Value = JoinCredits(show.Crew);
                    tag.Value = (object?)show.ImageTag ?? DBNull.Value;
                    seasons.Value = (object?)show.SeasonCount ?? DBNull.Value;
                    episodes.Value = (object?)show.EpisodeCount ?? DBNull.Value;
                    synced.Value = now;

                    insert.ExecuteNonQuery();
                }
            }

            return list.Count;
        }

        /// <summary>
        /// What the last sync saw of the server's television, newest first, on the same terms as
        /// <see cref="Load"/>.
        /// </summary>
        public static IReadOnlyList<JellyfinSeries> LoadSeries(SqliteConnection conn)
        {
            var series = new List<JellyfinSeries>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT item_id, title, year, genres, overview, community_rating, imdb_id, tmdb_id, cast_list, crew_list, image_tag, season_count, episode_count
FROM jellyfin_series
ORDER BY COALESCE(year, 0) DESC, title";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                series.Add(new JellyfinSeries
                {
                    ItemId = reader.GetString(0),
                    Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Year = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    Genres = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Overview = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    CommunityRating = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    ImdbId = reader.IsDBNull(6) ? null : reader.GetString(6),
                    TmdbId = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Cast = SplitCredits(reader.IsDBNull(8) ? null : reader.GetString(8)),
                    Crew = SplitCredits(reader.IsDBNull(9) ? null : reader.GetString(9)),
                    ImageTag = reader.IsDBNull(10) ? null : reader.GetString(10),
                    SeasonCount = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    EpisodeCount = reader.IsDBNull(12) ? null : reader.GetInt32(12)
                });
            }

            return series;
        }

        /// <summary>
        /// Records what one series is made of, replacing whatever was cached for it before.
        /// </summary>
        /// <remarks>
        /// Scoped to a single series, and that is the point: this is written when a show is opened,
        /// so it must not touch the eleven other shows whose episodes are already cached. The
        /// replacement is wholesale within that scope for the same reason the film cache is —
        /// an episode deleted upstairs should stop being offered rather than linger as something
        /// that cannot play.
        /// </remarks>
        public static void ReplaceEpisodes(
            SqliteConnection conn,
            string seriesId,
            IEnumerable<JellyfinSeason>? seasons,
            IEnumerable<JellyfinEpisode>? episodes)
        {
            if (string.IsNullOrWhiteSpace(seriesId))
                throw new ArgumentException("A series id is required.", nameof(seriesId));

            var seasonList = seasons?.Where(s => !string.IsNullOrWhiteSpace(s.ItemId)).ToList() ?? new List<JellyfinSeason>();
            var episodeList = episodes?.Where(e => !string.IsNullOrWhiteSpace(e.ItemId)).ToList() ?? new List<JellyfinEpisode>();

            var id = seriesId.Trim();
            var now = DateTime.UtcNow.ToString("o");

            using var tx = conn.BeginTransaction();

            foreach (var table in new[] { "jellyfin_seasons", "jellyfin_episodes" })
            {
                using var clear = conn.CreateCommand();
                clear.Transaction = tx;
                // A compile-time constant from the array above, never user input.
                clear.CommandText = $"DELETE FROM {table} WHERE series_id = @series";
                clear.Parameters.AddWithValue("@series", id);
                clear.ExecuteNonQuery();
            }

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = @"
INSERT INTO jellyfin_seasons (item_id, series_id, name, season_number, image_tag, episode_count, synced_at)
VALUES (@item, @series, @name, @number, @tag, @episodes, @synced)
ON CONFLICT(item_id) DO UPDATE SET
    series_id     = excluded.series_id,
    name          = excluded.name,
    season_number = excluded.season_number,
    image_tag     = excluded.image_tag,
    episode_count = excluded.episode_count,
    synced_at     = excluded.synced_at;";

                var item = insert.Parameters.Add("@item", SqliteType.Text);
                var series = insert.Parameters.Add("@series", SqliteType.Text);
                var name = insert.Parameters.Add("@name", SqliteType.Text);
                var number = insert.Parameters.Add("@number", SqliteType.Integer);
                var tag = insert.Parameters.Add("@tag", SqliteType.Text);
                var episodeCount = insert.Parameters.Add("@episodes", SqliteType.Integer);
                var synced = insert.Parameters.Add("@synced", SqliteType.Text);

                foreach (var season in seasonList)
                {
                    item.Value = season.ItemId;
                    series.Value = id;
                    name.Value = season.Name ?? "";
                    number.Value = (object?)season.Number ?? DBNull.Value;
                    tag.Value = (object?)season.ImageTag ?? DBNull.Value;
                    episodeCount.Value = (object?)season.EpisodeCount ?? DBNull.Value;
                    synced.Value = now;

                    insert.ExecuteNonQuery();
                }
            }

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = @"
INSERT INTO jellyfin_episodes
    (item_id, series_id, season_id, name, season_number, episode_number, overview, runtime_minutes, community_rating, image_tag, synced_at)
VALUES
    (@item, @series, @season, @name, @seasonNumber, @number, @overview, @runtime, @rating, @tag, @synced)
ON CONFLICT(item_id) DO UPDATE SET
    series_id        = excluded.series_id,
    season_id        = excluded.season_id,
    name             = excluded.name,
    season_number    = excluded.season_number,
    episode_number   = excluded.episode_number,
    overview         = excluded.overview,
    runtime_minutes  = excluded.runtime_minutes,
    community_rating = excluded.community_rating,
    image_tag        = excluded.image_tag,
    synced_at        = excluded.synced_at;";

                var item = insert.Parameters.Add("@item", SqliteType.Text);
                var series = insert.Parameters.Add("@series", SqliteType.Text);
                var season = insert.Parameters.Add("@season", SqliteType.Text);
                var name = insert.Parameters.Add("@name", SqliteType.Text);
                var seasonNumber = insert.Parameters.Add("@seasonNumber", SqliteType.Integer);
                var number = insert.Parameters.Add("@number", SqliteType.Integer);
                var overview = insert.Parameters.Add("@overview", SqliteType.Text);
                var runtime = insert.Parameters.Add("@runtime", SqliteType.Integer);
                var rating = insert.Parameters.Add("@rating", SqliteType.Real);
                var tag = insert.Parameters.Add("@tag", SqliteType.Text);
                var synced = insert.Parameters.Add("@synced", SqliteType.Text);

                foreach (var episode in episodeList)
                {
                    item.Value = episode.ItemId;
                    series.Value = id;
                    season.Value = episode.SeasonId ?? "";
                    name.Value = episode.Name ?? "";
                    seasonNumber.Value = (object?)episode.SeasonNumber ?? DBNull.Value;
                    number.Value = (object?)episode.Number ?? DBNull.Value;
                    overview.Value = episode.Overview ?? "";
                    runtime.Value = (object?)episode.RuntimeMinutes ?? DBNull.Value;
                    rating.Value = (object?)episode.CommunityRating ?? DBNull.Value;
                    tag.Value = (object?)episode.ImageTag ?? DBNull.Value;
                    synced.Value = now;

                    insert.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }

        /// <summary>The seasons cached for one series, in the order the server listed them.</summary>
        public static IReadOnlyList<JellyfinSeason> LoadSeasons(SqliteConnection conn, string seriesId)
        {
            var seasons = new List<JellyfinSeason>();
            if (string.IsNullOrWhiteSpace(seriesId)) return seasons;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT item_id, series_id, name, season_number, image_tag, episode_count
FROM jellyfin_seasons
WHERE series_id = @series
ORDER BY COALESCE(season_number, 9999), name";
            cmd.Parameters.AddWithValue("@series", seriesId.Trim());

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                seasons.Add(new JellyfinSeason
                {
                    ItemId = reader.GetString(0),
                    SeriesId = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Name = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Number = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    ImageTag = reader.IsDBNull(4) ? null : reader.GetString(4),
                    EpisodeCount = reader.IsDBNull(5) ? null : reader.GetInt32(5)
                });
            }

            return seasons;
        }

        /// <summary>The episodes cached for one series, in broadcast order.</summary>
        public static IReadOnlyList<JellyfinEpisode> LoadEpisodes(SqliteConnection conn, string seriesId)
        {
            var episodes = new List<JellyfinEpisode>();
            if (string.IsNullOrWhiteSpace(seriesId)) return episodes;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT item_id, series_id, season_id, name, season_number, episode_number, overview, runtime_minutes, community_rating, image_tag
FROM jellyfin_episodes
WHERE series_id = @series
ORDER BY COALESCE(season_number, 9999), COALESCE(episode_number, 9999), name";
            cmd.Parameters.AddWithValue("@series", seriesId.Trim());

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                episodes.Add(new JellyfinEpisode
                {
                    ItemId = reader.GetString(0),
                    SeriesId = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    SeasonId = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Name = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    SeasonNumber = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    Number = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    Overview = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    RuntimeMinutes = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    CommunityRating = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                    ImageTag = reader.IsDBNull(9) ? null : reader.GetString(9)
                });
            }

            return episodes;
        }

        /// <summary>What the last sync saw, newest first, exactly as the grouped view wants it.</summary>
        public static IReadOnlyList<JellyfinMovie> Load(SqliteConnection conn)
        {
            var movies = new List<JellyfinMovie>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT item_id, title, year, genres, overview, runtime_minutes, community_rating, imdb_id, tmdb_id, cast_list, crew_list, image_tag
FROM jellyfin_movies
ORDER BY COALESCE(year, 0) DESC, title";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                movies.Add(new JellyfinMovie
                {
                    ItemId = reader.GetString(0),
                    Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Year = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    Genres = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Overview = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    RuntimeMinutes = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    CommunityRating = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    ImdbId = reader.IsDBNull(7) ? null : reader.GetString(7),
                    TmdbId = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Cast = SplitCredits(reader.IsDBNull(9) ? null : reader.GetString(9)),
                    Crew = SplitCredits(reader.IsDBNull(10) ? null : reader.GetString(10)),
                    ImageTag = reader.IsDBNull(11) ? null : reader.GetString(11)
                });
            }

            return movies;
        }

        /// <summary>
        /// Credits are stored one per line. They are only ever read back whole, for one film, to
        /// be printed, so a table of people would buy nothing and cost a join.
        /// </summary>
        internal static string JoinCredits(IEnumerable<string>? credits)
            => credits is null
                ? ""
                : string.Join("\n", credits.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()));

        internal static List<string> SplitCredits(string? stored)
            => string.IsNullOrWhiteSpace(stored)
                ? new List<string>()
                : stored.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(c => c.Trim())
                        .Where(c => c.Length > 0)
                        .ToList();

        /// <summary>
        /// When the cache was last written, or null when nothing has ever synced. Read from the
        /// films and the series together, because a server holding only television has synced
        /// perfectly well and would otherwise report never having synced at all.
        /// </summary>
        public static DateTime? LastSyncedUtc(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT MAX(synced_at) FROM (" +
                "SELECT synced_at FROM jellyfin_movies UNION ALL SELECT synced_at FROM jellyfin_series)";

            var value = cmd.ExecuteScalar();
            if (value is not string text || string.IsNullOrWhiteSpace(text)) return null;

            return DateTime.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : null;
        }

        /// <summary>
        /// Forgets the server's library entirely, television included. Used when Jellyfin is
        /// switched off in config, where leaving the episodes of shows the app will no longer
        /// admit to would be keeping a cache of something the user has just said they do not have.
        /// </summary>
        public static void Clear(SqliteConnection conn)
        {
            using var tx = conn.BeginTransaction();

            foreach (var table in new[] { "jellyfin_movies", "jellyfin_series", "jellyfin_seasons", "jellyfin_episodes" })
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                // A compile-time constant from the array above, never user input.
                cmd.CommandText = $"DELETE FROM {table}";
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }
}
