# UrDatabase

A desktop app for cataloguing a film collection. Point it at your folders and
scan: it reads the titles out of your filenames, builds a local SQLite
catalogue, lays it out as poster art grouped by genre, and fills in the posters,
plot, runtime, cast and crew from [TMDB](https://www.themoviedb.org/) as you
look at them, with the IMDb rating fetched from
[OMDb](https://www.omdbapi.com/). If your films live on a
[Jellyfin](https://jellyfin.org/) server instead of on this disk, point it at
that too and browse and play them from the same window. Nothing of yours leaves
the machine: the only things sent out are the title being looked up, its IMDb id
for the rating, and whatever your own server is asked for.

It runs on Windows and macOS from one codebase, built with
[Avalonia UI 11](https://avaloniaui.net/) on .NET 8.

## Features

- **Set up on first launch.** A fresh install opens a setup screen instead of an
  empty library: tick folders on this computer, a Jellyfin server, or both, test
  the server before committing to it, and optionally paste your TMDB and OMDb
  keys. It writes `appsettings.json` for you and never appears again — the
  **Settings** button reopens the same screen, and anything saved there is
  applied to the running window rather than at the next launch
  (`Views/SetupWindow`, `Models/SetupChoices`, `Services/ConfigStore`).
- **Browse by genre.** The library opens as rows of poster cards, one row per
  genre, newest first within each row, each shelf headed by the genre and the
  number of films on it. The genre row across the top carries those counts too,
  and picking one narrows the whole view to that genre. `Cmd+F` — `Ctrl+F` on
  Windows — puts the cursor in the search field, and the field says so
  (`Views/MainWindow`).
- **Filter by where a film is.** When the library draws on both this computer
  and a server, a row above the genres offers **Everywhere**, **Offline** and
  **On the server**, each with a count. Genre and location are different
  questions: a scanned film has no genre until something enriches it, so without
  this every local film sat in the Uncategorised bucket, which sorts behind every
  genre a server library brings with it. A film in both places answers to both
  controls, so the counts deliberately do not add up to the total. The row is
  hidden entirely when everything comes from one place, or when every film is in
  both (`Services/LibraryFilter`).
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
  on disk and the next scan would catalogue it a second time
  (`Views/TmdbMatchWindow`, `Services/MovieMatch`, `Services/MovieIndex`).
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
- **Play.** The details window opens the file the catalogue links to this film —
  the link the scan wrote, not a guess at the filename — and hands it to whatever
  the operating system uses. A film with no link, or whose only linked copy has
  been moved or deleted, does not silently fall back to whichever file looks
  closest: if something unclaimed on disk resembles the title it is offered by
  name and Play asks first, and otherwise the window says plainly that nothing is
  linked. Linking a file by hand from the file picker settles it, and is
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
  with the network down. The server describes its own films, cast and crew
  included, so a
  Jellyfin library is complete without a TMDB key. The library is cached in SQLite, so the window opens instantly
  and stays browsable with the server switched off or the laptop away from home
  — the films simply cannot play until it is reachable again. Playing one
  streams it, without transcoding, through VLC or IINA
  (`Services/JellyfinClient`, `Services/JellyfinCache`,
  `Services/JellyfinLibrary`, `Services/MediaPlayerLauncher`).
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
| Jellyfin API | Optional: a remote movie library, its artwork and its stream |
| SSH.NET | Optional: the SFTP transfer that puts a film on the server's disk, which Jellyfin's API cannot do |

There is no server of ours, no account and no telemetry, and the app touches no
Firebase: the only outbound traffic is to `api.themoviedb.org`,
`image.tmdb.org`, `www.omdbapi.com` and, if you configure one, your own Jellyfin
server — over HTTP for the library, and over SSH to its machine if you configure
uploading. It works fully offline, with metadata and ratings simply absent.

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
| `PosterCacheDir` | Where downloaded posters go |
| `DownloadFolder` | Where a film downloaded from Jellyfin is saved. Defaults to a `UrDatabase` subfolder of the platform's film folder — inside what a scan already walks, so both halves of the library agree about it |
| `DownloadPosters` | `false` points the UI at TMDB's own image URLs; `true` caches each poster to disk |
| `TmdbImageSize` | TMDB's poster width — `w185`, `w342`, `w500`, `original` |
| `SetupCompleted` | Set by the setup screen once it has been answered, and the only thing that stops it being offered again |
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

### When setup appears

Only on an install that has never been configured: no `appsettings.json` of its
own, no catalogue on disk, and no record of the screen having been answered
before. An install predating this screen has at least one of those and goes
straight to the library, as it always did. Skipping is an answer too — it goes
straight to the library and does not ask again.

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

### A Jellyfin server

Entirely optional, and off unless you fill it in. Leave `ServerUrl` empty — as
it ships — and the app makes no request, opens no extra panel and behaves
exactly as it did before this existed.

The easiest way to fill it in is the setup screen, which has a **Test
connection** button: it signs in, finds the movie library and reports how many
films are in it, so a wrong address, a wrong password and a server with no movie
library are told apart before anything is saved. The same four fields can be
written by hand instead:

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
| `LibraryName` | Which movie library to read, when a server has more than one |

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

The rating badge on a server film says `Jellyfin` when it is Jellyfin's own
community rating, and `IMDb` only for the IMDb rating from OMDb. They are
different numbers from different populations and are never shown under each
other's name.

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
`jellyfin_movies` cache, the `movies_fts` FTS5 index and the triggers that keep
it current — and every statement is `IF NOT EXISTS`, so it runs against a
library you already have without touching your data.

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
  `v<version>`, the TMDB and OMDb keys are compiled in from the `TMDB_API_KEY`
  and `OMDB_API_KEY` repository secrets, the macOS builds are signed with a
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
these two keys in particular and when it would not be.

Because a merge releases, a pull request that changes anything under `src/` has
to bump the version above whatever `main` carries at the moment the check runs
— not above whatever it carried when the branch opened — or the release will
collide with a tag that already exists. How far to bump is in
[AGENTS.md](AGENTS.md#versioning).

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
                             Jellyfin, posters
  Assets/UrDatabase.icns     the macOS application icon
  Data/schema.sql            the shape a database is created with; Database.Migrate
                             brings an older one up to it
  appsettings.example.json   configuration template, copied to the user's data
                             directory on first run; the real file is ignored
  UrDatabase.App.entitlements  hardened runtime exceptions the .NET JIT needs
tests/UrDatabase.Tests/      xUnit suite
tool/                        Python helpers with their own unittest suite:
                             the version-bump check and the macOS bundler
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
  — the catalogue itself is not written to.
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
- **Films only.** The filename parser has no concept of television, so
  `Show.S01E02` becomes an oddly titled film rather than an episode. A mixed
  library will look wrong rather than broken. A Jellyfin server's series
  libraries are skipped outright for the same reason.
- **Playback position is not shared with the server.** A film played from
  Jellyfin does not resume where you left off and is not marked watched, because
  the app hands the stream to an external player and never hears from it again.
- **One Jellyfin server.** There is no way to add a second. The setup screen
  configures the first one and tests it, but a household with two servers has to
  pick one.
- **Files are matched to films by heuristic when nothing is linked.** The scan
  records which film each file belongs to and Play uses only that, so the
  heuristic no longer decides what opens. It still decides what gets *offered*
  for a film with no link — a catalogue built before the scanner recorded one —
  and there it can still be wrong; it asks before opening anything, and declines
  to answer rather than guess between two equally good candidates.
- **A film that was missing stays in the catalogue until you say otherwise.**
  A scan marks a file it could not find and never removes it, because nothing
  in the app can tell a film you deleted from one on a drive you unplugged.
  Nothing yet prunes a row that has been missing across many scans, and there is
  no screen that lists the missing ones or lets you clear them, so for now the
  mark is a fact recorded in the database rather than anything you can see or
  act on.
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
  about watch folders, a Jellyfin server and the two API keys. `DatabasePath`,
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
