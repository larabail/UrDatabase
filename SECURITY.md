# Security

## Reporting a vulnerability

Report privately. Do not open a public issue, and do not include a working
exploit in anything publicly visible.

Use [private vulnerability reporting][advisories] on this repository, which goes
to the maintainer and stays private until a fix ships.

[advisories]: https://github.com/larabail/UrDatabase/security/advisories/new

Please include what you can: what an attacker gains, the steps to reproduce it,
the operating system, and the affected version or commit. A report that shows
the impact is far more useful than one that only names a weakness.

Expect an acknowledgement within a week. You will be told when a fix lands, and
credited if you would like to be.

## Scope

This repository holds the whole of UrDatabase: the Avalonia application in
`src/`, its tests, the workflows in `.github/workflows/` that build and publish
release archives, and the static downloads site in `web/`. Reports about any of
those are in scope.

The application is a local desktop app with no server and no account. There is
nothing to authenticate to and nothing of yours is uploaded anywhere, so the
interesting surface is narrower than it is for most projects and worth naming
directly:

- **What it does with your disk.** Scanning walks the folders you configure and
  writes their paths into a SQLite file. Opening a film hands a path to the
  operating system to launch. A path or filename that escapes either of those
  intentions is a real finding.
- **What it does with the network.** It sends a title and a year to TMDB, and
  an IMDb id to OMDb, and renders what comes back. Anything in either response
  that can do more than be displayed is a real finding.
- **What CI ships.** The release workflow produces the archives people
  download. A change to it that could place unintended content in a release, or
  read a secret it has no business reading, is a real finding.

Out of scope: findings against TMDB, OMDb, Firebase Hosting or GitHub
themselves, and reports produced by a scanner without a demonstrated impact on
UrDatabase.

## What is not a vulnerability

**A user's own API key sitting on their machine.** UrDatabase talks to two
services: TMDB for search, posters and details, and OMDb for the IMDb rating.
It reads both keys at runtime, from `appsettings.json` beside the binary or
from the `URDATABASE_TMDB_API_KEY` and `URDATABASE_OMDB_API_KEY` environment
variables. Either way the key ends up readable on that machine.

This is the design rather than a gap in it. A desktop app has no server to keep
a key behind: anything the binary can reach, whoever holds the binary can
reach too, so a key shipped inside or alongside a build is recoverable by
definition. Rather than pretend otherwise, UrDatabase ships no key at all and
asks each user for their own. Both are public, rate-limited credentials, and
neither is needed to build the app or run its tests.

The one key this project genuinely keeps private is the `TMDB_API_KEY`
repository secret, which exists so CI can exercise the metadata paths. A way to
read it out of a workflow run, or to make a pull request from a fork read it,
*is* a vulnerability and is worth reporting.

**Unsigned macOS builds.** The macOS archives are not code-signed or notarized
yet, so Gatekeeper reports them as damaged or from an unidentified developer.
That warning is accurate and is not a bug. It does mean a user cannot verify a
download came from us, which is a known limitation recorded here rather than an
oversight; signing is intended.

**The catalogue itself.** `movies.db`, the poster cache and your film paths are
local files owned by your account and protected by your filesystem permissions.
The app does not encrypt them and does not claim to.

## History

This repository was private until 2026, and a TMDB API key was committed to it
during that time. Both halves of the problem were dealt with before the
repository was made public:

- The key was **rotated**, so the value that was committed is dead.
- The git history was **rewritten** to remove it, so the value is not reachable
  from any commit in this repository.

A rewrite cannot reach clones or forks that already existed, which is why
rotation came first and matters more. Reporting that key back is not necessary.
If you find a *different* one, or find the rotated one still live anywhere, that
is worth a report.

One consequence for contributors rather than for security: commits before the
rewrite have new hashes, so an old clone will not fast-forward onto `main`.
Re-clone it.
