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

| View | Role |
| --- | --- |
| `Views/MainWindow` | Search, genre chips, and the grouped/flat/single-genre poster panels. |
| `Views/MovieDetailsWindow` | Backdrop, poster, metadata, cast and crew, play and link actions. |
| `Views/MessageBoxWindow` | Simple modal dialog. |
| `Controls/PosterCard` | Rounded poster tile that loads its own bitmap. |

## Services

| Service | Responsibility |
| --- | --- |
| `AppConfig` | Loads settings, resolves API keys, applies platform defaults. Never throws. |
| `PlatformPaths` | Every filesystem location, resolved per platform. Expands `%APPDATA%` and `~`. |
| `Database` | Opens the SQLite database and applies `Data/schema.sql` idempotently. |
| `ScanService` | Walks watch folders and upserts the `files` table, skipping unreadable directories. |
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
  `%APPDATA%\UrDatabase` on Windows and `~/.config/UrDatabase` on macOS. `PlatformPaths.Expand`
  still understands `%APPDATA%`, `%LOCALAPPDATA%`, `%USERPROFILE%` and a leading `~`, and
  rewrites backslashes, so a config file written by an older Windows install keeps working.
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
- `files` — files found by the scanner, unique on `file_path` for the upsert.
- `movies_fts` — FTS5 index over `movies`, kept in sync by triggers.
- `imdb_ratings` — cached IMDb ratings. A row with a `NULL` rating records "asked already, there
  is none", which is what stops the app re-requesting it.

## Configuration

`appsettings.example.json` is tracked and ships next to the binary. `appsettings.json` is
gitignored so a personal key or personal folders can never be committed. The app runs with
neither file present, falling back to platform defaults.

API keys resolve most specific first: `appsettings.json`, then the `URDATABASE_TMDB_API_KEY` /
`URDATABASE_OMDB_API_KEY` environment variables, then whatever was compiled in at build time.
Compiled-in keys default to empty, so a local build needs no secrets.

## Attribution

TMDB's API terms require attribution, shown in the main window footer and the details window:
*"This product uses the TMDB API but is not endorsed or certified by TMDB."* OMDb is credited as
the source of the IMDb rating. Neither IMDb nor OMDb endorses this application, and IMDb's logo
and wordmark are not used.
