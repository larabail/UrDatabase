# Working in this repository

Instructions for agents and humans working on UrDatabase. Read this before
making any change.

The app is an Avalonia UI 11 desktop client on .NET 8, building for Windows and
macOS from one project, reading a local SQLite catalogue and enriching it from
TMDB. [README.md](README.md) covers what it does, how to run it and how it is
laid out; this file covers how work gets done.

## The rules

These are not style preferences. Breaking one of these means the change gets
sent back.

### Never commit to `main`

`main` is the release branch: every merge to it tags the version, publishes a
GitHub Release and deploys the downloads site. All work happens on a branch and
lands through a pull request.

Branch names are `kind/short-description`, using the same kinds as the commit
convention below — `feat/genre-filter`, `fix/poster-cache-path`,
`ci/macos-publish`, `docs/contributing`.

### Never add a `Co-authored-by` trailer

Commits must not carry `Co-authored-by:` lines, and in particular must not
attribute anything to Copilot. If your tooling adds one by default, strip it
before committing. This applies to squash-merge commit bodies too.

### New code comes with tests

Any new behaviour needs a test, and any bug fix needs a test that fails before
the fix and passes after it. Writing the failing test first is what proves you
found the bug rather than something adjacent to it. `dotnet build` and
`dotnet test` must both be clean before you open a pull request.

The suite is xUnit, in `tests/UrDatabase.Tests/`. Getting code under it is
mostly a matter of where you put the logic: anything reachable only from a
window's code-behind cannot be tested without a UI thread, so put the rule in a
service or a plain class and let the view call it. Parsing a filename, deciding
which genre bucket a film lands in, building a TMDB image URL — those belong in
`Services/` or `Models/`, not in an event handler.

`TmdbService` takes an optional `HttpMessageHandler`, which is the seam for
testing it, and anything else that talks to TMDB or OMDb needs the same. Use
it. A test that reaches a live metadata API is a test that fails on a rate
limit, on a plane, and in CI without a key.

SQLite is cheap to test against for real: open a database in a temporary file,
run the schema, assert on rows. Prefer that to mocking the data access.

### Keep `README.md` current

`README.md` is the first map for a new contributor. If a change alters setup
steps, commands, architecture, CI behaviour, or user-facing features, update
the README in the same pull request. Do not leave the next person to discover
the new truth by reading the diff, a workflow log, or the source.

Its [Known gaps](README.md#known-gaps) section is part of that bargain. Close
one of those gaps and the entry comes out in the same pull request.

### Never commit an API key

Two keys are involved: TMDB's, for search, posters and details, and OMDb's, for
the IMDb rating. At runtime each is resolved from the `TmdbApiKey` and
`OmdbApiKey` fields of `appsettings.json`, then from the
`URDATABASE_TMDB_API_KEY` and `URDATABASE_OMDB_API_KEY` environment variables,
then from whatever was compiled in. `appsettings.json` is gitignored precisely
so a key cannot be committed by accident. What is tracked is
`src/UrDatabase.App/appsettings.example.json`, which holds placeholders and
nothing else. Adding a setting means adding it to the example file, with an
empty or obviously fake value.

Only official release builds have anything compiled in, and it is CI that puts
it there, from the `TMDB_API_KEY` and `OMDB_API_KEY` repository secrets, at
release time. Your build has no keys in it. That distinction is the rule: a key
belongs in one of those secrets or in your own ignored config file, and nowhere
else. Never hardcode one as a default, a fallback or a constant "just to get it
working" — that commits it, and the release workflow already handles the
shipped case.

Both keys are optional and neither is needed to build or test. Keep it that
way: a test or a build step that fails without a key turns every fresh clone
red and pushes the next person towards pasting one somewhere it will be
committed.

Never edit the example file to hold a working key "just for a minute", and
never paste one into a test, an issue or a commit message. A workflow that
needs a key reads it from `TMDB_API_KEY` or `OMDB_API_KEY` rather than
introducing a second copy anywhere. The repository's third secret,
`FIREBASE_SERVICE_ACCOUNT`, deploys the downloads site and must never reach a
build or an artifact — it is the one secret here that is actually secret.

A key that reaches a desktop build is not private in any case — there is no
server to keep it behind, so anyone holding the build can read it out. That is
a deliberate trade for these two, and [SECURITY.md](SECURITY.md) explains when
it stops being an acceptable one. The rule here is narrower: a key committed to
this repository stays in its history forever and has to be rotated. That has
already happened once.

The same goes for your own data: `movies.db`, poster caches and absolute paths
to your film folders are yours and are not repository content. `.gitignore`
covers the obvious cases; check `git status` before you stage.

## Commit convention

Conventional Commits, with a body that explains the reasoning.

```
kind(scope): imperative summary in lower case

Why this change was needed, and what was wrong before.

What the change does about it, and any consequence a reader would not guess.
Note anything deliberately left undone.
```

Kinds in use: `feat`, `fix`, `refactor`, `test`, `docs`, `ci`, `build`,
`chore`. The scope is the area touched — `scan`, `tmdb`, `omdb`, `posters`,
`search`, `db`, `ui`, `config`. It is optional when a change is genuinely
repo-wide.

The summary line stays under about 72 characters, is imperative ("add", not
"added" or "adds"), and has no trailing full stop.

The body is the part that matters. Explain the problem before the solution. A
diff shows what changed; only the message can say why, and why the alternatives
were worse. Record the tradeoffs you made and anything you chose not to do —
that is what saves the next person from repeating your dead ends.

Do not describe the change as a list of edited files. Do not write "as
requested". Do not mention the agent, the model, or the conversation.

## Versioning

`Directory.Build.props` at the repository root holds a single `<Version>`, and
it is the only place a version number is allowed to live. Every project in the
solution inherits it, the release tag is `v<version>`, the macOS bundle takes
its `CFBundleShortVersionString` from it, and the release assets are named
`UrDatabase-<version>-<rid>.dmg` on macOS and
`UrDatabase-<version>-win-x64.zip` on Windows. Never add a `<Version>` to a
`.csproj`; it would silently win over the shared one for that project alone and
produce a release whose downloads disagree with their own tag.

How far to bump follows directly from the commit kind:

| The change is | Kind | Bump |
| --- | --- | --- |
| Something users could not do before | `feat` | MINOR — `0.1.0` to `0.2.0` |
| Something that was broken now works | `fix`, `perf` | PATCH — `0.1.0` to `0.1.1` |
| Something users must relearn, redo, or lose | any kind with `!` | see below |
| Nothing a user would notice | `docs`, `ci`, `test`, `chore`, `refactor`, `build` | none |

A minor bump resets the patch to zero, and a major resets both. `0.2.4` after
`0.1.4` reads as four patches that never happened.

While the version is below `1.0.0` a breaking change takes a MINOR bump, not a
MAJOR one. `1.0.0` is a statement that the app is finished enough to promise
something, and it is the owner's to make, not a consequence of arithmetic. Mark
the break anyway — put `!` before the colon
(`feat(config)!: move the database path out of appsettings`) or add a
`BREAKING CHANGE:` footer — so the release notes say so even though the number
does not.

Bumping further than required is always allowed; nothing overrules a person who
decides a release deserves more.

This matters because every merge to `main` releases. Without a bump the release
workflow tries to create a tag that already exists, and if it did not fail, two
different builds would reach users under one version and no bug report could be
tied to a revision.

A pull request that touches nothing under `src/` needs no bump. Documentation,
workflows, the downloads site and the test project change no shipped binary.
The commit is still honestly a `docs` or a `ci`, and bumping anyway is allowed.

One consequence: a long-lived branch will conflict on the `<Version>` line once
`main` has moved. Resolve it by taking the higher of the two and applying your
own bump on top of it.

## Pull requests

### Title

Same shape as a commit summary: `kind(scope): imperative summary`. The title
becomes the squash-merge commit subject, so it has to stand on its own in
`git log`.

```
feat(search): fall back to a LIKE query when the FTS index is absent
fix(posters): expand the cache path before creating the directory
ci: publish osx-arm64 alongside osx-x64
```

Never open a pull request titled `Update MainWindow.cs`, `changes`, or `WIP`.

### Body

Fill in `.github/pull_request_template.md`. The checklist is not decoration:
tests, a clean build, the version bump, no `Co-authored-by`, no secret.

### Checks

The workflows in `.github/workflows/` build, test and publish every runtime
identifier on each pull request, and attach the archives to the run so a
reviewer can try the change before merging. They do not release. A red pull
request does not get merged; fix it rather than merging around it.

That last sentence is enforced rather than encouraged. `main` is a protected
branch, so a pull request is the only way into it, and four checks have to be
green before the merge button works:

```
Build and test    Test builds    Version    Downloads site
```

Those names are matched literally. Renaming a job in `pr.yml` does not rename
the requirement — it silently stops that job being required, and the rule it
was enforcing quietly stops applying while everything still looks green. If you
rename one, update the protection rule in the same breath.

Three more things the rule does, in rough order of how often they catch people:

- **Your branch has to be up to date with `main` before it can merge.** A
  branch that has sat while `main` moved shows as behind and the button stays
  down until you merge `main` into it, which re-runs the checks.
- **A review from a code owner is required**, and `.github/CODEOWNERS` makes
  that the owner for every path. Pushing after an approval dismisses it.
- **Every conversation on the pull request has to be resolved.**

Administrators can bypass all of it. That is a deliberate escape hatch for the
day a required check is wedged, not an invitation — the rule against committing
to `main` is a rule for the owner too, and the only thing enforcing it there is
the owner.

## Before you open a pull request

```bash
dotnet build                     # must be clean
dotnet test                      # must be green
dotnet run --project src/UrDatabase.App
```

Run the app at least once. The suite cannot see a binding that silently
resolves to nothing or a window that opens blank, and Avalonia reports both at
runtime rather than at compile time.

## Things that will waste your time

- **The history of this repository was rewritten** in 2026 to remove a leaked
  TMDB key. Commits before that point have new hashes. An old clone will not
  fast-forward; re-clone rather than trying to merge it back together, and
  never push a branch based on the pre-rewrite history.
- **`appsettings.json` is ignored, and it is also what the app reads.** A
  setting you add to the example file and forget to add to your own local copy
  simply takes its default at runtime, with no error to tell you why.
- **Paths in configuration are expanded with environment variables**, so
  `%APPDATA%\UrDatabase` resolves on Windows and stays a literal string on
  macOS. Anything cross-platform has to come from
  `Environment.SpecialFolder`, not from a hardcoded `%VAR%`.
- **`AppConfig.Load` swallows every exception** and returns defaults. Malformed
  JSON does not raise; the app just behaves as though you configured nothing.
  Check that your file parses before hunting for a bug elsewhere.
- **A scanned library has no genres.** Scanning writes a title and a year and
  nothing else, so every film lands in the `Uncategorised` bucket until
  something fills the `genres` column in. Nothing does yet. An empty-looking
  grouped view after a successful scan is this, not a bug in the grouping.
