-- UrDatabase schema. Every statement is IF NOT EXISTS so it can run against an
-- existing library without touching data, and so a fresh install (any OS) gets a
-- usable database on first launch.
--
-- IF NOT EXISTS is enough for a whole new table and useless for a new column on
-- a table that already exists: CREATE TABLE IF NOT EXISTS sees the table, does
-- nothing, and the column never arrives. This file therefore describes the shape
-- a database is created with, and Database.Migrate — which runs immediately
-- after this script — is what gets an older one there with ALTER TABLE. A column
-- added to a table here has to be added there too, or it reaches new installs
-- only and every existing library fails on "no such column".
--
-- The same ordering is why no index here may mention a migrated column: this
-- script runs first, when the column does not exist yet.

CREATE TABLE IF NOT EXISTS movies (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    title       TEXT    NOT NULL,
    year        INTEGER,
    genres      TEXT,
    poster_path TEXT,
    -- Which TMDB film this is. Written by the automatic match and overwritten when somebody
    -- corrects it, so the plot, cast and artwork all describe the same film and a correction is
    -- not re-derived away the next time the film is opened.
    tmdb_id     INTEGER
);

CREATE TABLE IF NOT EXISTS files (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    movie_id   INTEGER REFERENCES movies(id) ON DELETE SET NULL,
    file_path  TEXT NOT NULL,
    size_bytes INTEGER,
    created_at TEXT,
    updated_at TEXT,
    -- When a scan last saw this path on disk, and which scan that was. Without
    -- them a scan can only add: it has no way to tell a file it did not see
    -- from one it never walked past, so a deleted film stays in the catalogue
    -- forever and Play keeps offering it.
    last_seen_at      TEXT,
    last_seen_scan_id INTEGER,
    -- Set when a completed scan walked the folder this path is under and did
    -- not find it. Marked, never deleted: an unmounted drive and a deleted file
    -- look identical from here, and only one of them should cost you a
    -- catalogue. NULL means present as far as the last scan could tell.
    missing_since     TEXT
);

-- Required for the scanner's ON CONFLICT(file_path) upsert.
CREATE UNIQUE INDEX IF NOT EXISTS ux_files_path ON files(file_path);
CREATE INDEX IF NOT EXISTS ix_files_movie ON files(movie_id);

-- One row per scan: what it covered, when, and how it ended.
--
-- The status is what makes the missing marking safe. A scan can be cancelled
-- half way through and deliberately keeps what it catalogued, so "this row was
-- not seen" only means "this file is gone" when the scan that did not see it
-- ran to the end. Anything other than 'completed' concludes nothing.
--
-- Vocabulary: 'running', 'completed', 'cancelled', 'failed'. Not a CHECK
-- constraint — a status this app failed to anticipate should not be a crash in
-- front of somebody trying to scan their films.
CREATE TABLE IF NOT EXISTS scans (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at    TEXT NOT NULL,
    finished_at   TEXT,
    status        TEXT NOT NULL,
    -- JSON arrays. `roots` is what was actually walked; `skipped_roots` is the
    -- configured folders that were not there, which is the difference between
    -- "you deleted those films" and "that drive was unplugged".
    roots         TEXT,
    skipped_roots TEXT,
    inserted      INTEGER NOT NULL DEFAULT 0,
    moved         INTEGER NOT NULL DEFAULT 0,
    updated       INTEGER NOT NULL DEFAULT 0,
    unchanged     INTEGER NOT NULL DEFAULT 0,
    failed        INTEGER NOT NULL DEFAULT 0,
    missing       INTEGER NOT NULL DEFAULT 0
);

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
    -- Cast and crew as the server reported them, one credit per line, in the same
    -- "Name (Role)" and "Job: Name" shapes the TMDB path produces. Named with a suffix
    -- because `cast` is a SQL keyword.
    cast_list        TEXT,
    crew_list        TEXT,
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
