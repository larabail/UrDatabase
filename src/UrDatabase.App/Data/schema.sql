-- UrDatabase schema. Every statement is IF NOT EXISTS so it can run against an
-- existing library without touching data, and so a fresh install (any OS) gets a
-- usable database on first launch.

CREATE TABLE IF NOT EXISTS movies (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    title       TEXT    NOT NULL,
    year        INTEGER,
    genres      TEXT,
    poster_path TEXT
);

CREATE TABLE IF NOT EXISTS files (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    movie_id   INTEGER REFERENCES movies(id) ON DELETE SET NULL,
    file_path  TEXT NOT NULL,
    size_bytes INTEGER,
    created_at TEXT,
    updated_at TEXT
);

-- Required for the scanner's ON CONFLICT(file_path) upsert.
CREATE UNIQUE INDEX IF NOT EXISTS ux_files_path ON files(file_path);
CREATE INDEX IF NOT EXISTS ix_files_movie ON files(movie_id);

-- Cached IMDb ratings from OMDb. The free tier allows 1,000 lookups a day, so a row is
-- written even when OMDb has no rating (rating IS NULL) to stop the app asking again.
CREATE TABLE IF NOT EXISTS imdb_ratings (
    imdb_id    TEXT PRIMARY KEY,
    movie_id   INTEGER REFERENCES movies(id) ON DELETE SET NULL,
    rating     REAL,
    fetched_at TEXT NOT NULL,
    source     TEXT NOT NULL DEFAULT 'omdb'
);

-- The movie library of a Jellyfin server, as of the last successful sync. Metadata only:
-- nothing here is a file. Playing one of these streams from the server, unless it has been
-- downloaded, in which case the copy is an ordinary row in movies and files like any other.
--
-- Cached so the window can open instantly and stay readable on a laptop that is nowhere near
-- the server. Replaced wholesale by each sync, so a film removed upstream disappears here too.
CREATE TABLE IF NOT EXISTS jellyfin_movies (
    item_id          TEXT PRIMARY KEY,
    title            TEXT NOT NULL,
    year             INTEGER,
    genres           TEXT,
    overview         TEXT,
    runtime_minutes  INTEGER,
    -- Jellyfin's own community rating, which is not an IMDb rating and is never shown as one.
    community_rating REAL,
    imdb_id          TEXT,
    tmdb_id          TEXT,
    image_tag        TEXT,
    synced_at        TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_jellyfin_movies_title ON jellyfin_movies(title);

-- Full text search over the catalogue, kept in sync with movies by triggers.
CREATE VIRTUAL TABLE IF NOT EXISTS movies_fts USING fts5(
    title,
    genres,
    content='movies',
    content_rowid='id'
);

CREATE TRIGGER IF NOT EXISTS movies_ai AFTER INSERT ON movies BEGIN
    INSERT INTO movies_fts(rowid, title, genres) VALUES (new.id, new.title, new.genres);
END;

CREATE TRIGGER IF NOT EXISTS movies_ad AFTER DELETE ON movies BEGIN
    INSERT INTO movies_fts(movies_fts, rowid, title, genres) VALUES ('delete', old.id, old.title, old.genres);
END;

CREATE TRIGGER IF NOT EXISTS movies_au AFTER UPDATE ON movies BEGIN
    INSERT INTO movies_fts(movies_fts, rowid, title, genres) VALUES ('delete', old.id, old.title, old.genres);
    INSERT INTO movies_fts(rowid, title, genres) VALUES (new.id, new.title, new.genres);
END;
