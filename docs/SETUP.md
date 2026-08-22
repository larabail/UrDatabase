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

Copy `src/UrDatabase.App/appsettings.example.json` to `src/UrDatabase.App/appsettings.json` and
edit it. That file is gitignored, so your key and your folders can never be committed. A file you
write by hand takes precedence over anything setup saved, and its presence is also what tells the
app it has been configured already, so setup does not appear.

| Setting | Meaning | Default when blank |
| --- | --- | --- |
| `DatabasePath` | Where the SQLite catalogue lives | `<app data>/UrDatabase/movies.db` |
| `WatchFolders` | Absolute folders to scan | `~/Movies` on macOS, Videos on Windows — but nothing at all once `SetupCompleted` is set |
| `PosterCacheDir` | Where downloaded posters are cached | `<app data>/UrDatabase/posters` |
| `TmdbApiKey` | TMDB key for metadata and posters | none |
| `OmdbApiKey` | OMDb key for the IMDb rating | none |
| `DownloadPosters` | Cache posters to disk instead of loading them from TMDB | `false` |
| `TmdbImageSize` | TMDB image size, e.g. `w185`, `w342`, `w500`, `original` | `w342` |
| `SetupCompleted` | Written by the setup screen once answered; stops it being offered again | `false` |

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

A self-contained macOS build is ad-hoc signed but not notarized. Gatekeeper kills unnotarized
binaries on first run (`Killed: 9`), so a distributed macOS build needs signing and notarization,
or users must clear the quarantine attribute themselves.
