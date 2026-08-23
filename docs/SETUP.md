# Setup

> The repository README is owned by another workstream and is updated separately. This file
> covers building and running the app itself.

## 1) Install the .NET 8 SDK

`dotnet --version` should print `8.x`. Nothing else is required — no Visual Studio, no Windows.

## 2) Build and run

```bash
dotnet build UrDatabase.sln -c Release
dotnet test UrDatabase.sln -c Release
dotnet run --project src/UrDatabase.App/UrDatabase.App.csproj
```

This works on both Windows and macOS from the same checkout. The test suite needs no API keys
and never touches the network.

## 3) Point it at your movies

The app creates its database on first use and needs no configuration to start. The first launch
opens a setup screen that writes `appsettings.json` for you — folders on this machine, a Jellyfin
server, or both, plus the two optional API keys — and the **Settings** button reopens it later.
Everything below is the same file, for anyone editing it directly.

Settings live in `appsettings.json` in the app's own data directory, which is where the setup
screen writes it:

| Platform | Where |
| --- | --- |
| macOS | `~/Library/Application Support/UrDatabase/appsettings.json` |
| Windows | `%APPDATA%\UrDatabase\appsettings.json` |

The app also puts a copy of `appsettings.example.json` there on first run, so there is a real
file to edit rather than a path to create. It sits beside the database, the poster cache and the
logs, and it survives an update.

**Never put configuration inside an installed `UrDatabase.app`.** The bundle is signed and
notarized; a file written next to the executable breaks the code signature and macOS then
refuses to launch it, and the next update discards it anyway. Nothing in the app writes there
either — a save always lands somewhere writable outside the bundle.

Working from a checkout is different, and unchanged. Copy the example next to the binary and it
is read, edited and saved there:

```bash
cp src/UrDatabase.App/appsettings.example.json src/UrDatabase.App/appsettings.json
```

That file is gitignored, so your key and your folders can never be committed, its presence stops
a per-user copy being created at all, and it is what tells the app it has been configured
already, so setup does not appear.

Configuration is read from the first of these that exists:

1. a path passed to `AppConfig.Load` outright — only tests do this
2. `<app data>/UrDatabase/appsettings.json` — the user's own
3. `appsettings.json` next to the executable — a build tree, or a portable install
4. `appsettings.example.json` next to the executable — the shipped template

With one exception, for the case of running the app once and writing a local config afterwards:
a per-user file that is still a byte-for-byte copy of the template records no decision, so it
drops below a file written next to the executable — and does not count as this install having
been configured. Editing it at all restores it to the top.

| Setting | Meaning | Default when blank |
| --- | --- | --- |
| `DatabasePath` | Where the SQLite catalogue lives | `<app data>/UrDatabase/movies.db` |
| `WatchFolders` | Absolute folders to scan | `~/Movies` on macOS, Videos on Windows — but nothing at all once `SetupCompleted` is set |
| `PosterCacheDir` | Where downloaded posters are cached | `<app data>/UrDatabase/posters` |
| `DownloadFolder` | Where a film downloaded from Jellyfin is saved | `UrDatabase` inside your films folder |
| `TmdbApiKey` | TMDB key for metadata and posters | none |
| `OmdbApiKey` | OMDb key for the IMDb rating | none |
| `DownloadPosters` | Cache posters to disk instead of loading them from TMDB | `false` |
| `TmdbImageSize` | TMDB image size, e.g. `w185`, `w342`, `w500`, `original` | `w342` |
| `SetupCompleted` | Written by the setup screen once answered; stops it being offered again | `false` |
| `Jellyfin` | An optional server to browse; blank switches the feature off | off |

`<app data>` is `%APPDATA%` on Windows and `~/Library/Application Support` on macOS. Paths may
use `%APPDATA%`, `%USERPROFILE%` or a leading `~`; a config file written on Windows still
resolves on macOS.

## 4) API keys

Metadata and ratings are optional. Without keys the app still runs, scans your folders, and
browses your library — it simply shows no posters and no rating.

Keys resolve **most specific first**:

1. whichever `appsettings.json` was loaded, per the order above
2. the `URDATABASE_TMDB_API_KEY` / `URDATABASE_OMDB_API_KEY` environment variables
3. whatever was compiled in at build time

Official release builds have keys compiled in, so they work with no configuration. **A local
build from source has no keys compiled in**, which is deliberate: contributors must never need a
key to build, run or test. If you want live metadata from your own build, supply your own key by
either of the first two routes — no rebuild required for the environment variable.

- A TMDB key is free from <https://www.themoviedb.org/settings/api>.
- An OMDb key is free from <https://www.omdbapi.com/apikey.aspx>.

Ratings are cached in the database and never fetched twice, which matters because OMDb's free
tier allows 1,000 lookups a day.

## 5) Publishing

The app publishes self-contained for each supported runtime:

```bash
dotnet publish src/UrDatabase.App/UrDatabase.App.csproj -c Release -r osx-arm64 --self-contained
dotnet publish src/UrDatabase.App/UrDatabase.App.csproj -c Release -r osx-x64   --self-contained
dotnet publish src/UrDatabase.App/UrDatabase.App.csproj -c Release -r win-x64   --self-contained
```

Release builds add the keys as MSBuild properties:

```bash
dotnet publish src/UrDatabase.App/UrDatabase.App.csproj -c Release -r osx-arm64 --self-contained \
  -p:TmdbApiKey="$TMDB_API_KEY" -p:OmdbApiKey="$OMDB_API_KEY"
```

Both properties default to empty, so omitting them is always valid.

The version comes from `<Version>` in `Directory.Build.props`, the single source of truth.

### macOS note

A local `dotnet publish` produces an ad-hoc signed binary. That runs on the
machine that built it, and **nothing else** — a current Mac kills an ad-hoc
signed download the moment it starts, with no dialog and no output, and
clearing the quarantine attribute does not change that because it is the
signature being refused. Only the release pipeline signs with a Developer ID
and notarizes; see [releases.md](releases.md#signing-and-notarization).

To reproduce what CI does, with a Developer ID already in your login keychain:

```bash
dotnet publish src/UrDatabase.App/UrDatabase.App.csproj \
  -c Release -r osx-arm64 --self-contained true -o /tmp/publish

python3 tool/make_macos_bundle.py \
  --publish-dir /tmp/publish --output /tmp/stage --version 0.2.1 \
  --icon src/UrDatabase.App/Assets/UrDatabase.icns

MACOS_SIGNING_IDENTITY="Developer ID Application: … (TEAMID)" \
  scripts/package-macos-app.sh /tmp/stage/UrDatabase.app /tmp/UrDatabase.dmg
```

Without notarization credentials that stops after signing and says so.
`security find-identity -v -p codesigning` lists the identities you have.
