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

The application is a local desktop app with no server, no account and no
telemetry. It contacts TMDB and OMDb and nothing else — in particular it does
not talk to Firebase, which appears in this project only as the host CI deploys
the downloads site to. Nothing of yours is uploaded anywhere, so the
interesting surface is narrower than it is for most projects and worth naming
directly:

- **What it does with your disk.** Scanning walks the folders you configure and
  writes their paths into a SQLite file. Opening a film hands a path to the
  operating system to launch. A path or filename that escapes either of those
  intentions is a real finding.
- **What it does with the network.** It sends a title to TMDB and an IMDb id to
  OMDb, and renders what comes back. Anything in either response that can do
  more than be displayed is a real finding.
- **What CI ships.** The release workflow produces the archives people
  download. A change to it that could place unintended content in a release, or
  read a secret it has no business reading, is a real finding.

Out of scope: findings against TMDB, OMDb, Firebase or GitHub themselves, and
reports produced by a scanner without a demonstrated impact on UrDatabase.

## What is not a vulnerability

**The API keys inside an official build.** Released archives have the TMDB and
OMDb keys compiled into them, and **a key compiled into a desktop binary is not
secret**. Anyone holding a build can extract it. There is no server to keep it
behind, so this is not a defect to be fixed by obfuscation, and reporting that
you pulled a key out of a release tells us nothing we have not written here.

The keys live in the `TMDB_API_KEY` and `OMDB_API_KEY` repository secrets for
two reasons, neither of which is that the shipped value stays private: to keep
them out of the repository and its history, and to make rotating one a change
to a single setting rather than a change to the source. If you are rotating
either, those two secrets are the only place the value needs editing.

What makes that trade acceptable is the specific keys involved. Both are free,
read-only metadata credentials. The worst an abuser achieves is exhausting a
quota, at which point posters and ratings stop appearing until it is rotated —
irritating, cheap to fix, and costing no user anything of theirs. A key that
could write, spend money or reach personal data would not be shipped this way,
and if one is ever needed it belongs behind a server rather than inside the
app. That reasoning, rather than the conclusion, is the part worth carrying to
the next key.

Anyone who would rather not share the shipped quota can supply their own key
in `appsettings.json` or in `URDATABASE_TMDB_API_KEY` /
`URDATABASE_OMDB_API_KEY`; both take precedence over the compiled-in value. A
build from source has no keys in it at all.

Reading `TMDB_API_KEY` or `OMDB_API_KEY` out of a workflow run therefore gains
an attacker nothing a published archive would not.

The third secret is a different matter. `FIREBASE_SERVICE_ACCOUNT` is used only
to deploy the downloads site and never enters a build, so unlike the other two
it is genuinely private and nothing that ships contains it. Anything that could
expose it — a workflow readable by a fork, a step that echoes it, a change that
carries it into an artifact — is a real finding and worth reporting, as is any
workflow change that could place unintended content into a release.

The same applies, more sharply, to the five macOS signing secrets added in
0.2.1: `MACOS_DEVELOPER_ID_CERT_P12_BASE64`,
`MACOS_DEVELOPER_ID_CERT_PASSWORD`, `APP_STORE_CONNECT_KEY_ID`,
`APP_STORE_CONNECT_ISSUER_ID` and `APP_STORE_CONNECT_PRIVATE_KEY`. Each can be
used to sign software as this developer, so a leak is worse than a leaked API
key by some distance — it would let somebody else's binary carry our identity
past Gatekeeper. They are imported into a keychain created for one workflow run
and deleted afterwards whether it succeeded or not, they are never written into
a build, and any change that could put one in a log, an artifact or a release
asset is a real finding.

**Unsigned Windows builds.** The Windows archive carries no Authenticode
signature, so SmartScreen warns on first run and a user cannot verify that a
download came from us. That is a real limitation recorded here rather than an
oversight: closing it needs a Windows code signing certificate this repository
does not have. The macOS builds *are* signed with a Developer ID and notarized
as of 0.2.1; releases before that are ad-hoc signed only and will not launch on
a current Mac at all.

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
