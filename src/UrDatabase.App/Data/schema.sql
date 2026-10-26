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
    tmdb_id     INTEGER,
    -- The title the scanner derived from the filename, kept when a corrected TMDB match renames
    -- the film. The scanner resolves what it parses out of a filename, so without this a re-scan
    -- would fail to find the renamed row and insert a second one beside it.
    scan_title  TEXT,
    -- What the library's filters ask about a film: how long it is, how well it is rated out of
    -- ten, and who is in it and who directed it. Filled in from TMDB's record for tmdb_id by the
    -- poster pass, and replaced when somebody corrects the match. Cast and crew are one line per
    -- person in the same shape jellyfin_movies stores them, because they are read back whole.
    runtime_minutes  INTEGER,
    rating           REAL,
    cast_list        TEXT,
    crew_list        TEXT,
    -- When those four were last asked for. Null means never, which is what tells the poster pass a
    -- film still needs the request; a film TMDB knows no runtime for is not asked again forever.
    facts_fetched_at TEXT
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
    missing_since     TEXT,
    -- The episode this file is, when the scan read it as one. A file is a film or an episode and
    -- never both: when this is set, movie_id is NULL. Its index is created by Database.Migrate,
    -- because an older library gains this column there, after this script has run.
    episode_id        INTEGER REFERENCES episodes(id) ON DELETE SET NULL,
    -- 1 when a person linked this file to a film by hand. A scan re-files an episode-shaped
    -- file it once catalogued as a film, and must not undo a person's say-so the same way.
    linked_by_hand    INTEGER NOT NULL DEFAULT 0
);

-- Required for the scanner's ON CONFLICT(file_path) upsert.
CREATE UNIQUE INDEX IF NOT EXISTS ux_files_path ON files(file_path);
CREATE INDEX IF NOT EXISTS ix_files_movie ON files(movie_id);

-- A programme on this disk, made of the episodes a scan found for it. Named by the scan, from
-- the folder or the filename, exactly as a film is: the identity a re-scan resolves against, so
-- title is never rewritten by a lookup. genres and poster_path stay NULL until something
-- identifies the programme.
--
-- tmdb_id is which TMDB programme it is, written by the background pass once the match is
-- confirmed (see LocalSeriesLookup). tmdb_fetched_at is when that programme's description was
-- last written here, and NULL means it still has to be asked for. A corrected identification
-- must never be written by setting tmdb_id alone: the episodes' tmdb_* columns describe the old
-- programme and are marked fetched, so they have to be cleared in the same transaction as the id
-- changes — LocalSeriesLookup.SaveDescriptionAsync and SeriesMatchCorrection.RecordAsync both do.
-- backdrop_path is TMDB's own
-- relative path, sized when it is shown; rating is TMDB's average out of ten; cast_list and
-- crew_list are newline separated lines, as on movies.
--
-- scan_title is the name the scan gave the programme, kept once somebody has chosen its match by
-- hand with Wrong series? and title has taken TMDB's name. NULL until then. The next scan resolves
-- episodes by it, exactly as movies.scan_title does for a film, so the renamed programme does not
-- grow a duplicate (see EpisodeCatalogue.LoadAsync).
CREATE TABLE IF NOT EXISTS series (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    title           TEXT    NOT NULL,
    year            INTEGER,
    genres          TEXT,
    poster_path     TEXT,
    created_at      TEXT,
    tmdb_id         INTEGER,
    overview        TEXT,
    backdrop_path   TEXT,
    rating          REAL,
    imdb_id         TEXT,
    cast_list       TEXT,
    crew_list       TEXT,
    tmdb_fetched_at TEXT,
    scan_title      TEXT
);

-- One episode of a local programme. Files hang off it through files.episode_id, so two prints
-- of one episode are one row here, as two prints of a film are one row in movies. A season
-- may be NULL for a numbering that has none; the unique index folds that NULL to -1, because
-- SQLite treats every NULL as distinct and two copies of an unnumbered episode would otherwise
-- become two rows.
--
-- title is what the filename called it. The tmdb_* columns, overview, runtime_minutes, air_date
-- and still_path are TMDB's description of the episode, fetched a season at a time when the
-- programme is opened; tmdb_fetched_at is set on every episode of a season that was asked about,
-- including one TMDB does not list, so a season is asked about once rather than on every open.
-- TMDB is asked by number, so its description is written only when its name agrees with title
-- (or title is empty), and never for season 0: DVD order and specials number differently.
CREATE TABLE IF NOT EXISTS episodes (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    series_id       INTEGER NOT NULL REFERENCES series(id) ON DELETE CASCADE,
    season_number   INTEGER,
    episode_number  INTEGER NOT NULL,
    title           TEXT,
    tmdb_title      TEXT,
    overview        TEXT,
    runtime_minutes INTEGER,
    air_date        TEXT,
    still_path      TEXT,
    tmdb_fetched_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_episodes_number
    ON episodes(series_id, IFNULL(season_number, -1), episode_number);

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

-- Which IMDb title a TMDB film is, as TMDB said. An empty imdb_id records that TMDB has none,
-- so the question is not asked again. Filled for free by the facts pass, and by the IMDb
-- rating backfill for any film the facts pass described before this table existed.
CREATE TABLE IF NOT EXISTS tmdb_imdb_ids (
    tmdb_id    INTEGER PRIMARY KEY,
    imdb_id    TEXT NOT NULL,
    fetched_at TEXT NOT NULL
);

-- How many OMDb requests this install has made on each UTC day, and whether OMDb has said the
-- day's limit is reached. The free tier allows 1,000 a day; the background rating backfill
-- keeps to a share of that and stops for the day the moment OMDb refuses.
CREATE TABLE IF NOT EXISTS omdb_usage (
    day           TEXT PRIMARY KEY,
    requests      INTEGER NOT NULL DEFAULT 0,
    limit_reached INTEGER NOT NULL DEFAULT 0
);
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
    -- What the server measured about the file: picture size, codecs, and the audio and subtitle
    -- languages it carries, as JSON. Read back whole, for one film, to be printed as a row of
    -- badges — never filtered on and never joined, so a table of streams would buy nothing and
    -- cost a join on the path that has to work with the server switched off.
    media_info       TEXT,
    synced_at        TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_jellyfin_movies_title ON jellyfin_movies(title);

-- The television half of the same server, on exactly the same terms: metadata only, replaced
-- wholesale by each sync, and nothing here is a file.
--
-- A separate table rather than a `kind` column on jellyfin_movies. The two disagree about what
-- is worth storing — a series has no runtime of its own and has two counts a film cannot have —
-- and a shared table would have carried a column that is always null for one of them and a
-- discriminator every single query would then have to remember to filter on.
CREATE TABLE IF NOT EXISTS jellyfin_series (
    item_id          TEXT PRIMARY KEY,
    title            TEXT NOT NULL,
    -- The year the show started. Jellyfin reports one year, not a range.
    year             INTEGER,
    genres           TEXT,
    overview         TEXT,
    community_rating REAL,
    imdb_id          TEXT,
    tmdb_id          TEXT,
    cast_list        TEXT,
    crew_list        TEXT,
    image_tag        TEXT,
    -- How many seasons and episodes the server counted, or NULL when it did not say. Never
    -- defaulted to zero: "no seasons" and "nobody counted" are different facts, and only one of
    -- them belongs on a card.
    season_count     INTEGER,
    episode_count    INTEGER,
    synced_at        TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_jellyfin_series_title ON jellyfin_series(title);

-- Seasons and episodes, written when a series is opened rather than by a sync. A library of two
-- hundred shows is thousands of episodes, and pulling them all during a sync would make the sync
-- unusable to fill in a screen almost nobody has open.
--
-- Cached anyway, once fetched, for the same reason the films are: a laptop away from the house
-- can still read what it has already seen. Each series' rows are replaced wholesale when it is
-- reopened, so an episode deleted upstairs stops being offered.
CREATE TABLE IF NOT EXISTS jellyfin_seasons (
    item_id       TEXT PRIMARY KEY,
    series_id     TEXT NOT NULL,
    name          TEXT NOT NULL,
    -- NULL is ordinary here: some servers number specials 0, and some send no number at all.
    season_number INTEGER,
    image_tag     TEXT,
    episode_count INTEGER,
    synced_at     TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_jellyfin_seasons_series ON jellyfin_seasons(series_id);

CREATE TABLE IF NOT EXISTS jellyfin_episodes (
    item_id          TEXT PRIMARY KEY,
    series_id        TEXT NOT NULL,
    -- Which season folder it is in. Empty when the server did not say, which is why episodes are
    -- grouped by season_number rather than by this.
    season_id        TEXT,
    name             TEXT NOT NULL,
    season_number    INTEGER,
    episode_number   INTEGER,
    overview         TEXT,
    runtime_minutes  INTEGER,
    community_rating REAL,
    image_tag        TEXT,
    synced_at        TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_jellyfin_episodes_series ON jellyfin_episodes(series_id);
-- Academy Award nominations from the UrActor API, cached because the archive changes once a
-- year, in March, and the key is limited to 60 requests a minute shared across everything this
-- app does. Every film opened would otherwise be a request, and browsing a library would spend
-- the allowance in under a minute.
--
-- Two tables rather than one. This one records that a film was looked up at all, so "asked
-- already, the Academy never nominated it" is remembered — which is the answer for almost every
-- film in almost every library, and the one it would be most wasteful to ask twice. The
-- nominations table below holds the answers when there were any, and a film with a row here and
-- none there is a film that has been asked about and has nothing.
--
-- Keyed on the title and the release year together, because a title alone is not a film: there
-- are four called "A Star Is Born" and three of them were nominated.
CREATE TABLE IF NOT EXISTS oscar_lookups (
    title      TEXT    NOT NULL,
    -- The film's release year, or 0 when the catalogue does not know it. Not NULL: this is half
    -- of a primary key, and SQLite would let every unknown-year film be its own row.
    year       INTEGER NOT NULL,
    fetched_at TEXT    NOT NULL,
    PRIMARY KEY (title, year)
);

CREATE TABLE IF NOT EXISTS oscar_nominations (
    title    TEXT    NOT NULL,
    year     INTEGER NOT NULL,
    -- The year the ceremony was held, which is normally the year after the film came out. Kept
    -- because it is what the API is keyed on and what a user reading "1976" would expect to see.
    ceremony INTEGER NOT NULL,
    category TEXT    NOT NULL,
    -- Who or what was nominated, and the context. For Best Picture the film is the nominee and
    -- the producers are the detail; for the craft awards it is the other way round.
    nominee  TEXT    NOT NULL,
    detail   TEXT,
    won      INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_oscar_nominations_film ON oscar_nominations(title, year);

-- Where the server says the viewer had got to in each part-watched film and episode, as of the
-- last successful sync. What the Continue watching row is built from.
--
-- A film is a position and nothing else: its title, year and artwork are already in
-- jellyfin_movies, and a second copy of them here would give the row its own idea of what a film
-- is called. An entry whose item_id names nothing in that table — a film in a library this app was
-- never pointed at — has no card to land on and is dropped when the row is built.
--
-- An episode has to carry more, because nothing caches episodes until a series is opened: there is
-- no first copy of its name for this to be a second copy of. It brings the programme, the season,
-- the number and its own title, which is what a card has to say. None of it can go stale, because
-- the whole table is replaced by each sync and so always says exactly what the server last said.
-- Its artwork is still not here — that is the series card's poster, found through series_id.
--
-- Replaced wholesale by each sync, and only after the server has answered, so a sync attempted
-- away from home leaves the previous row intact rather than emptying it.
CREATE TABLE IF NOT EXISTS jellyfin_resume (
    item_id           TEXT PRIMARY KEY,
    -- 100-nanosecond ticks, which is the unit Jellyfin counts in. Never seconds: the app talks to
    -- a player that answers in seconds, and one conversion in the wrong direction would report a
    -- two hour film as seven seconds in.
    position_ticks    INTEGER NOT NULL,
    runtime_ticks     INTEGER,
    -- The server's own percentage. Preferred to dividing the two columns above, because the server
    -- knows the length of the file it is serving and a cached runtime can describe a different cut.
    played_percentage REAL,
    -- The server's ordering, most recently watched first, kept because it is a real answer.
    sort_order        INTEGER NOT NULL,
    -- 'Movie' or 'Episode', in Jellyfin's own vocabulary. Without it the row would have to look
    -- every id up in both caches, or guess — and a wrong guess renders an episode as a film with
    -- an inexplicable name. Added by Database.Migrate as well, so an existing library gets it.
    item_type         TEXT,
    -- The four things an episode card says, and null on a film.
    series_id         TEXT,
    series_name       TEXT,
    season_number     INTEGER,
    episode_number    INTEGER,
    name              TEXT,
    synced_at         TEXT NOT NULL
);

-- Items the owner has taken out of their own Continue watching row. Local, and deliberately: it is
-- this app's opinion about this app's first shelf, and nothing here is ever sent to the server.
-- Marking something unplayed on Jellyfin would change it for every client in the house, which is a
-- different act with a different blast radius.
--
-- A table of its own rather than a column on jellyfin_resume, because that one is deleted and
-- rewritten by every sync: a dismissal stored there would evaporate the next time the server was
-- asked anything.
--
-- position_ticks is what the dismissal was about, and what makes it expire. The item comes back
-- the moment the server reports a different position for it, on the reasoning that somebody who
-- has watched more of a thing has plainly not abandoned it. That also keeps this table from
-- growing into a blacklist nobody can see: a stale row is pruned by the next sync.
CREATE TABLE IF NOT EXISTS jellyfin_resume_dismissals (
    item_id        TEXT PRIMARY KEY,
    position_ticks INTEGER NOT NULL,
    dismissed_at   TEXT NOT NULL
);

-- Films the owner has said they want to watch. Keyed on the identity a card carries ('local:12'
-- for a catalogued film, 'jellyfin:<item id>' for one only the server holds) rather than on
-- movies.id, because a server film has no row in movies to point at. A table of its own, so no
-- scan or sync can rewrite it, and nothing here is sent to the server. A new table rather than a
-- new column, so CREATE ... IF NOT EXISTS is the whole of the migration.
CREATE TABLE IF NOT EXISTS watchlist (
    item_key TEXT PRIMARY KEY COLLATE NOCASE,
    added_at TEXT NOT NULL
);

-- Every disk the app has seen plugged in, so that one which dies can be missed: the operating
-- system does not report a dead disk as missing, it just stops listing it. drive_id is the volume
-- serial number where there is one ('serial:1A2B-3C4D', Windows) and the label and capacity where
-- there is not ('volume:Films|4000787030016'), never the drive letter, which Windows hands out
-- again to whatever is plugged in next. last_root is where to say it was. A row is deleted only
-- when the owner forgets the disk. A new table, so CREATE ... IF NOT EXISTS is the migration.
CREATE TABLE IF NOT EXISTS known_drives (
    drive_id      TEXT PRIMARY KEY,
    label         TEXT,
    last_root     TEXT NOT NULL,
    total_bytes   INTEGER,
    first_seen_at TEXT NOT NULL,
    last_seen_at  TEXT NOT NULL
);

-- Problems the owner has reported with a copy of a film: bad audio, a low resolution, a squashed
-- aspect ratio. A person's judgement after watching it, which nothing in the file can measure, so it
-- is kept apart from the media badges that are recomputed every time a film is opened.
--
-- Keyed on the library's own card identity ('local:<movies.id>' or 'jellyfin:<item id>') rather
-- than on movies.id, because a server film has no row there, and kept out of jellyfin_movies because
-- every sync deletes and rewrites that table. problems is a comma-separated list of stable codes.
CREATE TABLE IF NOT EXISTS film_problems (
    film_key   TEXT PRIMARY KEY,
    problems   TEXT NOT NULL,
    note       TEXT,
    flagged_at TEXT NOT NULL
);

-- The owner's own opinion of a film: a rating out of ten and a free-text review. One row per film.
--
-- Keyed on film_key rather than movies(id), and not foreign-keyed to it, because a rating has to
-- outlive the row it was made against: a rescan that discards a renamed file deletes that row, and
-- a film that is only on the Jellyfin server has no row in movies at all. The key is 'tmdb:<id>'
-- when the film has been identified and 'title:<normalised title>|<year>' until then — see
-- FilmKey — and a title-keyed row is moved onto the TMDB key once the film is identified.
--
-- Shaped like UrActor's own Reviews so a later sync has nothing to convert: the same ten-point
-- scale, and the TMDB id alongside. Nothing here is sent anywhere today.
CREATE TABLE IF NOT EXISTS film_ratings (
    film_key   TEXT PRIMARY KEY,
    tmdb_id    INTEGER,
    title      TEXT NOT NULL,
    year       INTEGER,
    -- NULL when the owner wrote a review but gave no stars, which is a real thing to do.
    rating     INTEGER CHECK (rating IS NULL OR rating BETWEEN 1 AND 10),
    review     TEXT,
    updated_at TEXT NOT NULL
);

-- Each time the owner watched a film: the day, who with, and a note. As many rows per film as
-- there were viewings, which is what makes a rewatch countable.
--
-- watched_on is a local calendar date as yyyy-MM-dd, the same shape UrActor's Calendar uses, not
-- a timestamp: "the evening of the 3rd" is the fact a person remembers, and a UTC instant would
-- move it to the 4th for anyone east of Greenwich who watched late. companions is a JSON array of
-- names as typed — plain people, not accounts.
CREATE TABLE IF NOT EXISTS film_viewings (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    film_key   TEXT NOT NULL,
    tmdb_id    INTEGER,
    title      TEXT NOT NULL,
    year       INTEGER,
    watched_on TEXT NOT NULL,
    companions TEXT,
    note       TEXT,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_film_viewings_key ON film_viewings(film_key);

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

-- The same index over local programmes, so a search finds a series by name as it finds a film.
CREATE VIRTUAL TABLE IF NOT EXISTS series_fts USING fts5(
    title,
    genres,
    content='series',
    content_rowid='id'
);

CREATE TRIGGER IF NOT EXISTS series_ai AFTER INSERT ON series BEGIN
    INSERT INTO series_fts(rowid, title, genres) VALUES (new.id, new.title, new.genres);
END;

CREATE TRIGGER IF NOT EXISTS series_ad AFTER DELETE ON series BEGIN
    INSERT INTO series_fts(series_fts, rowid, title, genres) VALUES ('delete', old.id, old.title, old.genres);
END;

CREATE TRIGGER IF NOT EXISTS series_au AFTER UPDATE ON series BEGIN
    INSERT INTO series_fts(series_fts, rowid, title, genres) VALUES ('delete', old.id, old.title, old.genres);
    INSERT INTO series_fts(rowid, title, genres) VALUES (new.id, new.title, new.genres);
END;
