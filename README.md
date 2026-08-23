# UrDatabase

A desktop app for cataloguing a film collection. Point it at your folders and
scan: it reads the titles out of your filenames, builds a local SQLite
catalogue, lays it out as poster art grouped by genre, and fills in the posters,
plot, runtime, cast and crew from [TMDB](https://www.themoviedb.org/) as you
look at them, with the IMDb rating fetched from
[OMDb](https://www.omdbapi.com/). If your films live on a
[Jellyfin](https://jellyfin.org/) server instead of on this disk, point it at
that too and browse and play them from the same window — television included,
season by season. Nothing of yours leaves the machine: the only things sent out
are the title being looked up, its IMDb id for the rating, and whatever your own
server is asked for.

It runs on Windows and macOS from one codebase, built with
[Avalonia UI 11](https://avaloniaui.net/) on .NET 8.

## Features

- **Set up on first launch.** A fresh install opens a setup screen instead of an
  empty library: tick folders on this computer, a Jellyfin server, or both, test
  the server before committing to it, and optionally paste your TMDB, OMDb and
  UrActor keys. It writes `appsettings.json` for you and never appears again — the
  **Settings** button reopens the same screen, and anything saved there is
  applied to the running window rather than at the next launch
  (`Views/SetupWindow`, `Models/SetupChoices`, `Services/ConfigStore`).
- **Browse by genre.** The library opens as rows of poster cards, one row per
  genre, newest first within each row, each shelf headed by the genre and the
  number of films on it. The genre row across the top carries those counts too,
  and picking one narrows the whole view to that genre. A server library brings
  about twenty genres and a window fits roughly fifteen, so the row is wheeled
  or dragged sideways, and it travels the whole way — the last genre on it can
  be read in full. `Cmd+F` — `Ctrl+F` on Windows — puts the cursor in the
  search field, and the field says so (`Views/MainWindow`).
- **Filter by where a film is.** When the library draws on both this computer
  and a server, a row above the genres offers **Everywhere**, **Offline** and
  **On the server**, each with a count. Genre and location are different
  questions: a scanned film has no genre until something enriches it, so without
  this every local film sat in the Uncategorised bucket, which sorts behind every
  genre a server library brings with it. A film in both places answers to both
  controls, so the counts deliberately do not add up to the total. The row is
  hidden entirely when everything comes from one place, or when every film is in
  both (`Services/LibraryFilter`).
- **Filter by what it is.** When the library holds both films and television, a
  second row beside that one offers **Everything**, **Films** and **Television**.
  Series share the genre shelves with films, because Jellyfin gives a programme
  real genres and it belongs in Drama rather than in a wing of its own — but
  every series card carries a **Series** badge and its season count where a film
  shows its year, so the two are mixed but never silently. Nothing is both, so
  unlike the row above these counts do add up. It is hidden entirely on a library
  of films only, and on one of television only: a permanent "Television 0" beside
  "Films 412" is a control whose only possible use is to empty the window
  (`Services/LibraryFilter`).
- **It looks like a screening room.** Warm near-black rather than blue-black,
  because a blue surround makes every poster look faintly green; one brass
  accent, spent only on the focus ring, the primary action and progress that is
  genuinely running; posters at a true 2:3; and chrome dimmed to hairlines and
  text so that forty pieces of artwork are the brightest thing on screen. Every
  colour, face and metric is a token in `Styles/Tokens.axaml`. That accent is
  the whole theme's, not just this app's markup: Avalonia's Fluent theme derives
  every selected, checked and focused state from an accent it takes from the
  operating system, so the seven shades it reads are computed from the brass
  token at startup (`Services/AccentPalette`). Without that, a window built
  entirely out of the palette above still turned macOS blue the moment anything
  was selected.
- **A film with no poster yet still says what it is.** Artwork is fetched in the
  background, so most of a freshly scanned library has none for the first
  minute. Rather than a wall of identical holes, each card shows the title and
  year the scanner parsed, on a plate tinted from the title, inside a dashed
  edge that means "not final" without putting forty spinners on one screen
  (`Controls/PosterCard`, `Services/PlateTint`).
- **Search.** Typing in the search box queries the `movies_fts` full-text index
  and replaces the grouped view with a flat, ranked list of hits. What you type
  is escaped into FTS5's own query language first, so a title with punctuation
  in it — `Face/Off`, `Mission: Impossible`, an apostrophe — is searched for
  literally instead of being read as search operators, and the word you are
  still typing matches by prefix (`Services/FtsQuery`). Films from a Jellyfin
  server are matched alongside them, on title and genre.

  The query runs off the interface thread, and only once the box has been quiet
  for 200ms, so typing stays smooth on a library of any size. A search you have
  moved on from is cancelled, and its results are refused even if it finishes
  after the one that replaced it — what you end up looking at is always the last
  thing you typed, never the last query to come back
  (`Services/SearchCoordinator`, `Services/LibraryLoader`,
  `Services/MovieRepository`).
- **It says why something is missing.** A local film's plot, cast and crew come
  from TMDB, so an install with no TMDB key has none — and the screen says that,
  and says which setting fixes it, rather than reporting "none found" for a
  question nobody asked. A server film says when the server itself supplied
  nothing (`Services/MissingMetadata`).
- **Details in place.** Opening a card fetches the film from TMDB and fills the
  whole window — not a dialog inside it — with the backdrop, overview, runtime,
  genres, the top ten billed cast set as name over character, and up to three
  directors and three writers. Escape or **Library** goes back.

  The facts under the title are each printed under the name of the service they
  came from, which is the one thing this screen has to get right: the **IMDb**
  rating comes from OMDb via the IMDb id TMDB returns, and Jellyfin's community
  rating is a different measurement of a different population. They are never
  inked alike or labelled merely "rating". Absent either key, that fact is
  simply absent and nothing else changes (`Services/TmdbService`,
  `Services/DetailFacts`, `Views/MovieDetailsView`).
- **Television, season by season.** A Jellyfin server's `tvshows` libraries are
  synced alongside its films, and opening a series shows its seasons as a row of
  chips with that season's episodes listed under them — number, title, one line
  of plot and a runtime. Clicking an episode streams it, through the same player
  a film uses. Seasons and episodes are fetched when a series is opened rather
  than during a sync, because two hundred programmes is several thousand
  episodes and a sync that walked them all would be a sync nobody waits for; once
  fetched they are cached, so a programme you have already opened still lists its
  episodes with the server switched off (`Services/SeriesLoader`,
  `Services/SeriesGrouping`, `Views/SeriesDetailsView`).
- **What the copy actually is, beside the year and the runtime.** A row of small
  badges says how big the picture is — `4K`, `2K`, `1080p`, `720p`, `SD` — along
  with its dynamic range, video codec, audio track, size on disk, and the
  languages it can be heard and read in as two-letter codes. Text codes rather
  than flag emoji, because Windows has no flag glyphs and the same build would
  show `GB` there and 🇬🇧 on macOS.

  The two sources of that are not equally trustworthy and the screen does not
  pretend otherwise. A film on a Jellyfin server is *measured*: the sync asks for
  `MediaStreams` and gets real pixel dimensions, real language tags and a real
  channel count. A scanned file has nothing but its own name, and a name is
  whatever the person who encoded it typed — so its badges are read from the
  filename and the tooltip says "according to the filename". Where both exist,
  the measurement wins.

  Nothing is read from a filename until the film's own title is behind us,
  which is the whole difficulty: "Italian", "Dual", "4K" and "Atmos" are all
  words that appear in titles. The tags are taken from after the year, or from
  the first token no title could contain — so *The Italian Job* is not an
  Italian-language release and *Casablanca.mkv* claims nothing at all
  (`Services/MediaFlags`, `Services/FilenameMediaInfo`, `Services/LanguageTag`).
- **What to put on next, and you already own it.** Where a series lists its
  episodes, a film gets a shelf of other films — and every poster on it is one
  already in this library, so every one of them plays. TMDB supplies the
  ordering, from its recommendations for the film you are looking at, and the
  catalogue supplies the contents; what is shown is the intersection. The other
  way round would be an advertisement rather than a library.

  Clicking one opens it, and its own shelf carries on from there, so a library
  can be wandered rather than searched. Following a film replaces the one on
  screen rather than stacking on it — **Library** and Escape go back to the
  library from wherever you have got to, which is the one thing they have always
  done.

  With no TMDB key, or for a film nothing has identified, it falls back to films
  sharing the most genres, and says so in its heading rather than claiming a
  resemblance nobody computed. When there is nothing to show it hides entirely
  and the plot takes the space back (`Services/RelatedFilms`).
- **The Academy, under the poster.** A film that was nominated for an Oscar says
  so: wins first, marked with a star, then the nominations, with the category and
  who it was for. The panel is hidden outright for the great majority of films
  that were never nominated for anything — a heading standing over nothing on
  nine films in ten reads as a request that failed rather than as a fact.

  The archive is searched by title, so the release year decides which film the
  results belong to: there are four films called *A Star Is Born* and three of
  them were nominated. A ceremony from the film's own year up to three years
  after it counts, which covers the early ceremonies that did not follow the
  modern rule and the international feature award that runs a year or two
  behind. Anything further away is a different film and is not attributed.

  Answers are cached in the catalogue, including the answer "never nominated",
  because that is the commonest one and the one it would be most wasteful to ask
  twice — the archive changes once a year, in March. A rate limit or a network
  failure is deliberately *not* cached, so one bad afternoon does not record "no
  awards" against a hundred films permanently. Optional like the other two keys:
  no `UrActorApiKey`, no awards, and nothing else changes.

  Films only, deliberately. A programme has never won an Academy Award, and the
  archive is searched by title — so asking about a series called *Fargo* would
  hand it the 1996 film's Oscars, which is the exact false attribution the year
  window exists to prevent (`Services/OscarsService`, `Services/UrActorService`,
  `Services/OscarMatch`).
- **Posters fill themselves in.** Any film in the catalogue with no poster is
  looked up in the background, four at a time, through one shared connection to
  TMDB, and the result is written back to the database so the next launch is
  instant. A result is only accepted when its title agrees with the catalogued
  one and its year corroborates rather than contradicts it; TMDB's search always
  answers with something, and its answer for a short title is often a longer film
  that contains it, so an unverified first hit would put another film's artwork on
  the card and leave it there. Posters are either referenced at their TMDB URL or
  downloaded into a local cache directory, depending on `DownloadPosters`
  (`Services/PosterAutoLoader`, `Services/TmdbMatch`).
- **Say which film it actually is.** Two films share a title, a translation
  renames one, and a filename spells one wrongly, so some films are matched to
  the wrong TMDB record however careful the rules are. **Wrong film?** on the
  details screen searches TMDB and lists what it finds — poster, title, original
  title, year and plot — and choosing one replaces the artwork, plot, runtime,
  genres, cast and crew, **renames the film to what TMDB calls it**, and records
  the choice in `movies.tmdb_id` so nothing overwrites it and reopening the film
  does not undo it. The name a film was scanned under is kept in
  `movies.scan_title`, because the scanner matches files by the title it parses
  out of a filename: without it, a renamed film would stop answering to the name
  on disk and the next scan would catalogue it a second time. Where that has
  already happened — an older build, or one on a branch, scanning the same
  catalogue without knowing to look for the alias — the leftover row is cleared
  out, both as the rename happens and on the next completed scan
  (`Views/TmdbMatchWindow`, `Services/MovieMatch`, `Services/MovieIndex`,
  `Services/DiscardedNames`).
- **Scan your watch folders.** The scan button walks the configured folders for
  video files — `.mkv`, `.mp4`, `.avi`, `.mov`, `.wmv`, `.m4v`, `.mpg`, `.mpeg`
  — parses a title and year out of each filename, creates or reuses a canonical
  row in `movies`, and links the file to it. Scanning an empty database is what
  gives you a library; re-scanning is idempotent, so two spellings of one title
  collapse onto a single film rather than multiplying
  (`Services/ScanService`, `Services/FilenameParser`, `Services/MovieIndex`).
- **A re-scan notices what changed, including what is gone.** Every scan is
  recorded, and every file it walks past is stamped with the scan that saw it.
  A film you deleted is therefore *marked* missing rather than sitting in the
  catalogue forever — and marked rather than deleted, because from the app's
  side an unplugged drive and a deleted film are the same absence and only one
  of them should cost you a library. A folder that is not there when the scan
  runs is skipped, and nothing under it is touched. A film you dragged into
  another folder updates the row it already had instead of becoming a second
  copy. A scan you cancel keeps what it catalogued and concludes nothing about
  the rest. The result is reported as what it is — added, updated, unchanged,
  moved, failed and now missing, counted separately — rather than one number
  that meant all of them (`Services/ScanService`, `Services/ScanFileIndex`,
  `Services/ScanSessions`).
- **A film whose file is gone stops pretending otherwise.** Once a completed
  scan can no longer find any of the files a film has, the film stops claiming
  to be on this computer: no **Offline** badge, no answer to the **Offline**
  filter, and nothing offered to Play. If a Jellyfin server has it, it stays in
  the library as a server film, keeping its poster, its genres and any TMDB
  match you corrected by hand. If nowhere else has it, it leaves the library
  altogether. Nothing is deleted from the database, so putting the file back —
  or linking one by hand — brings the same film back on the next scan rather
  than a fresh copy of it (`Services/MissingFilms`).
- **Play.** The details window opens the file the catalogue links to this film —
  the link the scan wrote, not a guess at the filename — and hands it to whatever
  the operating system uses. A film with no link, or whose only linked copy has
  been moved or deleted, does not silently fall back to whichever file looks
  closest: if something unclaimed on disk resembles the title it is offered by
  name and Play asks first, and otherwise the window says plainly that nothing is
  linked. A file the last scan marked missing is offered for neither, on the
  catalogue's own account and before the disk is consulted. Linking a file by
  hand from the file picker settles it, and is
  remembered. Only the video types the scanner recognises can be linked or
  opened, checked both when the link is made and again before anything is
  launched — the app asks the operating system to open a path, and an OS will run
  a script as readily as it plays a film
  (`Services/PlayTargetResolver`, `Services/MovieFileMatcher`).
- **Browse a Jellyfin server.** Optional, off until you configure it. Point the
  app at a server and its movie library appears alongside your local one, with
  every server film badged **Server** so you can tell at a glance what is not on
  this machine. A film the server has and this computer has too is one card, not
  two, badged **Server** and **Offline**: it is the same film, and the pair of
  badges says both things about it — where it came from, and that it still plays
  with the network down. Delete this computer's copy and it loses the
  **Offline** badge and carries on as a server film, rather than either
  disappearing or claiming a file that is not there. The server describes its own films, cast and crew
  included, so a
  Jellyfin library is complete without a TMDB key. The library is cached in SQLite, so the window opens instantly
  and stays browsable with the server switched off or the laptop away from home
  — the films simply cannot play until it is reachable again. Playing one
  streams it, without transcoding, through VLC or IINA
  (`Services/JellyfinClient`, `Services/JellyfinCache`,
  `Services/JellyfinLibrary`, `Services/MediaPlayerLauncher`).
- **Continue watching, across every device.** A row above every genre with the
  films *and episodes* the server says you are part way through, in its own
  order — most recently watched first — each card carrying a brass rule along
  the bottom of the poster showing how far in you are and a line saying how much
  is left. An episode is titled with its programme and marked `S1E1`, under the
  programme's own poster, so it reads as a sibling of the films beside it — one
  episode per programme, the one you were last watching, so a series you dip in
  and out of does not fill the row on its own;
  **clicking it carries on watching it**, from where the server says you got to.
  That is the one card in this app that plays rather than opening a screen, so
  the status line says what it just did and the right-click menu offers the
  programme instead. It is the server's own
  answer, so a film started on the television carries on here. Cached like the
  library, so it is on screen the instant the window opens and stays there with
  the server switched off; an empty row is not shown at all. Opening a
  part-watched film replaces **Play** with **Continue watching**, which starts it
  where you stopped rather than at the beginning, with **Start again** beside it.
  Anything played here reports back, so what you watch in UrDatabase appears in
  Continue watching everywhere else too — VLC only, and see
  [Known gaps](#known-gaps) for what that costs. Right-click a card to take it
  out of the row **in this app only**; it comes back if you watch any more of it
  anywhere
  (`Services/ResumeRow`, `Services/ResumeDismissals`,
  `Services/PlaybackReporter`, `Services/VlcStatus`).
- **Download a server film to watch offline.** A film on the server has a
  **Download** button that keeps a copy on this disk, named the way the scanner
  reads it and catalogued the moment it finishes — so it is playable and
  searchable without waiting for a scan. Afterwards **Play** opens the local
  copy rather than the stream, which is the whole point: it works on a train.
  A transfer can be stopped and resumes where it left off, and a half-finished
  film is never mistaken for a whole one
  (`Services/JellyfinDownloader`, `Services/JellyfinDownload`).
- **Send a film the other way.** A film on this computer has an **Upload to
  Jellyfin** button that copies it onto the server for everyone else in the
  house, into the `Title (Year)/Title (Year).ext` layout Jellyfin's own libraries
  use, and then asks the server to rescan so it actually appears. Optional and
  off until configured, because Jellyfin's API has no endpoint that accepts a
  video file at all — the transfer is SFTP, and needs an account on the machine
  running the server. Bytes arrive under a name no scan reads as a film and take
  the film's real name only once the last one is there, so a cancelled or dropped
  upload leaves the server exactly as it was
  (`Services/JellyfinUploader`, `Services/JellyfinUpload`).
- **It says when there is a newer version.** A build that is behind the newest
  release raises a banner above the library — never a dialog, because an update
  is not urgent enough to stand between somebody and the film they opened the app
  to watch. **Update now** fetches the build for this machine into your downloads
  folder and opens it; **What's new** opens the release notes; **Later** puts it
  away until a newer version than the one dismissed comes out. It does not
  install anything, and the banner says so: the running app cannot replace itself
  — on macOS it is a signed bundle that would invalidate its own signature, on
  Windows a folder of files it holds open — so the archive is opened and the last
  step is yours. A machine no build is published for, or a fetch that fails, gets
  the downloads page instead of a dead end. One request to the GitHub releases
  API per launch, and `"CheckForUpdates": false` stops it being made at all
  (`Services/UpdateService`, `Services/UpdateFeed`, `Services/UpdatePrompt`).

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
| Jellyfin API | Optional: a remote library, its artwork, its stream, and the Continue watching row in both directions |
| SSH.NET | Optional: the SFTP transfer that puts a film on the server's disk, which Jellyfin's API cannot do |

There is no server of ours, no account and no telemetry, and the app touches no
Firebase: the only outbound traffic is to `api.themoviedb.org`,
`image.tmdb.org`, `www.omdbapi.com`, `api.github.com` — at most once a day, to
ask whether there is a newer release, and not at all when `CheckForUpdates` is
false — and, if you configure one, your own Jellyfin server, over HTTP for the
library and over SSH to its machine if you configure uploading. It works fully
offline, with metadata, ratings and the update check simply absent.

## Getting started

### Prerequisites

The [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), and nothing
else. `dotnet --version` should print `8.` something. Avalonia needs no
platform workload, no Visual Studio and no Xcode: any editor and the `dotnet`
CLI are enough on both operating systems.

No API key is needed to build the app or to run the tests, and a downloaded
release needs none either — official builds carry the keys already. You only
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

**You do not have to read this section.** The first launch opens a setup screen
that asks the two questions that matter — where the films on this machine are,
and whether there is a Jellyfin server — writes the answers to
`appsettings.json`, and does not ask again. The **Settings** button reopens it
whenever you want to change something. What follows is what that file contains,
for anyone who would rather edit it directly.

Settings live in `appsettings.json` in the app's own data directory, beside the
database, the poster cache and the logs:

| Platform | Where |
| --- | --- |
| macOS | `~/Library/Application Support/UrDatabase/appsettings.json` |
| Windows | `%APPDATA%\UrDatabase\appsettings.json` |

The app also puts a copy of the shipped template there on first run, so
configuring it by hand means editing a file that already exists.

Nothing is ever written inside `UrDatabase.app`. That bundle is signed and
notarized: a file written next to the executable breaks the seal, and macOS then
refuses to launch the app at all — so settings have to live somewhere writable
that also survives an update. Editing anything inside `UrDatabase.app` is never
the answer, and if you already have, reinstall from the DMG.

Configuration is read from the first of these that exists:

1. `<app data>/UrDatabase/appsettings.json` — yours, and where the setup screen saves
2. `appsettings.json` next to the executable — a build tree, or a portable install
3. `appsettings.example.json` next to the executable — the shipped template

Running from source is unaffected. A local `src/UrDatabase.App/appsettings.json`
is gitignored, is read and saved in place, and stops the per-user copy being
created at all. If one was created already — you ran the app before writing
yours — an untouched copy of the template does not outrank your file, and does
not count as this install having been configured either. Edit the per-user file
and it goes back to winning, everywhere.

To configure a checkout by hand:

```bash
cp src/UrDatabase.App/appsettings.example.json src/UrDatabase.App/appsettings.json
```

| Key | What it does |
| --- | --- |
| `DatabasePath` | The SQLite catalogue. Defaults to `UrDatabase/movies.db` under the user's application data directory |
| `WatchFolders` | Absolute paths the scan button walks, searched recursively. Empty means the platform's film folder, unless `SetupCompleted` is set — somebody who was asked and named none meant none |
| `TmdbApiKey` | Your TMDB v3 API key. Leave it empty to run without metadata |
| `OmdbApiKey` | Your OMDb API key. Leave it empty to run without the IMDb rating |
| `UrActorApiKey` | Your UrActor API key. Leave it empty to run without the Academy Award nominations |
| `PosterCacheDir` | Where downloaded posters go |
| `DownloadFolder` | Where a film downloaded from Jellyfin is saved. Defaults to a `UrDatabase` subfolder of the platform's film folder — inside what a scan already walks, so both halves of the library agree about it |
| `DownloadPosters` | `false` points the UI at TMDB's own image URLs; `true` caches each poster to disk |
| `TmdbImageSize` | TMDB's poster width — `w185`, `w342`, `w500`, `original` |
| `SetupCompleted` | Set by the setup screen once it has been answered, and the only thing that stops it being offered again |
| `CheckForUpdates` | `true`, as it ships, asks GitHub at most once a day whether there is a newer release and raises a banner if there is; the answer is kept in `update-state.json` so that further launches that day cost no request, and the request that is made is conditional, which GitHub does not charge when nothing has changed. `false` means no request is made at all, rather than one being made and its answer hidden |
| `Jellyfin` | An optional server to browse. Empty, as it ships, means the feature is off entirely — see [A Jellyfin server](#a-jellyfin-server) |
| `JellyfinSftp` | An optional SFTP account on the machine running that server, which is what makes **Upload to Jellyfin** appear. Empty, as it ships, means no upload button anywhere — see [Sending a film the other way](#sending-a-film-the-other-way) |

Paths may contain environment variables and are expanded on load, so
`%APPDATA%\UrDatabase\movies.db` works on Windows. The application data
directory .NET reports is `%APPDATA%` on Windows and
`~/Library/Application Support` on macOS, so a configuration file written on one
is not portable to the other.

Spell one of those keys wrong and the app says so. `"Url"` where the Jellyfin
setting is `ServerUrl` used to deserialise to nothing and start a perfectly
normal-looking app with an empty library and no explanation; now the status line
under the library names the key and what it was probably meant to be, the setup
screen repeats it — that being where somebody is likely to be fixing it — and
the same lines go to `startup.log`:

```
appsettings.json: unknown setting "Jellyfin.Url" — did you mean "ServerUrl"?
```

Saying so is all it does. The key is still ignored and the app still starts, so
a file written by a newer version cannot stop an older one from launching. A
file with no keys in it, or none at all, is not a problem and produces nothing:
a fresh install starts silently. Malformed JSON is a separate matter and is
still silent — see [issue #25](https://github.com/larabail/UrDatabase/issues/25).

### Where the file lives

The app reads `appsettings.json` from beside the executable, then from
`UrDatabase/appsettings.json` under the user's application data directory, then
falls back to the shipped `appsettings.example.json`. The first of those is the
documented location and the one the setup screen writes to; the second exists
only for an app installed somewhere its own folder cannot be written to, where
setup would otherwise have nowhere to put an answer. A file you edit by hand
therefore always wins over one the app wrote for itself.

Setup only ever writes what you typed into it. A key that came from an
environment variable, or one compiled into an official build, is shown as an
empty box and stays out of the file — otherwise pressing Save would copy a
shipped credential onto your disk under your own name, where nobody would think
to rotate it.

`URDATABASE_DATA_DIR` moves that application data directory, and with it the
whole install: `appsettings.json`, the catalogue, the poster cache and the logs
all hang off it.

```bash
URDATABASE_DATA_DIR=/tmp/urdb-scratch dotnet run --project src/UrDatabase.App
```

It exists so the app can be launched against a throwaway install without
touching a real one — for a verification run, for reproducing a bug against a
copied database, or for keeping two libraries apart. Setting `HOME` does not do
this and quietly appears to: on macOS .NET asks the operating system for the
application data directory rather than reading the environment, so a script that
sets `HOME` writes to the live install anyway. That has already cost somebody
their API keys, and it is why the variable exists rather than a note asking
people to be careful. Blank or unset leaves the install where it has always
been; a relative path is resolved against the working directory, since a macOS
bundle starts at `/`.

### When setup appears

Only on an install that has never been configured: no `appsettings.json` of its
own, no catalogue on disk, and no record of the screen having been answered
before. An install predating this screen has at least one of those and goes
straight to the library, as it always did. Skipping is an answer too — it goes
straight to the library and does not ask again.

### API keys

**If you downloaded a release, there is nothing to do here.** Official builds
have the keys compiled in at release time, so metadata, ratings and awards work
out of the box with no configuration at all.

Keys matter only when you build from source, because a build from source has
none compiled in. Without them the app builds, runs and passes its full test
suite — you simply get no posters, no details, no rating and no awards until you
supply your own.

| Service | Get a key from | What it buys |
| --- | --- | --- |
| TMDB | [your TMDB account settings](https://www.themoviedb.org/settings/api) | Posters and details. Without it, browsing, genres and search still work |
| OMDb | [omdbapi.com](https://www.omdbapi.com/apikey.aspx) | The IMDb star, and nothing else |
| UrActor | [developer.uractor.com](https://developer.uractor.com/) | The Academy Award nominations under the poster, and nothing else |

Any of them can be given in `appsettings.json`, as `TmdbApiKey`, `OmdbApiKey`
and `UrActorApiKey`, or in the environment:

```bash
export URDATABASE_TMDB_API_KEY=...       # macOS
export URDATABASE_OMDB_API_KEY=...
export URDATABASE_URACTOR_API_KEY=...
$env:URDATABASE_TMDB_API_KEY = '...'     # Windows PowerShell
$env:URDATABASE_OMDB_API_KEY = '...'
$env:URDATABASE_URACTOR_API_KEY = '...'
```

One thing about the UrActor key is worth knowing before you paste it anywhere:
that API takes it as the last segment of the URL path rather than in a header,
so it lands in server logs and browser history like any other part of a URL. It
grants read-only access to public awards data and nothing else. This app never
writes it to a log — `UrActorService.Redact` takes it out of anything bound for
one — but treat it as the low-value credential its own documentation says it is.

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

### A Jellyfin server

Entirely optional, and off unless you fill it in. Leave `ServerUrl` empty — as
it ships — and the app makes no request, opens no extra panel and behaves
exactly as it did before this existed.

The easiest way to fill it in is the setup screen, which has a **Test
connection** button: it signs in, finds the film and television libraries and
reports how much is in each, so a wrong address, a wrong password and a server
this app can read nothing from are told apart before anything is saved. The same
four fields can be written by hand instead:

```jsonc
"Jellyfin": {
  "ServerUrl": "http://media-box:8096",  // a bare host gets http:// added
  "Username": "you",
  "Password": "your password",
  "ApiKey": "",                          // the alternative to a username
  "LibraryName": ""                      // blank picks the first movie library
}
```

| Key | What it does |
| --- | --- |
| `ServerUrl` | Where the server is. A host with no scheme is assumed to be `http://`, since a server on a home network usually has no certificate |
| `Username`, `Password` | The preferred sign-in. Exchanged for a session token at startup |
| `ApiKey` | A Jellyfin API key, used when no username and password are given |
| `LibraryName` | Which **movie** library to read, when a server has more than one. It does not narrow television: a server routinely files that under several libraries — "TV Shows" and "Anime" is the usual pair — and all of them are read |

Any of the four can come from the environment instead, which is the way to keep
a password out of a file:

```bash
export URDATABASE_JELLYFIN_URL=http://media-box:8096
export URDATABASE_JELLYFIN_USERNAME=you
export URDATABASE_JELLYFIN_PASSWORD=...
export URDATABASE_JELLYFIN_API_KEY=...
```

**Prefer the username and password.** A Jellyfin API key is server-scoped and
administrative — Jellyfin has no narrower kind — whereas a session token is
scoped to one account, which is the smaller thing to keep on a laptop. The key
is offered because some setups only have one, not because it is the better
option.

Nothing is discovered automatically. Jellyfin's UDP discovery is off in many
deployments, including behind a reverse proxy, so the address is something you
type once rather than something the app guesses at.

#### When it will not connect

Five things can go wrong reaching a server, and the app names which one it hit
rather than reporting a single "could not reach the server" for all of them:

| What the app says | What happened | What to do |
| --- | --- | --- |
| the name could not be resolved | The address never got as far as being contacted | Use the server's IP address. A Tailscale, VPN or router-local name can work in your browser and still not resolve for the app |
| refused the connection | The machine is there and nothing is listening on that port | Check Jellyfin is running, and check the port — it is `8096` unless somebody changed it |
| did not answer in time | Neither refused nor completed | Usually a firewall dropping the connection, or a network this machine cannot currently see |
| does not look like Jellyfin | Something answered, but not Jellyfin | You have reached a reverse proxy that routes by hostname, and an address it does not recognise lands on the wrong site. Try `http://<address>:8096` directly |
| rejected the credentials | Jellyfin answered and said no | Check the username and password, or the API key |

The first and fourth are the ones that waste an evening, because in both cases
the address is genuinely correct in a browser. The **Test** button in setup says
the same thing before you save, and after any failed sync the app asks the
server to identify itself on `/System/Info/Public` and writes the verdict to
`logs/jellyfin.log` in its data directory — so the answer is on disk even for
the startup sync that never shows a dialog. Credentials are redacted out of that
file, including one written into the address itself.

An address is taken as typed: a bare host gets `http://`, a trailing slash is
dropped, and a port or a path prefix you wrote is left exactly as it is, because
a proxy may need either.

Once configured, **Sync Jellyfin** appears next to the scan button. The app also
syncs quietly at startup, after the window has already painted from the cache,
so a slow or absent server never delays anything you are looking at. A sync that
fails leaves the last good library in place and says so in the status line;
only a sync you asked for interrupts you with a dialog.

Server films are described entirely by the server — title, year, genres,
overview, runtime, artwork and the IMDb id — so **a Jellyfin library needs no
TMDB key at all**, and no title is ever run through the filename parser. Because
Jellyfin supplies real genres, its films group properly instead of piling into
the **Uncategorised** bucket a scanned library falls into.

A server with only films and a server with only television both work. Neither is
an error: only a server this app can read nothing from at all is, and it says
that rather than complaining about whichever half is missing.

The rating badge on a server film says `Jellyfin` when it is Jellyfin's own
community rating, and `IMDb` only for the IMDb rating from OMDb. They are
different numbers from different populations and are never shown under each
other's name.

#### Television

Series from the server's `tvshows` libraries are synced beside its films and
appear on the same genre shelves, each carrying a **Series** badge and the number
of seasons behind it where a film shows its year. The **Television** control in
the row above the genres takes them out of the library in one click, and that
row appears only when there is actually both kinds to choose between.

Opening a series is not opening a film. It shows the programme — its plot, its
cast, its ratings, how many seasons and episodes the server counted — and under
that a row of seasons, with the selected season's episodes listed below:
`S01E02`, the title, one line of plot and the runtime. Specials are listed last
rather than first, which is where the number alone would put them, because
somebody opening a programme wants episode one.

Clicking an episode plays it, through the same VLC or IINA that plays a film and
by the same `static=true` stream URL. That URL carries a token, so it is built at
the moment Play is pressed rather than being held on every row in the list. An
episode played this way reports its position back exactly as a film does, so it
appears in Continue watching here and on every other client — see
[Reporting playback back to the server](#reporting-playback-back-to-the-server).
From this list an episode starts at the beginning, because picking one out of a
season is asking to watch it rather than to carry on with it. Carrying on is
what the **Continue watching** row above the genres is for: an episode you are
part way through is there, and clicking that card resumes it. Its right-click
menu has **Open programme…**, which comes back to this screen at the season that
episode is in.

Seasons and episodes are **not** fetched by a sync. A library of two hundred
programmes is several thousand episodes, and pulling them all would turn a sync
that takes seconds into one that takes minutes, to fill in a screen almost nobody
has open. They are fetched when a series is opened, written to
`jellyfin_seasons` and `jellyfin_episodes`, and read from there first the next
time — so a programme opens instantly on the second visit and still lists its
episodes on a laptop nowhere near the server. The screen says which of the two it
is showing you.

A sync leaves those cached episodes alone, because it never asked the server
about them: clearing them would empty a programme's episode list on the strength
of a request that was not made. Reopening the series is what replaces them, and
an episode deleted upstairs stops being offered then.

Nothing folds a programme onto a film. *Fargo*, *Hannibal*, *Westworld* and
*Shōgun* are each a film and a programme, and the title matching that folds a
server film onto its local copy would make them one card — keeping the film and
losing the programme from the library entirely. Nor does a series carry a TMDB
id: Jellyfin reports a TMDB *television* id for one, which is a different
catalogue with its own numbering, and matching a film to it by number would be
wrong in a way nobody would spot.

There is no download for an episode, and no local television. See
[Known gaps](#known-gaps).

#### A film that is in both places

A film the server holds and this computer holds too is shown once, as one card
badged **Server** and **Offline**, and the details screen says the same thing on
its facts row.

The two are matched on the TMDB id first, when both sides have one — the
catalogue's `movies.tmdb_id` and the server's own provider id. That is the only
thing that survives the two sources disagreeing about the name: a file
catalogued as `El Drama` is `The Drama` on the server, and no amount of folding
case and punctuation turns one into the other.

Failing that, they are matched by title and year, using the rules a re-scan
already uses to avoid cataloguing the same film twice: case, accents and
punctuation are not differences, and a filename that carried no year is treated
as agreeing with the server's. The fallback has to stay, because only a film
something has identified has an id — a scan TMDB refused to match has none, and
so does a film the server could not identify.

The local row is the one kept, so such a film plays from disk, links to a file
and can have its TMDB match corrected like any other. It borrows the server's
genres when the scan gave it none, and the server's artwork until the catalogue
has its own — neither is written to the database, so nothing about it goes stale
when the server changes. Opening it fills anything TMDB could not answer from
the server's own description — plot, runtime, cast, crew, the IMDb id, and
Jellyfin's community rating — so folding the two cards together costs you
nothing, including on an install with no TMDB key. What is already answered is
left alone, or correcting a match would appear not to have taken
(`Services/ServerDetails`).

Delete this computer's copy and the card degrades rather than disappearing: the
next completed scan marks the file gone, the **Offline** badge comes off, and
what is left is a server film that streams. The catalogue row stays exactly
where it was, which is the point — it is what carries `movies.tmdb_id` and
`movies.scan_title`, so a match you corrected by hand outlives the file, and
putting the film back is a scan rather than a re-correction.

#### When a film is no longer here

A scan writes `files.missing_since` for every file it looked for and could not
find, and the library reads it. A film all of whose files carry that mark stops
being a film on this computer:

- it loses the **Offline** badge and stops answering the **Offline** filter;
- it is not offered to **Play**, and neither is the missing file offered as a
  suggestion for some other film;
- it stays in the library as a **Server** film if a Jellyfin server has it, with
  its poster, its genres and its corrected TMDB match intact;
- it leaves the library altogether if nowhere else has it.

Which of the last two happens is decided after the two halves of the library are
folded together, because only then does a local row know whether the server has
the same film. The rule is three outcomes from two facts and lives in
`Services/MissingFilms`, away from both the query that supplies the facts and
the window that renders the answer, so it can be asserted on.

Nothing is deleted from the database. A film that has left the library is a row
that is no longer shown, not a row that is gone, for the same reason the file
row itself is marked rather than removed: from in here, a film you deleted and a
film on a drive you unplugged are the same absence. It also means the way back
is cheap — put the file where it was, scan, and the film returns as the film it
always was rather than as a new one beside it. **Link File…** does the same for
a copy you moved somewhere the scan does not walk, but only while the film still
has a card of its own to open; once it has left the library, or degraded to a
server film, a scan is the way back.

Three things deliberately do not trigger any of this. A scan you cancelled
stopped somewhere arbitrary and marks nothing. A watch folder that was not there
when the scan ran was not searched, so an unplugged drive costs you nothing. And
a film the catalogue holds no file for at all — a row restored from an older
library — is left alone, because only a mark a scan actually wrote is evidence
that something went away.

#### The row a rename can leave behind

There is one row with no file that *is* removed rather than left, and it is the
narrowest case in the app. Correcting a film's match renames the row and keeps
the scanned name in `movies.scan_title` so the next scan finds the film it
already has. That works from the build that introduced it onwards — and a
catalogue is a file on disk that older builds, and builds on other branches, go
on opening. Anything scanning it without knowing to look for the alias
catalogues the film a second time under the name on disk, and because a scan
leaves an existing `files.movie_id` alone, that second row never gets a file.
What is left is a blank card that cannot be opened, played, matched or removed,
sitting next to the film it duplicates.

A row is cleared out only when every one of these is true. The name has to match
on the scanner's own key, so case, accents, punctuation and `&` are not
differences, and the years have to agree, so a remake is safe. Exactly one row
may claim that name as one it discarded — two claimants is an ambiguity, and an
ambiguity means doing nothing. That row has to be the older of the two, because
the duplicate is created by a scan that ran *after* the rename. It has to be the
one holding the file, since the whole mechanism is that the file stayed put. And
it has to carry a TMDB id, because a correction is the only thing that discards
a name and it writes both together. The row being removed, meanwhile, has to
hold nothing at all: no file, no TMDB id, no artwork, no genres, and no former
name of its own.

Each of those costs a row staying and would otherwise cost a film. The
conditions on the row being removed are asked twice — once to find it, and again
as part of the delete — because the catalogue has more than one writer and a
file linked in between must save it.

The sweep runs as the rename happens and again after any completed scan, so
debris that no rename ever saw is still found. A scan that failed to record a
file does not sweep at all: that failure leaves a committed row with no file,
which is exactly what debris looks like (`Services/DiscardedNames`).

This is the one place the app deletes a `movies` row, and the reason it is
allowed to is that the row is worth nothing: the film it names is right beside
it, holding the file, the identification and the artwork. Contrast the section
above, where the row is the only record the film ever existed and is kept
however long it stays missing.

#### Playing a server film

Films are streamed by default, and Jellyfin direct-plays most of them as
Matroska. The system's default opener answers an `http://` URL by launching a
browser, which cannot play that, so the app needs a real video player and looks
for **[VLC](https://www.videolan.org/vlc/)** or
**[IINA](https://iina.io/)** — either will do, and VLC exists on every platform
this app runs on. With neither installed, Play says so and names them both
rather than failing silently.

The stream URL carries an access token, because a player is handed a bare URL
and has nowhere to put a header. The app therefore never logs it, shows it or
puts it in a message; anything written to `jellyfin.log` has the token redacted
out of it first.

#### Continue watching

`GET /UserItems/Resume` is the server's own answer to "where was I", and the row
above every genre is that answer rather than something this app worked out. It
is asked for films **and episodes**, because a half-watched episode is the
commonest thing to be part way through and a row without them disagreed with
every other client in the house. A film started on the television is part way
through here, and the ordering — most recently watched first — is the server's
too.

For a film, only the position is cached, in `jellyfin_resume`, alongside the
library it belongs to. Titles, years and artwork are already in
`jellyfin_movies`, so the row is built by matching each entry's item id onto the
card the library already made. That has three consequences worth knowing:

- a film held both here and on the server appears once, as the same card badged
  **Server** and **Offline** that every shelf below shows;
- a resume entry the library cannot place — a film in a library this app was
  never pointed at, an episode of a programme it has never seen — has no card to
  land on and is dropped rather than rendered as an oddly titled film;
- the row narrows with the source controls, exactly as the shelves do.

An episode carries a little more, because nothing caches episodes until a
programme is opened: the row keeps the series id, the programme's name, the
season and episode numbers and the episode's own title. None of that can go
stale, because the whole table is replaced by each sync and so always says
exactly what the server last said. The card is titled with the **programme** and
marked `S1E1`, under the programme's own poster, because an episode's own name
identifies nothing — a real one here is "In throes of increasing wonder … ",
which names no show, no season and no place in it. The full name is in the
tooltip and on the series screen.

**Clicking an episode card plays it, from where you were.** It is the only card
in this app that starts a stream rather than opening a screen, and that is
deliberate: it is what the row is for and what every other Jellyfin client does.
The position the card carries is handed to the player, by the same
`--start-time` a film resumed from its details screen uses — see
[Picking a film up where you left it](#picking-a-film-up-where-you-left-it) for
how that works and what IINA cannot do with it.

What that costs is that the first row on the page can be played by accident, so
two things soften it: the status line names what started and says whether it
genuinely resumed — it will not claim a seek on a player that cannot make one —
and the right-click menu carries **Open programme…**, which opens the series
screen at the season that episode is in. Playing goes through the same
`StreamPlayback` door the series screen uses, so an episode started here reports
its position back exactly as one started there does, and neither entry point can
start an episode without following it.

A **film** in the row opens its details screen, as every other card in the app
does, and its button there says **Continue watching**.

**One episode per programme**, and it is the newest. Somebody who dips in and out
of a series is part way through several of its episodes at once, and showing all
of them would fill the row with one show — the same poster and the same title
repeated, with `S1E1` and `S1E2` the only thing telling two cards apart. The one
kept is the first the server listed, which is the episode you were last actually
watching, because that list is ordered most recently watched first. Dismiss it
and the next episode of that programme takes its place. Films are not folded
that way: two half-watched films are two different things to carry on with.

An episode resolves through its programme's card, which is what supplies the
poster, so filtering the library to **Films** takes the episodes out of the row
too — the row describes whatever the page is showing. A programme takes no
progress mark from an episode of it: "twenty minutes left" is true of one
episode and says nothing about a hundred hours of television.

It survives an unreachable server the way the library does, and for the same
reason: the cache is only replaced once the server has answered, so a sync
attempted away from home leaves the last good row exactly where it was. A row
that will not load at all — an older server, a permission, a proxy rewriting a
path — costs you the row and not the library: the sync succeeds, the previous
row stays, and the reason goes to `jellyfin.log`. An empty answer from a server
that *did* answer is a real answer and does clear it, and an empty row is left
off the page entirely rather than shown as a heading with nothing under it.

Anything with no position in it never appears, and neither does anything under a
second in: a player that has just been handed a stream reports a position before
anybody has watched anything.

The progress mark is deliberately shown wherever the card appears rather than
only in this row. It is the same film and the same fact, and a mark that
vanished as soon as you looked at the Drama shelf would be answering "where was
I" only in the place you already knew. The tooltip says it in words.

#### Taking something out of Continue watching

Right-click a card in the row and choose **Remove from Continue watching**. A
right-click because the row is the first thing on the page and a visible button
there would be one twitch away from being pressed by accident; the menu appears
only on a card that is actually in the row.

It is local to this app, entirely. **Nothing is sent to the server**, no other
Jellyfin client is affected, and the playback position is left exactly where it
was. Jellyfin's own answer to this is "mark unplayed", which hides the item in
every client in the house and throws the position away — a different act with a
much larger blast radius, and not the one on offer here.

**A dismissal lasts until the position moves.** The position it was made at is
recorded with it, and the moment the server reports a different one the
dismissal is stale and the item is back in the row. So:

- something you have genuinely abandoned stays gone, because nothing will ever
  move its position;
- something you dismiss here and then watch more of on the television comes back,
  because you have plainly not abandoned it;
- the list cleans itself up rather than growing into a blacklist nobody can see:
  each sync forgets the dismissals the server has moved past, and those for
  anything that has left the resume list altogether.

Dismissing one episode says nothing about the rest of its programme — the next
episode is a different thing to be part way through — and it is keyed on the
item, so nothing else on the shelves below changes.

Immediately afterwards, **⌘Z** (Ctrl Z away from a Mac) puts the last one back,
and the status line says so. That is one deep and for this session only; after
that, a dismissal ends the way every other one does, by the position moving.
Dismissals live in `jellyfin_resume_dismissals`, a table of their own rather
than a column on `jellyfin_resume` — that one is deleted and rewritten by every
sync, so a dismissal stored there would last minutes.

#### Picking a film up where you left it

Opening a part-watched film puts **▶ Continue watching** where **Play** normally
is, and **Start again** beside it. Continue watching hands VLC `--start-time`
with the position the server reported, so the film opens where you stopped;
Start again opens it at zero. Jellyfin direct-plays with byte ranges, which is
what lets VLC seek an HTTP input at all.

The button only says "Continue watching" when it will genuinely do that, and
four things have to hold at once: the server has a position for the film, the
film is being streamed rather than played from a downloaded copy, the stream
exists at all, and the installed player takes an offset. Fail any one and the
button reads **Play** and starts from the beginning, with the line underneath
saying why — an IINA user is told outright that only VLC can be told where to
open a film, rather than being left to wonder why a film the row calls
part-watched starts over.

That rule lives in `PlayPrompts.CanResume`, and the label and the seek position
are both read off it, so the button's words and its behaviour cannot drift
apart. A button that names what it will do and then does something else is
worse than one that never offered.

#### Reporting playback back to the server

Films **and episodes** played here report their position back, so what you watch
in UrDatabase shows up in Continue watching on every other device. **VLC only** —
see [Known gaps](#known-gaps).

Nothing in that path was ever about films: a report is an item id, a position
and a state, so an episode goes through exactly the same `POST /Sessions/Playing`,
`/Progress` and `/Stopped` as a film does. Until now the series screen simply did
not ask for it, and an episode watched here was invisible to every other client
in the house — which is the complaint the film half of this was built to answer.

VLC 3.x has an HTTP control interface. The app launches it with `--extraintf
http` — beside VLC's real interface, never `--intf`, because the point is to
watch the film — bound to `127.0.0.1` on a port the operating system has just
confirmed is free, and then reads `/requests/status.xml` every two seconds for
`time`, `length` and `state`. Those become `POST /Sessions/Playing` when the
film starts, `/Sessions/Playing/Progress` every ten seconds and on every pause
or resume, and `/Sessions/Playing/Stopped` when it ends. All three go through
the same client that holds the Jellyfin token.

**The interface password is generated fresh for every launch, from the
cryptographic random generator, and is never logged.** That is a security
requirement rather than tidiness: VLC takes it as a command line argument, and a
process's command line is readable by every account on the machine. A fixed
password would let any local user drive the viewer's player — and, through VLC's
own playlist commands, ask it to open files. A 256-bit secret that is worth
nothing once the film ends is the mitigation, and binding to loopback is what
keeps it off the network. The port is logged, because it is the useful half when
this does not work; the password never is.

None of it can stop a film playing. If a port cannot be found, VLC is launched
without the interface. If VLC refuses the arguments, it is launched again
without them. If the interface never answers — a build without it, a port taken
in the moment between being offered and being bound, a player closed
immediately — the app gives up after thirty seconds, reports nothing and says
nothing. A server that has gone away mid-film costs a resume position, not an
evening: every failure goes to `jellyfin.log` and none of them reaches a dialog.

A paused film is reported as paused rather than left to go quiet, because a
session that stops sending progress is one the server eventually times out —
which would turn "gone to make tea" into "stopped watching". A film nobody
actually started is never reported at all.

#### Keeping a copy

**Download**, on a server film, fetches the original file into `DownloadFolder`
instead of streaming it. That request is made by the app rather than by an
external player, so it authenticates with a header and its URL carries no token
at all — unlike the stream above, which is why a download is the safer of the
two to log.

The copy is named `Title (Year).ext` from the catalogue's own title, never from
the filename the server sends. Only the extension is taken from the server: a
remote name may contain path separators or `..`, and building the local name
from it would let a server decide where this app writes. The container is
whatever the server turns out to be holding, which is why "is this already
downloaded?" is answered by looking for the name without its extension.

Bytes land in a `.part` file and take the film's real name only once the last
one is written. That is what makes an interrupted transfer safe: a stopped
download keeps what it got and resumes from there, and a half-downloaded film is
never played, scanned or counted as the whole thing. If the server ignores the
resume request — a reverse proxy that does not implement ranges answers with the
whole file — the download starts again rather than appending a second copy onto
the first twenty minutes of one.

A finished download is written into the catalogue immediately, through the same
upserts a scan uses, so it is playable and searchable without anyone having to
work out that a scan is what makes a film appear. Because it lands inside a
folder a scan already walks, the later scan agrees with it instead of inserting a
duplicate.

The film then becomes exactly the case above: one card, badged **Server** and
**Offline**, matched to the server's copy by title and year. **Play** opens the
local file rather than the stream, and the film keeps working with the server
switched off — which is the whole point of having fetched it.

#### Sending a film the other way

**Upload to Jellyfin**, on a film that lives on this computer, copies it onto the
server so everything else in the house can watch it.

It is a separate setting from the rest of Jellyfin, and off until you fill it in,
because it needs something Jellyfin cannot provide. **Jellyfin's API has no
endpoint that accepts a video file** — the only uploads it takes are images and
subtitles. A film becomes a film by already being on the server's filesystem when
the library is scanned. So this copies the file there over SFTP and then asks
Jellyfin to look again:

```jsonc
"JellyfinSftp": {
  "Host": "media-box",              // or "uploader@media-box:2222"
  "Port": 2222,                     // 0 or absent means 22
  "Username": "uploader",
  "PrivateKeyPath": "~/.ssh/id_ed25519",
  "PrivateKeyPassphrase": "",       // only if the key has one
  "MoviesPath": ""                  // blank means "movies"
}
```

| Key | What it does |
| --- | --- |
| `Host` | The machine running Jellyfin, not Jellyfin itself. A port or an account written into it — `uploader@media-box:2222` — is read out rather than discarded, because that is how the address gets copied out of an `ssh` command |
| `Port` | The SSH port. An account set up only for uploads is routinely put somewhere other than 22 |
| `Username` | The SSH account, which is rarely the same name as the Jellyfin user |
| `PrivateKeyPath` | The **private** half of an SSH key pair — the file without the `.pub`. Expanded like every other configured path |
| `PrivateKeyPassphrase` | Optional, for a key that has one |
| `MoviesPath` | Where films go, **as that account sees it** |

All six can come from the environment instead, which is the way to keep any of it
out of a file:

```bash
export URDATABASE_JELLYFIN_SFTP_HOST=media-box
export URDATABASE_JELLYFIN_SFTP_PORT=2222
export URDATABASE_JELLYFIN_SFTP_USERNAME=uploader
export URDATABASE_JELLYFIN_SFTP_KEY=~/.ssh/id_ed25519
export URDATABASE_JELLYFIN_SFTP_PASSPHRASE=...
export URDATABASE_JELLYFIN_SFTP_MOVIES_PATH=movies
```

`URDATABASE_JELLYFIN_SFTP_KEY` holds a **path**, never key material. A private
key belongs in a file with permissions of its own, not in an environment that
every child process inherits.

**`MoviesPath` is the setting most likely to be wrong, and the reason is worth
knowing.** An upload account is usually chrooted, so it lands in a directory that
*is* its whole filesystem: the server's own `/tank/movies` is reached as
`movies`, and writing `/tank/movies` would create a `tank` directory inside the
chroot and put the film somewhere Jellyfin will never look. Blank means `movies`,
which is the usual answer. A path starting with `/` is kept absolute, for an
account that is not chrooted.

A password is not an option. The account worth pointing this at is one that can
do nothing but write films — no shell, chrooted, its own key — and such accounts
are set up key-only. Offering a password field would invite a server password
into a configuration file for no gain.

**The server's host key is checked against `~/.ssh/known_hosts`, and an upload is
refused if it does not match.** SSH.NET trusts whatever key it is handed unless
told otherwise, which would be quietly weaker than the `sftp` command this
replaces — that one checks the same file and hard-fails on a mismatch. The
private key is never at risk either way, since public key authentication does not
disclose it; what would be at risk is the film, handed to whatever answered on
that address.

Three outcomes, and they read differently because they mean different things:

| What the file says | What happens |
| --- | --- |
| The host is listed and this is one of its keys | The upload proceeds |
| The host is listed with a **different** key | Refused, naming both explanations — a rebuilt server, or something else answering — with the `ssh-keygen -R` line for the harmless one |
| The host is **not listed at all** | Refused, with the two ways to add it |

**An unknown host is refused rather than trusted on first use.** There is no
prompt and nothing is remembered: a key nothing has vouched for does not get a
film. If you have ever reached the server with `sftp` or `ssh` the entry is
already there and nothing needs doing. If you have not:

```bash
ssh-keyscan -p 2222 media-box >> ~/.ssh/known_hosts
# or simply connect once and accept the key
sftp -P 2222 uploader@media-box
```

The refusal names the file, the fingerprint the server offered — in the same
`SHA256:…` form `ssh-keygen -l` prints, so it can be compared against the server
directly — and the command that fixes it. Both fingerprints go to `jellyfin.log`
too, since a mismatch cannot be diagnosed without them and neither is a secret.

What arrives is `movies/Title (Year)/Title (Year).ext`: one directory per film,
named from the catalogue rather than from the local filename, which is the layout
Jellyfin's own libraries use and what lets it identify what it finds. A film
linked to `arrival.2016.1080p.WEB-DL.x265-GROUP.mkv` therefore arrives as
`movies/Arrival (2016)/Arrival (2016).mkv`. Only the extension comes from the
local file. Remote paths are built with forward slashes on every platform — using
`Path.Combine` would produce a single file on the server literally named
`Arrival (2016)\Arrival (2016).mkv`, which no scan would ever match.

The safety properties mirror the download's. Bytes arrive under a name ending in
`.uploading`, which is not a video extension and which a library scan running
mid-transfer walks straight past; the file takes the film's real name only once
the last byte is there and its size matches. A transfer that is cancelled, drops,
or arrives short takes its partial file with it, so the server is left as it was
rather than holding a forty-minute copy of a two-hour film. A film the server
already has costs no transfer at all, matched without regard to its extension so
that a library holding `Arrival (2016).mp4` is not sent an `.mkv` to sit beside
it.

Afterwards the app asks Jellyfin to rescan (`POST /Library/Refresh`), because a
file that has appeared on the disk is not yet a film the server knows about.
**That scan is asynchronous**, so the film appears shortly rather than instantly,
and the app says so rather than implying otherwise. Scanning is also an
administrative action: an ordinary Jellyfin account cannot start one, and a
server that refuses is not a failed upload — the film is on its disk and appears
at the next scheduled scan. The wording covers all three outcomes.

SSH is spoken by [SSH.NET](https://github.com/sshnet/SSH.NET), bundled rather
than shelled out to. The system `sftp` binary would mean parsing another
program's output for progress, no way to cancel mid-file short of a signal, and a
dependency Windows has only shipped since 2018. Everything above the socket talks
to `ISftpTransport`, so the whole of it is tested against a fake filesystem and
no test in this repository opens a connection (`Services/JellyfinUpload`,
`Services/JellyfinUploader`, `Services/SshNetSftpTransport`,
`Services/SftpFailure`, `Services/KnownHosts`).

### The catalogue

Point `DatabasePath` anywhere and the app creates what it needs on first
launch. `src/UrDatabase.App/Data/schema.sql` is the full schema — the `movies`
and `files` tables, the `scans` table each scan records itself in, the
`jellyfin_movies` cache and its television counterparts `jellyfin_series`,
`jellyfin_seasons` and `jellyfin_episodes`, the `jellyfin_resume` positions
behind the Continue watching row and the `jellyfin_resume_dismissals` that are
taken back out of it, the `movies_fts` FTS5 index and the
triggers that keep it current — and every statement is `IF NOT EXISTS`, so it
runs against a library you already have without touching your data.

Television is cached in three tables of its own rather than as a `kind` column on
`jellyfin_movies`. A series has no runtime and has two counts a film cannot have,
so a shared table would carry a column that is always null for one of them and a
discriminator every query would have to remember to filter on. The seasons and
episodes tables are the only ones in the app written outside a sync: they are
filled when a programme is opened, one series at a time.

`IF NOT EXISTS` covers a whole new table and does nothing at all for a new
column on a table that already exists, so `Database.Migrate` runs straight after
the script and adds those with `ALTER TABLE`. A column added to the schema file
alone would reach new installs only, and every existing library would fail on
"no such column" — which is to say it would work perfectly on a fresh clone and
break for everybody with films in it. Anything added to a table in the script
has to be added there too.

`IF NOT EXISTS` creates a missing table but says nothing about one that is
present and out of date, so a column added to an existing table needs more than
the script: `Database.Migrate` inspects each table and issues the
`ALTER TABLE ... ADD COLUMN` itself. That is how a catalogue built by an older
version gained `jellyfin_movies.cast_list` and `crew_list`, `movies.tmdb_id`,
which records which TMDB film each row is, and `movies.scan_title`, which keeps
the name the scanner gave a film once its displayed title can be corrected to
something else. Adding a column is the only migration
shape supported, and each one is nullable with no default, so nothing is
rewritten and no row can be lost. Losing the race to add one is tolerated rather
than reported: several connections open the catalogue at once, and only the
column existing afterwards matters.

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

Each file the scan records gets `files.movie_id` pointing at the film it belongs
to, and that column is the only thing Play consults. It matters that it is not
the filename: names are ambiguous in ways that bite hardest on the shortest
titles — "it" is inside "spirited", so a film called *It* used to open
`Spirited Away.mkv` — and they say nothing at all about a file somebody renamed.

A film with no usable link falls back to a suggestion rather than to a guess.
The name has to match on whole words, must not name a year other than the film's
own, and must be the only candidate of its strength; a title of five characters
or less needs the year before a partial match counts at all. Whatever survives
that is offered by name and opened only once you say so, and confirming it
records the link. If two files are equally good, nothing is offered — a coin
flip is not a match.

A scan, a Jellyfin sync and the poster fetches all write to that one file, and
they run at the same time — so the app makes them take turns rather than
collide, and browsing stays readable throughout. If a write genuinely cannot be
made, the status line says so; it does not fail silently and leave you to
wonder why a poster never arrived. Two copies of the app open on the same
catalogue is the one case that is only handled rather than prevented: they wait
for each other, and either may eventually give up and tell you.

Closing the window does not simply abandon whatever posters were mid-flight.
Each is a TMDB request that has already been made, so the app waits up to two
seconds for the answers to arrive and be written down, and only then cancels
what is left — the window disappears immediately either way, and the fetches
that did finish are not asked for again on the next launch.

A poster being cached is a promise that it is readable, because nothing ever
re-checks one: the file existing is the whole of the lookup from then on, and
both the cards and the details page decode straight from it. So a download is
written to a staging file beside its destination, checked for being an image at
all rather than an error page some proxy answered with, and only then moved into
place. Anything interrupted leaves nothing behind and is simply fetched again;
anything left by a process that was killed outright is cleared out an hour
later, once it is old enough to be certainly nobody's.

## Downloads

Released builds for Windows and macOS are listed at
[urdatabase-downloads.web.app](https://urdatabase-downloads.web.app), a static
page deployed to Firebase Hosting. Every asset is also on the
[releases page](https://github.com/larabail/UrDatabase/releases): the macOS
builds as `UrDatabase-<version>-<rid>.dmg`, Windows as
`UrDatabase-<version>-win-x64.zip`.

Once you are running a build, it tells you itself when a newer one exists: a
banner above the library, with **Update now** to fetch the right file for the
machine into your downloads folder and open it. Nothing installs itself — see
[Known gaps](#known-gaps) — so the last step is the same drag or unzip described
below. `"CheckForUpdates": false` in `appsettings.json` switches the check off.

### Opening it the first time

On a Mac, open the `.dmg` and drag **UrDatabase** to Applications. The build is
signed with an Apple Developer ID, notarized by Apple and stapled, so it opens
like any other application. There is no terminal command and nothing to clear.

On Windows, SmartScreen shows *"Windows protected your PC"*. Choose **More
info**, then **Run anyway**. That is a reputation check rather than a signature
one, and there is no Windows code signing certificate yet.

> [!NOTE]
> **Releases before 0.2.1 do not open on a current Mac.** They shipped a bare
> `UrDatabase.App` executable in a zip, ad-hoc signed and not notarized, and
> macOS kills such a process the moment it starts — no dialog, no error,
> nothing in the interface. This README used to offer
> `xattr -dr com.apple.quarantine` as the fix and that was wrong: macOS refuses
> the ad-hoc signature itself, so clearing the quarantine flag changes nothing.
> Take 0.2.1 or later.

## CI and releases

`Directory.Build.props` at the repository root holds a single `<Version>`, and
that one line is the source of truth for everything below. Do not put a version
in the `.csproj`.

- **On a pull request**, the workflows restore, build and test, then publish
  each runtime identifier and attach the results to the run. You can download
  and try a branch before it merges. Those builds are signed but deliberately
  not notarized, so the first launch needs a right-click → **Open**.
- **On a merge to `main`**, the version in `Directory.Build.props` is tagged
  `v<version>`, the TMDB, OMDb and UrActor keys are compiled in from the
  `TMDB_API_KEY`, `OMDB_API_KEY` and `URACTOR_API_KEY` repository secrets — the
  first two warn loudly when absent, the third does not, because a build with no
  awards is barely distinguishable from one where nothing on screen was ever
  nominated — the macOS builds are signed with a
  Developer ID and notarized, a GitHub Release is created with
  `UrDatabase-<version>-win-x64.zip`, `UrDatabase-<version>-osx-arm64.dmg` and
  `UrDatabase-<version>-osx-x64.dmg` attached, and the downloads site is
  deployed to Firebase Hosting.

Signing and notarizing need five more repository secrets —
`MACOS_DEVELOPER_ID_CERT_P12_BASE64`, `MACOS_DEVELOPER_ID_CERT_PASSWORD`,
`APP_STORE_CONNECT_KEY_ID`, `APP_STORE_CONNECT_ISSUER_ID` and
`APP_STORE_CONNECT_PRIVATE_KEY`. Without them a release still builds, and it
says so on the run, in its own release notes and on the downloads page — which
reads what actually happened out of the release rather than claiming anything
of its own. See
[docs/releases.md](docs/releases.md#signing-and-notarization).

Compiling the keys in at release is what makes a downloaded build work with no
setup. It is not a way of keeping them private, and nothing here pretends it
is; [SECURITY.md](SECURITY.md) sets out why that is an acceptable trade for
these three keys in particular and when it would not be.

Because a merge releases, a pull request that changes anything under `src/` has
to bump the version above whatever `main` carries at the moment the check runs
— not above whatever it carried when the branch opened — or the release will
collide with a tag that already exists. How far to bump is in
[AGENTS.md](AGENTS.md#versioning).

That check narrows the collision rather than closing it: two branches can still
take the same version, both go green and merge minutes apart, and the second
merge then ships nothing. So the release run *fails* when it finds code under
`src/` that has changed since the existing tag was published — that code is on
`main` and in no release, and the state used to report success, which is how it
went unnoticed twice. Clearing it is a pull request that raises `<Version>` and
nothing else; the release it triggers carries everything stranded since the last
tag.

Hosting is the only Firebase product involved, and only CI touches it: the
site is a few static files describing where to get the binaries. There is no
database, no authentication and no functions, and nothing in `src/` talks to
Firebase at all. The Firebase project is `actordb-cf981` and the hosting site
is `urdatabase-downloads`.

## Repository layout

```
src/UrDatabase.App/          the application: one cross-platform project
  Views/                     windows, screens and their code-behind
  Controls/                  reusable pieces, e.g. the poster card
  Styles/                    Tokens.axaml: every colour, face and metric, plus
                             the Fluent resources the theme would otherwise
                             paint in the system accent.
                             Theme.axaml: the shared control styles
  Models/                    what the views bind to
  Services/                  config, SQLite, scanning, search, TMDB, OMDb,
                             UrActor, Jellyfin, posters, playback reporting,
                             the update check
  Assets/UrDatabase.icns     the macOS application icon
  Data/schema.sql            the shape a database is created with; Database.Migrate
                             brings an older one up to it
  appsettings.example.json   configuration template, copied to the user's data
                             directory on first run; the real file is ignored
  UrDatabase.App.entitlements  hardened runtime exceptions the .NET JIT needs
tests/UrDatabase.Tests/      xUnit suite; TempLog is how a test class stays out
                             of the real log directory, and LogIsolation is what
                             stops it forgetting
tool/                        Python helpers with their own unittest suite:
                             the version-bump check, the release gate and the
                             macOS bundler
Directory.Build.props        the single <Version> for the whole solution
web/                         the downloads site served by Firebase Hosting
docs/                        design notes
scripts/                     local helper scripts, and the macOS signing script
.github/                     workflows, issue templates, the PR template
```

## Contributing

Read [AGENTS.md](AGENTS.md) first: it is the working agreement for humans and
agents alike, and it is short. The rules that catch people out are that `main`
is never committed to directly, that commits carry no `Co-authored-by` trailer,
and that a change under `src/` bumps the version.

`dotnet build` and `dotnet test` must both be clean before you open a pull
request.

### The suite never touches your install

Nothing under `tests/` may read or write the app's own data directory — it holds
somebody's catalogue, their poster cache and an `appsettings.json` with their
Jellyfin password and their API keys in it. That is not a convention here, it is
enforced: the test assembly calls `AppLog.ForbidRealDirectory()` before any test
runs, and from then on a log line that has not been pointed somewhere else
throws `UnredirectedLogWriteException` rather than reaching the filesystem.

It is enforced because asking people to remember did not work. `AppLog.Redirect`
had been available for a while and twelve test classes were still appending to a
real `jellyfin.log` on every run — not tests about logging, tests about uploads
and downloads and caches, whose subject happened to log on a failure path.

If you add a log line to a service and a test starts failing with that
exception, the test is the thing to fix, not the log line. Give the class a
`TempLog`:

```csharp
public class WhateverTests : IDisposable
{
    private readonly TempLog _log = new();

    public void Dispose() => _log.Dispose();
}
```

Put it on the class, not inside the single test you know logs today. The app
itself is unaffected: nothing in a shipped build arms the guard, so it logs to
the real directory exactly as it always has.

## Known gaps

Stated plainly, so nobody has to find out by using it:

- **A film TMDB cannot confirm has no poster until you pick one.** The automatic
  match refuses a result whose title or year does not corroborate the film, so a
  title TMDB spells differently, or one the filename parser mangled, now comes
  back with nothing rather than with the wrong film's artwork. The card stays
  blank until you open it and use **Wrong film?**. That is the trade: an empty
  frame invites the fix, and a confidently wrong one does not.
- **Correcting a match does not correct the year, and there is still no way to
  rename a film by hand.** Choosing the right TMDB film now renames it — a film
  catalogued as `S W A T` becomes `S.W.A.T.` in the library, in search and on the
  genre shelves — but the year on the card still comes from the filename, and a
  film TMDB does not have cannot be renamed at all, because the name has to come
  from a film you picked.
- **A scanned library has no genres.** Nothing writes the `genres` column for a
  scanned film yet, so every film from a scan lands in a single
  **Uncategorised** bucket, and a freshly scanned library looks bare until
  something fills genres in — which no code does. The **Offline**
  filter means those films are still one click away rather than buried behind a
  server library's genres, but they remain ungrouped. Films from a Jellyfin
  server are unaffected: the server supplies their genres, and a scanned film
  the server also has borrows them for as long as the two are shown as one card
  — the catalogue itself is not written to. A **programme** the server never
  identified has no genres either and shares that bucket, deliberately: both mean
  "nobody has said what this is", and the **Television** filter separates them
  again in a click.
- **A film in both places is matched by identity, then by name.** The server's
  copy and this computer's are shown as one card when they agree on a TMDB id,
  and otherwise when their titles agree once case, accents and punctuation are
  set aside and their years do not contradict each other. So two spellings that
  differ by a word — a translated title, or one the filename parser mangled —
  are folded together once both sides have identified the film, and stay two
  cards until they have. Correcting the local film's match with **Wrong film?**
  is what fixes that by hand. Nor does such a card offer the stream as a
  fallback: it plays the file on this disk, and says the server has it rather
  than doing anything with that.
- **Television is a Jellyfin feature only.** A server's series, seasons and
  episodes are browsable and playable, but nothing on this disk is. The filename
  parser has no concept of television, so `Show.S01E02.mkv` in a watch folder is
  still catalogued as an oddly titled film, and a local mixed library will look
  wrong rather than broken. Teaching the parser to recognise an episode is not
  enough on its own — a scanned episode needs a series to belong to, and the
  catalogue has no table for one — so it was left out rather than half done.
- **An episode cannot be downloaded.** **Download** keeps a copy of a server
  *film* so it plays with the server switched off; there is no equivalent for an
  episode, or for a season. Episodes stream or they do not play.
- **A programme is described only by the server.** There is no TMDB enrichment
  for television and no **Wrong film?** for a series, so a programme the server
  has not identified stays undescribed. TMDB's television catalogue is a separate
  one from its films, and using the film endpoints for it would be worse than
  using nothing.
- **A scanned film's badges are only as good as its filename.** A copy on this
  disk is described by its own name, so `Casablanca.mkv` gets no badges at all
  and a file whose name says `1080p` is badged `1080p` whatever is actually
  inside it. Nothing opens the container to check, and nothing reads a
  `.nfo` beside it. Only a Jellyfin film is measured. A film in both places is
  described by the copy on this disk and not by the server's, deliberately —
  Play opens the local file, and badging it with the server's 4K remux would
  describe a copy nobody is about to watch — so such a film can show fewer
  badges than the same film opened from the server.
- **The watch-next shelf needs films TMDB has identified.** It matches
  recommendations against the catalogue on `movies.tmdb_id`, which the poster
  loader writes for every film it can match — so a film with no poster usually
  has no shelf either, and neither has anything the automatic match refused. The
  genre fallback behind it needs genres, and a scanned library has none of those
  either, so on a purely local library that has never reached TMDB the shelf is
  simply absent. Nothing on it is ever a film you do not own, which is the one
  guarantee it does make.
- **A series has no awards panel.** The Academy does not give programmes Oscars,
  and the archive is searched by title, so a series is never asked about at all —
  a programme sharing a name with a film would otherwise be handed the film's
  nominations. Emmys are a different body with a different API and are not part
  of this.
- **Awards are matched on the Academy's own spelling of a title.** The archive
  matches exactly and case-sensitively, so a film catalogued under a translated
  title, a subtitle the Academy omitted, or a name the filename parser mangled
  has no awards as far as the app is concerned. There is no fuzzy match. What
  does fix it is **Wrong film?**, which renames the film to the one you picked —
  the awards are looked up again on the new name, so identifying a film corrects
  its Oscars along with its artwork. Until then the quiet failure is deliberate:
  attaching another film's Oscars would be worse than showing none.
- **Playback position is shared with the server through VLC, and only VLC.** A
  film or an episode streamed through VLC now reports where it got to, so it
  resumes and is marked watched on every device — but three cases still do not.
  **IINA reports nothing, and cannot resume**: it is mpv underneath and exposes
  a JSON IPC socket rather than an HTTP interface, which is a different protocol
  over a different transport, so an IINA user plays films exactly as before, sees
  **Play** rather than **Continue watching**, and contributes nothing to Continue
  watching. It cannot be told where to begin either — the argument that would do
  it is forwarded by `iina-cli` rather than by the application binary this app
  launches — so an episode played from the Continue watching row starts at the
  beginning and has to be seeked. Guessing at an argument is not a cosmetic risk:
  a player that refuses one does not play the film at all. **A downloaded film
  reports nothing and does not resume**, because it
  is opened with the system's default opener, which is not necessarily VLC, takes
  a path and nothing else, and may well be away from the server anyway — the
  position is simply not recorded, rather than queued for later delivery. And
  **the last few seconds are lost**: the position is read every two seconds and a
  player that goes away is noticed after six, so the stop is recorded up to about six seconds behind where the
  film actually reached. That is deliberate — a stop at nearly the right place
  beats no stop at all — but it means a film you quit at the very end may not tip
  over into "watched".
- **A dismissal from Continue watching cannot be reviewed, only undone or
  outlived.** Right-clicking a card takes it out of the row, and ⌘Z puts the last
  one back while the window is still open. After that the only way it returns is
  the position moving — watch any more of it, anywhere — because there is no
  screen listing what you have dismissed and no way to clear the list. That is
  the trade for a rule that expires by itself rather than a blacklist you have
  to maintain, but something abandoned at a position nothing will ever move is
  gone from the row for good, and the only record of it is a row in
  `jellyfin_resume_dismissals`.
- **One Jellyfin server.** There is no way to add a second. The setup screen
  configures the first one and tests it, but a household with two servers has to
  pick one.
- **Files are matched to films by heuristic when nothing is linked.** The scan
  records which film each file belongs to and Play uses only that, so the
  heuristic no longer decides what opens. It still decides what gets *offered*
  for a film with no link — a catalogue built before the scanner recorded one —
  and there it can still be wrong; it asks before opening anything, and declines
  to answer rather than guess between two equally good candidates.
- **A film that was missing stays in the catalogue, out of sight.** The library
  now takes such a film off the wall — it leaves entirely, or carries on as a
  server film if a server has it — but the row itself is never deleted, because
  nothing in the app can tell a film you deleted from one on a drive you
  unplugged. So a library you have churned through accumulates rows you cannot
  see, there is no screen that lists them or lets you clear them out, and the
  only way to bring one back is to put its file where a scan will find it or
  link a copy by hand. Two consequences worth knowing: an unplugged drive hides
  every film on it until it is plugged back in and rescanned, and a film that
  degraded to a server film degrades on the strength of the *cached* server
  library, so a server you have never synced on this machine cannot rescue
  anything. The one row that *is* deleted is the empty duplicate a rename can
  leave behind, which is a different thing and worth nothing — see
  [The row a rename can leave behind](#the-row-a-rename-can-leave-behind).
- **Nothing else in the app edits the catalogue.** There is no way to delete a
  film, merge two of them, or correct a year by hand. The sweep above is
  deliberately narrow — it will not touch a duplicate that has a file, a poster
  or a TMDB id, which is exactly the duplicate you would most want to merge —
  so two rows for one film, arrived at any other way, stay two rows.
- **A film that has left the library cannot be pointed at a new file from
  inside the app.** **Link File…** is on the details screen, and a film with no
  copy here either has no card to open or opens as a server film, which does not
  offer it. So the way back is a scan: put the file somewhere a watch folder
  covers and press the scan button. Moving a film permanently outside every
  watch folder still means adding that folder to the list.
- **A film renamed in place is a deletion and an addition.** Move detection
  matches on filename and byte count, so a file that keeps its path but changes
  its name is not followed: the old row is marked missing and the new file
  arrives as a new one. If nothing else claims the film, that reads as the film
  leaving the library and a differently named one appearing beside it in the
  same scan.
- **A moved film is followed by name and size, and only those.** A file that
  turns up somewhere new is treated as one that moved when exactly one missing
  row has the same filename and the same byte count, and the old path is really
  gone. Rename a film as well as moving it and the scan sees a deletion and an
  addition instead. Two files that share a name and a size are not guessed
  between at all: both are left as they are, which loses a link rather than
  attaching one to the wrong film.
- **Two prints of one film, and the app picks.** When several linked files
  survive, Play opens the largest, then the most recently written, then the first
  by path. That is a guess at which is the better copy, not a preference you can
  set.
- **A linked path is trusted to be where you said it was.** A file is only
  accepted, and only opened, when its extension is one the scanner recognises,
  and that is checked again immediately before launching. What is *not* checked
  is where the path points: nothing confines it to your watch folders or resolves
  it through symlinks first, so a catalogue you did not write yourself is worth
  the same suspicion as any other file it hands you.
- **Settings covers where your films are, and nothing else.** The screen asks
  about watch folders, a Jellyfin server and the three API keys. `DatabasePath`,
  `PosterCacheDir`, `DownloadFolder`, `JellyfinSftp`, `DownloadPosters` and
  `TmdbImageSize` are still file-only; they survive a save untouched, but nothing
  in the app edits them.
- **Downloads are one at a time, from the details screen.** There is no queue,
  no way to fetch a whole genre, and leaving the film stops the transfer —
  though what it got is kept and starting again resumes from there. Nothing in
  the app deletes a download either: that is Finder's job.
- **Uploads are one at a time, do not resume, and need an SFTP account.** The  same shape as downloads — one film, from the details screen, stopped by leaving
  it — with two differences. There is no resume: a stopped or dropped upload
  removes what it had transferred and starting again sends the film from the
  beginning, because verifying which of the bytes already on the server are the
  right ones would mean reading them all back, and a guess there produces a
  corrupt film rather than a slow one. And it needs something most Jellyfin
  installs do not come with: an SSH account on the machine running the server,
  key-only, with write access to the library. Jellyfin's own API takes images and
  subtitles and no other kind of file, so there is no route that needs only a
  Jellyfin login. Series are not supported either, here or anywhere else in the
  app.
- **An uploaded film does not appear in the app until the next Jellyfin sync.**
  The server is asked to rescan and does so on its own schedule; this app then
  has to be told, by **Sync Jellyfin**, before the film shows as being in both
  places. Pressing it immediately is usually too early. Nothing polls, and
  nothing deletes a film from the server either — that is the server's job.
- **Host key checking reads `~/.ssh/known_hosts` and understands most of it, not
  all of it.** Plain and hashed entries, the bracketed `[host]:port` form,
  comma-separated patterns with wildcards and negations, several keys per host
  and `@revoked` are all handled. `@cert-authority` is not: validating one means
  validating a certificate, so a host vouched for only by a CA is refused with a
  message saying exactly that rather than being reported as unknown. There is no
  setting for the file's location and no way to trust a key from inside the app —
  a host with no entry is refused, and adding one is a step you take with
  `ssh-keyscan` or by connecting once with `sftp`.
- **Windows builds are not signed.** SmartScreen warns on first run and there
  is no way around it short of a Windows code signing certificate. The macOS
  side of this closed in 0.2.1; the Windows side has not.
- **The app tells you about an update and fetches it; it does not install it.**
  **Update now** downloads the right build for the machine and opens it, and
  there it stops: you still drag UrDatabase into Applications or unzip it over
  the old copy, and you still quit the running app first. Replacing itself is not
  something either platform lets a running application do — a macOS bundle that
  rewrites its own contents invalidates the signature Gatekeeper checks, and a
  Windows build cannot overwrite the files its own process has open — so a real
  installer would be a second program that outlives this one, launched on the way
  out. That is a larger thing than a banner, and doing half of it would mean an
  update that sometimes leaves an app that will not start. Nothing verifies the
  download beyond its length either. The release does publish a
  `SHA256SUMS.txt`, but it comes from the same server over the same connection as
  the build, so checking one against the other would prove only that the file
  arrived intact — which is what TLS is already for — and not that either file is
  what the release intended. Verification that meant anything would need a
  signature made with a key the app already trusted, and there is not one, so the
  guarantee is https and GitHub's own certificate, exactly as it would be in a
  browser.
- **Nothing on the library page is virtualised, so a very large library is slow
  to draw.** Every film on screen gets a real poster card, built up front,
  whether or not it has been scrolled to. Querying is no longer the expensive
  part — typing stays smooth on any size of library — but drawing is: around two
  thousand films the shelves take about half a second to lay out, and at ten
  thousand and up the window becomes unusable and the process grows to several
  gigabytes. A search narrow enough to be useful is unaffected. Fixing it means
  virtualising the shelves and the result list, which is a change to the view.

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
