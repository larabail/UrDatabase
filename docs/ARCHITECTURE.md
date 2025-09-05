# Architecture (Initial)

## Overview
- **WPF (.NET 8)** for native Windows UI
- **SQLite** single-file DB for offline-first persistence
- **Dapper** for data access (simple and fast)
- **Services**:
  - `ScanService`: walks watch folders, inserts/updates `files` table
  - `LaunchService`: opens file via default player
  - `FileService`: move/rename files (atomic on same drive)
  - `MetadataService`: calls TMDb/OMDb, caches results to DB + poster cache dir

## Database
See `src/MovieDb.App/Data/schema.sql`. Includes FTS5 for fast search.

## Config
`appsettings.json`:
```json
{
  "DatabasePath": "%APPDATA%\\MovieDb\\movies.db",
  "WatchFolders": ["D:\\Movies\\New", "E:\\Videos\\Movies\\NEW"],
  "TmdbApiKey": "",
  "PosterCacheDir": "%APPDATA%\\MovieDb\\posters"
}
```
