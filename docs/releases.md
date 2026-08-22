# Releasing UrDatabase

How a version reaches somebody's computer, what makes one happen, and the
things a person still has to do by hand.

## The short version

1. Open a pull request. If it changes anything under `src/`, raise `<Version>`
   in `Directory.Build.props`.
2. Merge it.
3. That's it. `main` tags `v<version>`, builds all three downloads, publishes a
   GitHub release and records a deployment. The downloads site picks the new
   release up on its own, without being redeployed.

There is no separate "cut a release" step, and deliberately so: a manual step
after the merge is a step that gets forgotten, and when it is forgotten nothing
looks wrong — the code is merged and simply never reaches anybody.

## The version is one number in one file

`Directory.Build.props` at the repository root:

```xml
<Project>
  <PropertyGroup>
    <Version>0.1.0</Version>
  </PropertyGroup>
</Project>
```

That number is the only source of truth. Everything else is derived from it:

| Thing | Comes out as |
| --- | --- |
| Git tag | `v0.1.0` |
| GitHub release | `UrDatabase 0.1.0` |
| macOS, Apple silicon | `UrDatabase-0.1.0-osx-arm64.zip` |
| macOS, Intel | `UrDatabase-0.1.0-osx-x64.zip` |
| Windows, 64-bit | `UrDatabase-0.1.0-win-x64.zip` |
| Assembly version | `0.1.0` |

Nothing else needs editing to release, and nothing else should be edited
instead.

### When a bump is required

A pull request that changes **anything under `src/`** must raise `<Version>`
above the version currently on `main`. The `Version` check on every pull
request enforces it and explains what to do when it fails.

Anything else — `web/downloads/`, `docs/`, `tests/`, `.github/`, the root
Markdown files — requires no bump. None of it can change what a published build
contains, and demanding a version for a typo on a web page would publish a
release containing nothing a user could find.

How much to raise it by is a judgement call and nothing enforces it:

| Change | Bump |
| --- | --- |
| A fix | patch — `0.1.0` → `0.1.1` |
| A feature | minor — `0.1.0` → `0.2.0` |
| Something that breaks an existing catalogue or workflow | major — `0.1.0` → `1.0.0` |

The rule lives in `tool/check_version_bump.py`, which has its own tests. Run
them with:

```sh
python3 -m unittest discover -s tool -p "test_*.py"
```

### Why not moving the version does nothing

`release.yml` refuses to publish over an existing tag. That is what makes it
safe to run on every merge — a documentation change lands, the workflow sees
`v0.1.0` already tagged, writes a line saying so and exits green. It is also
why forgetting the bump means the merge ships nothing at all, silently. Hence
the check on the pull request.

## What runs, and when

### `pr.yml` — on every pull request to `main`

| Job | What it does |
| --- | --- |
| **Build and test** | Lints the workflows, restores, builds and runs `dotnet test` on the solution. Linux. |
| **Test builds** | Publishes all three runtime identifiers and uploads them as a downloadable artifact, so a reviewer can run the change without building it. macOS. |
| **Version** | Enforces the bump rule above. |
| **Downloads site** | Runs the downloads page's own tests. |

Those four names are the ones to set as **required status checks** on `main`.
Branch protection matches checks by name, so renaming a job silently stops it
being required.

The test builds are the point of the whole job: this is a desktop application,
and what it looks like and how it behaves are not visible in a diff. They are
in the **Artifacts** section at the bottom of the run page, as
`UrDatabase-<version>-builds`.

### `release.yml` — on every push to `main`

Reads the version, and stops immediately if there is nothing to do. Otherwise:
runs the tests again on the merged result, builds and zips all three runtime
identifiers, checksums them, pushes the annotated tag `v<version>`, opens a
GitHub Deployment, publishes the release with the three zips, `SHA256SUMS.txt`
and generated notes, then closes the deployment.

The deployment is why the repository has a **Deployments** entry per release:
it answers "what shipped and when" without reading workflow logs.

### `deploy-downloads.yml` — when the downloads site changes

Deploys `web/downloads/` to Firebase Hosting on pushes to `main` that touch
`web/downloads/`, `firebase.json`, `.firebaserc` or the workflow itself, and on
demand from the Actions tab.

Not tied to releases on purpose. The page reads the GitHub releases API in the
browser, so a release published five minutes ago appears on it without any
deploy. Tying the two together would mean the only way to fix a typo on a web
page is to publish a version of the application.

## Why the builds are made on macOS

Both `pr.yml`'s packaging job and `release.yml` run on `macos-14`, and that
cannot be changed without breaking the Mac downloads.

On Apple silicon the kernel refuses to execute an arm64 binary carrying no code
signature at all. Not a Gatekeeper prompt — an immediate kill, which a shell
reports as `Killed: 9` and Finder reports as nothing whatsoever. The .NET SDK
ad-hoc signs the macOS launcher, which satisfies that requirement, but only when
the build host is macOS; the condition in `Microsoft.NET.Sdk.targets` is
literally `IsOSPlatform(OSX) and Exists('/usr/bin/codesign')`.

So `dotnet publish -r osx-arm64` on a Linux runner exits 0 and produces a
download that is dead on arrival, with nothing in the logs saying so. The
packaging action verifies the signature with `codesign --verify` for exactly
that reason.

macOS runners are free on public repositories. On a private one they bill at ten
times the Linux rate, so making this repository private again has a cost
attached to it.

## The builds are not signed for distribution

Ad-hoc signing is enough to let a binary run. It is not a Developer ID, there is
no notarization, and both operating systems say so:

- **macOS** quarantines anything a browser downloaded and refuses to open an
  unsigned app at all, reporting it as *damaged*. It is not damaged. The fix is
  one command, which the downloads page and the release notes both spell out
  with the real filename in it:

  ```sh
  xattr -dr com.apple.quarantine ~/Downloads/UrDatabase-0.1.0-osx-arm64
  ```

- **Windows** shows *"Windows protected your PC"* from SmartScreen. **More
  info**, then **Run anyway**.

Fixing this properly means an Apple Developer account and a Windows code signing
certificate, both of which cost money annually. Until then, saying so plainly is
the honest option — a page that implies the download is signed sends people off
re-downloading a file that was never broken.

## Secrets

| Secret | Used by | Status |
| --- | --- | --- |
| `TMDB_API_KEY` | `pr.yml` test builds, `release.yml` | Set |
| `OMDB_API_KEY` | `pr.yml` test builds, `release.yml` | Set |
| `FIREBASE_SERVICE_ACCOUNT` | `deploy-downloads.yml` | **Missing — see below** |
| `GITHUB_TOKEN` | tagging, releases, deployments | Provided automatically |

### The two API keys are not secret once shipped

`TMDB_API_KEY` and `OMDB_API_KEY` are compiled into the published binaries so
that an official download works the moment it is opened, with no account to
create and no configuration file to write. **They can be extracted from any
shipped build by anybody who wants them.** That is inherent to the approach and
is not a mistake to be fixed by hiding them better.

They live in Actions secrets for two much smaller reasons: to keep them out of
the repository and its history, and to make rotating one an edit to a repository
setting rather than a commit. Rotating either means updating the secret **and
publishing a new version** — every build already out there keeps the old key
until it is replaced.

Consequences worth knowing:

- Pull requests **from forks cannot read secrets**. An outside contributor's
  test builds are produced with both keys empty and start with no metadata,
  posters or ratings. That is expected, it is not a failure, and nothing in the
  pipeline treats it as one. The build log says so, and so does the pull
  request's job summary.
- `dotnet test` is **never** given the keys. The test suite drives the TMDB and
  OMDb clients through fake HTTP handlers, so it must keep passing with both
  unset — that is what proves the fakes are actually in the path. Please do not
  "fix" this by adding them.
- Everything in this repository and in its workflow logs is world-readable. The
  keys are passed to MSBuild from `env:`, never interpolated into a `run:` line.

## Manual steps the owner still has to do

### 1. Restore `FIREBASE_SERVICE_ACCOUNT` — required before the site can deploy

The secret was lost when the repository was rebuilt, and a key cannot be read
back out of GitHub, so it has to be regenerated rather than recovered. Until it
exists, `deploy-downloads.yml` fails on its first step with a message naming
exactly this.

1. Firebase console → project `actordb-cf981` → **Project settings** →
   **Service accounts** → **Generate new private key**. A JSON file downloads.
2. `larabail/UrDatabase` → **Settings** → **Secrets and variables** →
   **Actions** → **New repository secret**.
3. Name: `FIREBASE_SERVICE_ACCOUNT`. Value: the entire contents of that JSON
   file, outer braces included.
4. Actions → **Deploy the downloads site** → **Run workflow**.

The workflow creates the Hosting site `urdatabase-downloads` itself on the first
successful run — a deploy fails outright against a site that does not exist, and
creating one is a different API call from deploying to it, so it is done in the
workflow rather than left as a step in a document nobody re-reads.

The site then answers at `https://urdatabase-downloads.web.app`. A custom domain,
if one is ever wanted, is connected in the Firebase console and needs DNS records
at the registrar; a deploy can succeed while a custom domain still says "Site Not
Found".

### 2. Set the required status checks on `main`

Settings → Branches → branch protection for `main` → **Require status checks to
pass before merging**, and select, by these exact names:

- `Build and test`
- `Test builds`
- `Version`
- `Downloads site`

Without this the version check is advisory, and a pull request can merge red.

### 3. Nothing else

No Firestore, no Cloud Functions, no emulators, no Firebase Authentication. The
application uses no Firebase at runtime at all; Hosting serves one static page
and that is the entire relationship.

## When something goes wrong

**A merge published nothing.** Almost always the version did not move. The
release run says so in its summary. Raise `<Version>` and merge again.

**The release failed halfway, and the tag exists.** Re-running the workflow
does nothing, because it sees the tag. Delete the tag and the partial release
on GitHub, then re-run — or, more simply, raise the version and release the next
one.

**A release shipped with no posters or ratings.** One of the API keys was empty
when it was built. The run logs a warning saying which. Set the secret and
publish a new version; the broken build cannot be repaired in place because the
key is compiled in.

**The downloads page shows nothing.** It reads the GitHub releases API from the
browser, which allows sixty unauthenticated requests an hour per address. The
page says so when it happens and still links to the releases page. It is not a
deploy problem and redeploying will not change it.

**A workflow was rejected before any job started.** An invalid expression is
refused by GitHub before scheduling, which fails in seconds with no logs and no
indication of the offending line. `actionlint` catches these, and the
**Build and test** job runs it first for that reason. Locally:

```sh
actionlint
```
