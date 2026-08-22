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

The app creates its database on first use and needs no configuration to start. Everything below
is optional.

Copy `src/UrDatabase.App/appsettings.example.json` to `src/UrDatabase.App/appsettings.json` and
edit it. That file is gitignored, so your key and your folders can never be committed.

| Setting | Meaning | Default when blank |
| --- | --- | --- |
| `DatabasePath` | Where the SQLite catalogue lives | `<app data>/UrDatabase/movies.db` |
| `WatchFolders` | Absolute folders to scan | `~/Movies` on macOS, Videos on Windows |
| `PosterCacheDir` | Where downloaded posters are cached | `<app data>/UrDatabase/posters` |
| `TmdbApiKey` | TMDB key for metadata and posters | none |
| `OmdbApiKey` | OMDb key for the IMDb rating | none |
| `DownloadPosters` | Cache posters to disk instead of loading them from TMDB | `false` |
| `TmdbImageSize` | TMDB image size, e.g. `w185`, `w342`, `w500`, `original` | `w342` |

`<app data>` is `%APPDATA%` on Windows and `~/.config` on macOS. Paths may use `%APPDATA%`,
`%USERPROFILE%` or a leading `~`; a config file written on Windows still resolves on macOS.

## 4) API keys

Metadata and ratings are optional. Without keys the app still runs, scans your folders, and
browses your library — it simply shows no posters and no rating.

Keys resolve **most specific first**:

1. `appsettings.json`
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
