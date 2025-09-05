-- SQLite schema (initial)
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS movies (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    title TEXT,
    year INTEGER,
    original_title TEXT,
    imdb_id TEXT,
    tmdb_id TEXT,
    runtime_minutes INTEGER,
    genres TEXT,
    rating FLOAT,
    votes INTEGER,
    awards TEXT,
    plot TEXT,
    language TEXT,
    country TEXT,
    poster_path TEXT,
    added_at TEXT,
    updated_at TEXT
);

CREATE TABLE IF NOT EXISTS files (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    movie_id INTEGER,
    file_path TEXT NOT NULL,
    size_bytes INTEGER,
    video_codec TEXT,
    audio_codec TEXT,
    resolution TEXT,
    source TEXT,
    created_at TEXT,
    updated_at TEXT,
    FOREIGN KEY (movie_id) REFERENCES movies(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS people (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    tmdb_id TEXT,
    imdb_id TEXT
);

CREATE TABLE IF NOT EXISTS roles (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    movie_id INTEGER NOT NULL,
    person_id INTEGER NOT NULL,
    role_type TEXT NOT NULL, -- 'cast' or 'crew'
    role_name TEXT,
    billing_order INTEGER,
    FOREIGN KEY (movie_id) REFERENCES movies(id) ON DELETE CASCADE,
    FOREIGN KEY (person_id) REFERENCES people(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS watch_folders (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    path TEXT NOT NULL UNIQUE,
    is_active INTEGER DEFAULT 1
);

CREATE VIRTUAL TABLE IF NOT EXISTS movies_fts USING fts5(
    title, original_title, genres, plot, content='movies', content_rowid='id'
);
