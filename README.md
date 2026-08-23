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
  genre, newest first within each row. Genre chips across the top narrow the
  view to a single genre (`Views/MainWindow`).
- **Search.** Typing in the search box queries the `movies_fts` full-text index
  and replaces the grouped view with a flat, ranked list of hits. Films from a
  Jellyfin server are matched alongside them, on title and genre.
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
- **Browse a Jellyfin server.** Optional, off until you configure it. Point the
  app at a server and its movie library appears alongside your local one, with
  every server film badged **Server** so you can tell at a glance what is not on
  this machine. The library is cached in SQLite, so the window opens instantly
  and stays browsable with the server switched off or the laptop away from home
  — the films simply cannot play until it is reachable again. Playing one
  streams it, without transcoding, through VLC or IINA
  (`Services/JellyfinClient`, `Services/JellyfinCache`,
  `Services/MediaPlayerLauncher`).
- **Download a server film to watch offline.** A film on the server has a
  **Download** button that keeps a copy on this disk, named the way the scanner
  reads it and catalogued the moment it finishes — so it is playable and
  searchable without waiting for a scan. Afterwards **Play** opens the local
  copy rather than the stream, which is the whole point: it works on a train.
  A transfer can be stopped and resumes where it left off, and a half-finished
  film is never mistaken for a whole one
  (`Services/JellyfinDownloader`, `Services/JellyfinDownload`).

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

There is no server of ours, no account and no telemetry, and the app touches no
Firebase: the only outbound traffic is to `api.themoviedb.org`,
`image.tmdb.org`, `www.omdbapi.com` and, if you configure one, your own Jellyfin
server. It works fully offline, with metadata and ratings simply absent.

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

Paths may contain environment variables and are expanded on load, so
`%APPDATA%\UrDatabase\movies.db` works on Windows. The application data
directory .NET reports is `%APPDATA%` on Windows and
`~/Library/Application Support` on macOS, so a configuration file written on one
is not portable to the other.

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
duplicate. Afterwards **Play** opens the local copy rather than the stream, and
the film keeps working with the server switched off.

### The catalogue

Point `DatabasePath` anywhere and the app creates what it needs on first
launch. `src/UrDatabase.App/Data/schema.sql` is the full schema — the `movies`
and `files` tables, the `jellyfin_movies` cache, the `movies_fts` FTS5 index and
the triggers that keep it current — and every statement is `IF NOT EXISTS`, so
it runs against a library you already have without touching your data.

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
  Services/                  config, SQLite, scanning, TMDB, OMDb, Jellyfin, posters
  Assets/UrDatabase.icns     the macOS application icon
  Data/schema.sql            the full schema, applied on first launch
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

- **A scanned library has no genres.** Nothing writes the `genres` column for a
  scanned film yet, so every film from a scan lands in a single
  **Uncategorised** bucket. Since the grouped view is the main way to browse, a
  freshly scanned library looks bare until TMDB enrichment fills genres in — and
  no code does that yet. Films from a Jellyfin server are unaffected: the server
  supplies their genres.
- **Films only.** The filename parser has no concept of television, so
  `Show.S01E02` becomes an oddly titled film rather than an episode. A mixed
  library will look wrong rather than broken. A Jellyfin server's series
  libraries are skipped outright for the same reason.
- **A server film has no cast or crew.** The details window fills those from
  TMDB, and a Jellyfin film deliberately makes no TMDB call, so both lists are
  empty for one. Jellyfin can report them; nothing asks yet.
- **Playback position is not shared with the server.** A film played from
  Jellyfin does not resume where you left off and is not marked watched, because
  the app hands the stream to an external player and never hears from it again.
- **One Jellyfin server.** There is no way to add a second. The setup screen
  configures the first one and tests it, but a household with two servers has to
  pick one.
- **Files are matched to films by heuristic.** An exact filename stem wins,
  otherwise the first name containing the title. Two films whose titles are
  substrings of each other can still be confused.
- **Linking a file by hand does not persist.** The file picker updates the open
  window and nothing else; reopening the film forgets it.
- **Settings covers where your films are, and nothing else.** The screen asks
  about watch folders, a Jellyfin server and the two API keys. `DatabasePath`,
  `PosterCacheDir`, `DownloadFolder`, `DownloadPosters` and `TmdbImageSize` are
  still file-only; they survive a save untouched, but nothing in the app edits
  them.
- **A downloaded film appears twice.** The copy on this disk and the film on the
  server are both shown, one badged **Server**, because the merge deduplicates
  by identity rather than by title and hiding either would mean hiding the only
  one that works in some situation. It is correct, and it does look odd.
- **Downloads are one at a time, from the details window.** There is no queue,
  no way to fetch a whole genre, and closing the window stops the transfer —
  though what it got is kept and starting again resumes from there. Nothing in
  the app deletes a download either: that is Finder's job.
- **Nothing is uploaded.** Films go from the server to this machine and never
  the other way. Jellyfin's API has no endpoint that accepts a video, so putting
  a film on a server means copying it to the server's own disk by some other
  means and rescanning the library there.
- **Windows builds are not signed.** SmartScreen warns on first run and there
  is no way around it short of a Windows code signing certificate. The macOS
  side of this closed in 0.2.1; the Windows side has not.

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
