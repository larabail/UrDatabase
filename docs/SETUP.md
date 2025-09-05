# Setup (Detailed)

## 1) Create GitHub repository
- Name: **movie-db** (Private)
- Default branch: **main**
- Add no starter files.

## 2) Initialize locally and push
```bash
git init
git add .
git commit -m "chore: repo scaffold"
git branch -M main
git remote add origin <YOUR_GITHUB_REMOTE_URL>
git push -u origin main
```

## 3) Install tools
- Visual Studio 2022+ with **.NET desktop development**
- Or **.NET 8 SDK**: `dotnet --version` should print 8.x

## 4) Configure app
- Create folder `%APPDATA%\MovieDb\`
- Copy your `movies.db` there (or create using `/src/MovieDb.App/Data/schema.sql`)
- Copy `src/MovieDb.App/appsettings.example.json` → `src/MovieDb.App/appsettings.json` and edit:
  - `WatchFolders`: list of absolute paths (e.g., `D:\Movies\New`)
  - `TmdbApiKey`: your TMDb API key (optional)
  - `PosterCacheDir`: local folder for downloaded posters

## 5) Build
```bash
cd src/MovieDb.App
dotnet restore
dotnet build
dotnet run
```

## 6) Next steps
- Implement scanner service
- Implement metadata service (TMDb/OMDb) with caching
- Wire search box to FTS
