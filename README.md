# Movie DB (Windows, WPF, SQLite)

Personal movie database app for Windows. Offline-first with SQLite, fast file operations, and optional metadata fetch (TMDb/OMDb).

## Tech stack
- **WPF (.NET 8)** — native Windows GUI
- **SQLite** — local database
- **Dapper** (or EF Core) — data access
- **TMDb API** (optional) — posters, cast/crew
- **FileSystemWatcher** — watch & scan folders
- **Process.Start** — launch movies with default player

## Quick start
1) **Install prerequisites**
   - [Git](https://git-scm.com/)
   - [Visual Studio 2022+] with `.NET desktop development` *or* [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download)
2) **Create the repo on GitHub**
   - Web: New repo → name `movie-db` (private), add no files.
   - CLI (optional): `gh repo create movie-db --private --source . --remote origin`
3) **Initialize locally**
```bash
git init
git add .
git commit -m "chore: repo scaffold"
git branch -M main
git remote add origin <YOUR_GITHUB_REMOTE_URL>
git push -u origin main
```
4) **App config**
   - Copy your SQLite DB to: `%APPDATA%\MovieDb\movies.db` (create the folder).
   - Or generate one from `/src/MovieDb.App/Data/schema.sql` using your importer.
   - Copy `src/MovieDb.App/appsettings.example.json` → `appsettings.json` and edit values.
5) **Build & run**
   - Open `src/MovieDb.App/MovieDb.App.csproj` in Visual Studio **or**:
```bash
cd src/MovieDb.App
dotnet build
dotnet run
```

> **Note:** Do **not** commit your actual movie files or `movies.db`. This repo tracks source code and templates only.

## Folder layout
```
movie-db/
  src/
    MovieDb.App/         # WPF app
      Data/schema.sql    # SQLite schema
      appsettings.example.json
  docs/                  # setup & design notes
  scripts/               # local helper scripts
  .github/               # CI and issue templates
```

## Roadmap
- [ ] File scanner to populate `files` table from watch folders
- [ ] Metadata service (TMDb) + caching of posters
- [ ] Search UI (FTS)
- [ ] Move/rename UI (atomic on same drive)
- [ ] Optional embedded player (LibVLC)
```

