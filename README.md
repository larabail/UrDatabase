# UrDatabase (Windows, WPF, SQLite)

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
   - Git
   - Visual Studio 2022+ with `.NET desktop development` *or* .NET 8 SDK
2) **Create the repo on GitHub**
   - Name `urdatabase` (private), add no files.
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
   - Copy your SQLite DB to: `%APPDATA%\UrDatabase\movies.db` (create the folder).
   - Or generate one from `/src/UrDatabase.App/Data/schema.sql` using your importer.
   - Copy `src/UrDatabase.App/appsettings.example.json` → `appsettings.json` and edit values.
5) **Build & run**
```bash
cd src/UrDatabase.App
dotnet build
dotnet run
```

> **Note:** Do **not** commit your actual movie files or `movies.db`. This repo tracks source code and templates only.

## Folder layout
```
urdatabase/
  src/
    UrDatabase.App/       # WPF app
      Data/schema.sql     # SQLite schema
      appsettings.example.json
  docs/                   # setup & design notes
  scripts/                # local helper scripts
  .github/                # CI and issue templates
```

## Roadmap
- [ ] File scanner to populate `files` table from watch folders
- [ ] Metadata service (TMDb) + caching of posters
- [ ] Search UI (FTS)
- [ ] Move/rename UI (atomic on same drive)
- [ ] Optional embedded player (LibVLC)
