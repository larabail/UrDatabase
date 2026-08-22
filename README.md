# UrDatabase

A desktop app for cataloguing a film collection you already have on disk. Point
it at your folders and scan: it reads the titles out of your filenames, builds
a local SQLite catalogue, lays it out as poster art grouped by genre, and fills
in the posters, plot, runtime, cast and crew from
[TMDB](https://www.themoviedb.org/) as you look at them, with the IMDb rating
fetched from [OMDb](https://www.omdbapi.com/). Nothing of yours leaves the
machine: the only things sent out are the title being looked up and, for the
rating, its IMDb id.

It runs on Windows and macOS from one codebase, built with
[Avalonia UI 11](https://avaloniaui.net/) on .NET 8.

## Features

- **Browse by genre.** The library opens as rows of poster cards, one row per
  genre, newest first within each row. Genre chips across the top narrow the
  view to a single genre (`Views/MainWindow`).
- **Search.** Typing in the search box queries the `movies_fts` full-text index
  and replaces the grouped view with a flat, ranked list of hits.
- **Details on click.** Opening a card fetches the film from TMDB and shows the
  overview, runtime, genres, backdrop, the top ten billed cast with their
  characters, and up to three directors and three writers. The star rating
  beside it is the **IMDb** rating, looked up from OMDb using the IMDb id TMDB
  returns; if no OMDb key is available the star is simply absent and the rest
  of the page is unaffected (`Services/TmdbService`,
  `Views/MovieDetailsWindow`).
- **Posters fill themselves in.** Any film in the catalogue with no poster is
  looked up in the background, four at a time, and the result is written back
  to the database so the next launch is instant. Posters are either referenced
  at their TMDB URL or downloaded into a local cache directory, depending on
  `DownloadPosters` (`Services/PosterAutoLoader`).
- **Scan your watch folders.** The scan button walks the configured folders for
  video files — `.mkv`, `.mp4`, `.avi`, `.mov`, `.wmv`, `.m4v`, `.mpg`, `.mpeg`
  — parses a title and year out of each filename, creates or reuses a canonical
  row in `movies`, and links the file to it. Scanning an empty database is what
  gives you a library; re-scanning is idempotent, so two spellings of one title
  collapse onto a single film rather than multiplying
  (`Services/ScanService`, `Services/FilenameParser`, `Services/MovieIndex`).
- **Play.** The details window hands the linked file to whatever the operating
  system uses to open it. A file can also be linked by hand from a file picker.

[Known gaps](#known-gaps) is worth reading before you judge any of the above;
several are thinner than they sound.

## Tech stack

| Piece | What it is for |
| --- | --- |
| Avalonia UI 11 | The interface, on Windows and macOS from one project |
| .NET 8 | Runtime and SDK, target framework `net8.0` |
| SQLite, via `Microsoft.Data.Sqlite` | The catalogue, a single file on disk |
| Dapper | Data access — the queries are small and hand-written |
| CommunityToolkit.Mvvm | Observable models behind the views |
| TMDB API v3 | Search, posters, plot, runtime, genres, cast and crew |
| OMDb API | The IMDb rating, and nothing else |

There is no server, no account and no telemetry, and the app touches no
Firebase: the only outbound traffic is to `api.themoviedb.org`,
`image.tmdb.org` and `www.omdbapi.com`. It works fully offline, with metadata
and ratings simply absent.

## Getting started

### Prerequisites

The [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), and nothing
else. `dotnet --version` should print `8.` something. Avalonia needs no
platform workload, no Visual Studio and no Xcode: any editor and the `dotnet`
CLI are enough on both operating systems.

No API key is needed to build the app or to run the tests, and a downloaded
release needs none either — official builds carry both keys already. You only
supply a key if you build from source and want live metadata. See
[Configuration](#configuration).

### Build and run

The same four commands work identically on Windows and macOS:

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/UrDatabase.App
```

### Publish a standalone build

CI does this for you on release, but to produce one by hand, pick a runtime
identifier — `win-x64`, `osx-arm64` or `osx-x64`:

```bash
dotnet publish src/UrDatabase.App -c Release -r osx-arm64
```

## Configuration

Settings live next to the built app in `appsettings.json`. The file is
gitignored, because it holds your API keys and the absolute paths to your own
film folders. Copy the template and edit it:

```bash
cp src/UrDatabase.App/appsettings.example.json src/UrDatabase.App/appsettings.json
```

| Key | What it does |
| --- | --- |
| `DatabasePath` | The SQLite catalogue. Defaults to `UrDatabase/movies.db` under the user's application data directory |
| `WatchFolders` | Absolute paths the scan button walks, searched recursively |
| `TmdbApiKey` | Your TMDB v3 API key. Leave it empty to run without metadata |
| `OmdbApiKey` | Your OMDb API key. Leave it empty to run without the IMDb rating |
| `PosterCacheDir` | Where downloaded posters go |
| `DownloadPosters` | `false` points the UI at TMDB's own image URLs; `true` caches each poster to disk |
| `TmdbImageSize` | TMDB's poster width — `w185`, `w342`, `w500`, `original` |

Paths may contain environment variables and are expanded on load, so
`%APPDATA%\UrDatabase\movies.db` works on Windows. The application data
directory .NET reports is `%APPDATA%` on Windows and `~/.config` on macOS, so a
configuration file written on one is not portable to the other.

### API keys

**If you downloaded a release, there is nothing to do here.** Official builds
have both keys compiled in at release time, so metadata and ratings work out of
the box with no configuration at all.

Keys matter only when you build from source, because a build from source has
none compiled in. Without them the app builds, runs and passes its full test
suite — you simply get no posters, no details and no rating until you supply
your own.

| Service | Get a key from | What it buys |
| --- | --- | --- |
| TMDB | [your TMDB account settings](https://www.themoviedb.org/settings/api) | Posters and details. Without it, browsing, genres and search still work |
| OMDb | [omdbapi.com](https://www.omdbapi.com/apikey.aspx) | The IMDb star, and nothing else |

Either key can be given in `appsettings.json`, as `TmdbApiKey` and
`OmdbApiKey`, or in the environment:

```bash
export URDATABASE_TMDB_API_KEY=...       # macOS
export URDATABASE_OMDB_API_KEY=...
$env:URDATABASE_TMDB_API_KEY = '...'     # Windows PowerShell
$env:URDATABASE_OMDB_API_KEY = '...'
```

Each key is resolved in the same order: the configuration file first, then the
environment variable, then whatever was compiled in. So the file beats the
environment, the environment beats the shipped default, and anyone running an
official build can substitute their own key — to escape a shared quota, say —
without rebuilding anything.

Treat all of these as public, rate-limited credentials rather than secrets. A
desktop app has no server to keep a key behind, so a key it can reach is a key
its user can reach, whether you typed it in or we compiled it in. That is a
deliberate tradeoff and [SECURITY.md](SECURITY.md) explains when it is an
acceptable one. Never commit a key to this repository — see the same file for
the one that once was.

### The catalogue

Point `DatabasePath` anywhere and the app creates what it needs on first
launch. `src/UrDatabase.App/Data/schema.sql` is the full schema — the `movies`
and `files` tables, the `movies_fts` FTS5 index and the triggers that keep it
current — and every statement is `IF NOT EXISTS`, so it runs against a library
you already have without touching your data.

From there, filling the catalogue is a scan. Set `WatchFolders`, press the scan
button, and the films appear.

Filenames come in every shape, so the parser copes with the common ones:
`The Matrix (1999) 1080p.mkv`, `the.matrix.1999.BluRay.x264-GROUP.mkv`,
`The Matrix [1999].mp4`, underscores, junk brackets, and both Windows and macOS
paths. It strips resolution, source, codec, audio and release group from the end
of a name, keeps the hyphen inside a real title like `Spider-Man`, and prefers a
bracketed year over a bare one so `Blade Runner 2049 (2017)` resolves to the
right film and year. One casualty of splitting dotted names: genuine full stops
go with them, so `S.W.A.T.` arrives as `S W A T`.

## Downloads

Released builds for Windows and macOS are listed at
[urdatabase-downloads.web.app](https://urdatabase-downloads.web.app), a static
page deployed to Firebase Hosting. Every asset is also on the
[releases page](https://github.com/larabail/UrDatabase/releases), named
`UrDatabase-<version>-<rid>.zip`.

### macOS will refuse to open the download

The macOS builds are ad-hoc signed but **not notarized**, and that is enough for
Gatekeeper to refuse them. Downloading through a browser attaches a quarantine
flag, and on first launch macOS kills the process outright — no dialog, no error,
nothing in the interface. The app has not crashed and the download is not
corrupt; it looks broken because macOS declined to say anything.

Clear the flag once, after unzipping, against the folder that came out of the
archive:

```bash
xattr -dr com.apple.quarantine ~/Downloads/UrDatabase-*
```

Then open it normally. There is no `.app` bundle to put in `/Applications`: the
archive contains a folder named after the build, such as
`UrDatabase-0.1.0-osx-arm64/`, holding an executable called `UrDatabase.App`.
Run that.

This step exists only because notarization does not. Proper Developer ID
signing and notarization would remove it entirely and is the real fix, but it
needs a paid Apple signing identity that this repository does not have yet — so
the manual step is a gap, not a design decision. Building from source avoids it,
since nothing was downloaded to be quarantined.

## CI and releases

`Directory.Build.props` at the repository root holds a single `<Version>`, and
that one line is the source of truth for everything below. It starts at
`0.1.0`. Do not put a version in the `.csproj`.

- **On a pull request**, the workflows restore, build and test, then publish
  each runtime identifier and attach the archives to the run. You can download
  and try a branch before it merges.
- **On a merge to `main`**, the version in `Directory.Build.props` is tagged
  `v<version>`, the TMDB and OMDb keys are compiled in from the `TMDB_API_KEY`
  and `OMDB_API_KEY` repository secrets, a GitHub Release is created with
  `UrDatabase-<version>-win-x64.zip`, `UrDatabase-<version>-osx-arm64.zip` and
  `UrDatabase-<version>-osx-x64.zip` attached, and the downloads site is
  deployed to Firebase Hosting.

Compiling the keys in at release is what makes a downloaded build work with no
setup. It is not a way of keeping them private, and nothing here pretends it
is; [SECURITY.md](SECURITY.md) sets out why that is an acceptable trade for
these two keys in particular and when it would not be.

Because a merge releases, a pull request that changes anything under `src/` has
to bump the version, or the release will collide with a tag that already
exists. How far to bump is in
[AGENTS.md](AGENTS.md#versioning).

Hosting is the only Firebase product involved, and only CI touches it: the
site is a few static files describing where to get the binaries. There is no
database, no authentication and no functions, and nothing in `src/` talks to
Firebase at all. The Firebase project is `actordb-cf981` and the hosting site
is `urdatabase-downloads`.

## Repository layout

```
src/UrDatabase.App/          the application: one cross-platform project
  Views/                     windows and their code-behind
  Controls/                  reusable pieces, e.g. the poster card
  Models/                    what the views bind to
  Services/                  config, SQLite, scanning, TMDB, OMDb, posters
  Data/schema.sql            the full schema, applied on first launch
  appsettings.example.json   configuration template; the real file is ignored
tests/UrDatabase.Tests/      xUnit suite
Directory.Build.props        the single <Version> for the whole solution
web/                         the downloads site served by Firebase Hosting
docs/                        design notes
scripts/                     local helper scripts
.github/                     workflows, issue templates, the PR template
```

## Contributing

Read [AGENTS.md](AGENTS.md) first: it is the working agreement for humans and
agents alike, and it is short. The rules that catch people out are that `main`
is never committed to directly, that commits carry no `Co-authored-by` trailer,
and that a change under `src/` bumps the version.

`dotnet build` and `dotnet test` must both be clean before you open a pull
request.

## Known gaps

Stated plainly, so nobody has to find out by using it:

- **A scanned library has no genres.** Nothing writes the `genres` column yet,
  so every film from a scan lands in a single **Uncategorised** bucket. Since
  the grouped view is the main way to browse, a freshly scanned library looks
  bare until TMDB enrichment fills genres in — and no code does that yet.
- **Films only.** The filename parser has no concept of television, so
  `Show.S01E02` becomes an oddly titled film rather than an episode. A mixed
  library will look wrong rather than broken.
- **Files are matched to films by heuristic.** An exact filename stem wins,
  otherwise the first name containing the title. Two films whose titles are
  substrings of each other can still be confused.
- **Linking a file by hand does not persist.** The file picker updates the open
  window and nothing else; reopening the film forgets it.
- **Settings is a placeholder** that says so when clicked. Configuration is
  file-only for now.
- **macOS builds are not notarized**, so the first launch needs one `xattr`
  command, as described above.

## Licence

UrDatabase is **source-available, not open source**. The code is published so
it can be read and audited; reading it grants no right to ship it.

You may download an official build and use it for your own personal,
non-commercial purposes indefinitely, without asking — cataloguing your own
film collection is exactly what it is for. Passing a build on to someone else,
hosting it as a service, using it commercially, publishing a derivative or
reusing its code elsewhere all need written permission. See [LICENSE](LICENSE).

This product uses the TMDB API and the OMDb API but is not endorsed or
certified by either. Film metadata and artwork remain subject to TMDB's terms,
and IMDb ratings to OMDb's.

Security reports go through [SECURITY.md](SECURITY.md), privately — not through
a public issue.
