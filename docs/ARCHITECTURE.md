# Architecture

## Overview
- **Avalonia UI 11 on .NET 8** — one cross-platform project that builds and runs on both
  Windows and macOS. There are no per-OS source folders and no Windows-only target framework.
- **SQLite** single-file database for offline-first persistence
- **Dapper** for data access (simple and fast)
- **TMDB** is the only source of metadata and artwork. **OMDb** supplies the IMDb rating shown
  in the details window, and nothing else.

## Projects

| Path | Purpose |
| --- | --- |
| `src/UrDatabase.App/UrDatabase.App.csproj` | The application. `net8.0`, `WinExe`. |
| `tests/UrDatabase.Tests/UrDatabase.Tests.csproj` | xUnit tests. `net8.0`. |
| `Directory.Build.props` | Holds the single `<Version>` property that CI reads. |

## UI layer

Avalonia replaces WPF. The differences that mattered during the port:

- XAML files are `.axaml` and use the `https://github.com/avaloniaui` namespace.
- `Visibility` becomes `IsVisible`.
- WPF keyed styles and `Style.Triggers` become selector-based styles and pseudo-classes
  (`ToggleButton.chip:checked`). Because the Fluent theme paints a control's background inside
  its template, the checked chip colour is applied through a `/template/` selector.
- Mouse events become pointer events, and a panel needs an explicit `Background` to be
  hit-testable.
- Avalonia will not convert a string path or URL into an image, so `ImageLoader` fetches and
  caches bitmaps and the views assign `Image.Source` themselves.
- Avalonia has no `MessageBox`, so `Views/MessageBoxWindow` stands in for it.
- `Program.cs` is an explicit entry point; WPF generated one from `App.xaml`.
- Every colour, face and metric lives in `Styles/Tokens.axaml`, merged into the application's
  resources, with the shared control styles in `Styles/Theme.axaml`. Windows do not declare
  their own brushes: three of them each used to carry a private copy of `#EAEAEA`, and the
  copies had already drifted apart.

| View | Role |
| --- | --- |
| `Views/SetupWindow` | First-run setup, and the Settings screen thereafter: watch folders, a Jellyfin server, API keys. |
| `Views/MainWindow` | Search, the genre row, the grouped/flat/single-genre poster panels, and the empty library. Hosts the details screen. |
| `Views/MovieDetailsView` | Backdrop, poster, facts, cast and crew, play and link actions. A control, not a window: it fills `MainWindow` so a 16:9 backdrop gets the whole window instead of a third of a dialog. `ShowAsync` is awaited and completes when it is dismissed. |
| `Views/MessageBoxWindow` | Simple modal dialog. |
| `Controls/PosterCard` | A 2:3 poster plate that loads its own bitmap, tints itself from the title, and shows what the scanner parsed while it waits for artwork. |

## Services

| Service | Responsibility |
| --- | --- |
| `AppConfig` | Loads settings from the per-user data directory, resolves API keys, applies platform defaults. Never throws. |
| `ConfigStore` | Where `appsettings.json` is read from and written to. Never writes inside an app bundle, and refuses to save a resolved config. |
| `ConfigDiagnostics` | Names keys in the settings file that are not settings, and what each was probably meant to be. Reports them; never rejects the file. |
| `FirstRun` | Whether this launch has never been configured, and so whether to offer setup. |
| `JellyfinDiagnostics` | Names which of five connection failures happened, and what to try about it. |
| `JellyfinUploader` / `ISftpTransport` | Copies a film onto the Jellyfin server's disk over SFTP, then asks the server to rescan. Everything above the socket talks to the interface, so the whole of it is tested against a fake filesystem; `SshNetSftpTransport` is the only implementation that opens a connection. |
| `KnownHosts` | Reads OpenSSH's `known_hosts` and decides whether the key a server offered is the one it offered last time. Pure, so the file's awkward corners — the bracketed `[host]:port` form, hashed entries, `@revoked` — are testable without a server. |
| `PlatformPaths` | Every filesystem location, resolved per platform. Expands `%APPDATA%` and `~`. |
| `Database` | Opens the SQLite database, applies `Data/schema.sql` idempotently, and migrates an existing library. The schema script is all `CREATE ... IF NOT EXISTS`, so it cannot add a column to a table somebody already has — `Migrate` does that. |
| `ScanService` | Walks watch folders and upserts the `files` table, skipping unreadable directories. A completed scan stamps `files.missing_since` on every row it did not find under a folder it actually walked. |
| `MissingFilms` | What the library does about a film whose every file a scan could not find: leave it alone, keep it as a server film, or take it out of the library. Pure. |
| `TmdbService` | TMDB search, details and credits; builds image URLs. |
| `OmdbService` | Fetches an IMDb rating for one IMDb id. |
| `ImdbRatingService` | Caches ratings in SQLite so a rating is never fetched twice. |
| `PosterAutoLoader` | Fills in missing posters in the background. |
| `ImageLoader` | Loads posters and backdrops from a URL or local file into an Avalonia bitmap. |
| `MovieFileMatcher` | Matches a catalogue entry to a file on disk. |
| `FileLauncher` | Opens a movie in the default player, per platform. |
| `BuildKeys` | Reads API keys compiled in at build time. |
| `AppLog` | Best-effort diagnostics under the app data folder. |

## Cross-platform behaviour

Nothing may assume Windows. The specific decisions:

- **Paths.** All app data lives under
  `Environment.GetFolderPath(SpecialFolder.ApplicationData)/UrDatabase`, which resolves to
  `%APPDATA%\UrDatabase` on Windows and `~/Library/Application Support/UrDatabase` on macOS.
  `PlatformPaths.Expand` still understands `%APPDATA%`, `%LOCALAPPDATA%`, `%USERPROFILE%` and a
  leading `~`, and rewrites backslashes, so a config file written by an older Windows install
  keeps working.
- **Watch folders.** The default is derived per platform — `~/Movies` on macOS, the Videos known
  folder on Windows. No drive letter is ever hardcoded.
- **Launching a movie.** `UseShellExecute` cannot open documents on macOS, so `FileLauncher`
  uses `open` there, `xdg-open` on Linux, and shell execute on Windows. Arguments are passed via
  `ArgumentList`, so paths with spaces are safe.
- **Case sensitivity.** File extension checks and title matching are ordinal and
  case-insensitive, so they behave the same on APFS, NTFS and case-sensitive volumes.

## Database

`src/UrDatabase.App/Data/schema.sql` is applied on every `Database.Open`. Every statement is
`IF NOT EXISTS`, so it creates a working database on a fresh install without disturbing an
existing library.

- `movies` — the catalogue.
- `files` — files found by the scanner, unique on `file_path` for the upsert. `missing_since`
  records a file a completed scan looked for and could not find; it is cleared the moment the
  file is seen again or is linked by hand.
- `movies_fts` — FTS5 index over `movies`, kept in sync by triggers.
- `imdb_ratings` — cached IMDb ratings. A row with a `NULL` rating records "asked already, there
  is none", which is what stops the app re-requesting it.

### Opening it

`Database.Connect` is the only place a connection to the catalogue is constructed, and a test
enforces that rather than trusting it. It sets `foreign_keys`, a `busy_timeout` and WAL, and
bounds the provider's own lock wait to match the busy timeout; `Database.Open` is `Connect` plus
the schema, for callers that could be the first thing to touch a fresh install. The window's read
path uses `Connect`, because re-running the schema on every keystroke in the search box is work
nobody asked for.

The split exists because the alternative failed quietly. The read path used to build its own
connection, which left the most frequent query in the app as the one connection in it with no busy
timeout — a difference invisible at the call site and in review.

### Writing to it

SQLite allows one writer at a time, and this app has several: a scan committing in batches, a
Jellyfin sync replacing the cached server library in one transaction, and the poster loader
writing from up to four tasks at once. `DatabaseWriteLane` gives them a turn each, keyed by the
file SQLite reports it opened, so writers in this process never contend for the write lock at all.

What the lane cannot see is a second copy of the app on the same file. The busy timeout handles
that, and a bounded retry on `SQLITE_BUSY` and `SQLITE_LOCKED` handles what the timeout does not.
The retry is finite on purpose and rethrows what survives it: a write that cannot be made is the
caller's to report, and the bug that produced all of this was about failures nobody was told
about.

The scan takes its turn per batch rather than per scan. Holding the lane for a whole library would
shut the poster loader out for the length of it, which is the starvation `FilesPerTransaction`
already exists to prevent.

## Configuration

Settings are read from the first file that exists: an explicit path, then
`appsettings.json` in the per-user data directory, then `appsettings.json` next to the
executable, then the tracked `appsettings.example.json` that ships beside it. The per-user file
is created from the example on first run, unless a build tree already has its own copy.

One refinement to that order: while the per-user file is still a byte-for-byte copy of the
template it drops below a config next to the executable, because a copy nobody has edited states
nothing. Otherwise a developer who ran the app once and wrote `appsettings.json` afterwards
would find it silently ignored.

The per-user location is not a preference. An installed macOS app runs from inside a signed,
notarized bundle: a file written next to the executable invalidates the code signature, so
Gatekeeper refuses to launch it, and an update discards it regardless. Nothing in `AppConfig`
writes to `AppContext.BaseDirectory` under any circumstance.

`appsettings.json` is gitignored so a personal key or personal folders can never be committed.
The app runs with no file at all, falling back to platform defaults.

API keys resolve most specific first: the loaded `appsettings.json`, then the
`URDATABASE_TMDB_API_KEY` / `URDATABASE_OMDB_API_KEY` environment variables, then whatever was
compiled in at build time. Compiled-in keys default to empty, so a local build needs no secrets.

## Attribution

TMDB's API terms require attribution, shown in the main window footer and the details window:
*"This product uses the TMDB API but is not endorsed or certified by TMDB."* OMDb is credited as
the source of the IMDb rating. Neither IMDb nor OMDb endorses this application, and IMDb's logo
and wordmark are not used.
