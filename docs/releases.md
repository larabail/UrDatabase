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
    <Version>0.2.1</Version>
  </PropertyGroup>
</Project>
```

That number is the only source of truth. Everything else is derived from it:

| Thing | Comes out as |
| --- | --- |
| Git tag | `v0.2.1` |
| GitHub release | `UrDatabase 0.2.1` |
| macOS, Apple silicon | `UrDatabase-0.2.1-osx-arm64.dmg` |
| macOS, Intel | `UrDatabase-0.2.1-osx-x64.dmg` |
| Windows, 64-bit | `UrDatabase-0.2.1-win-x64.zip` |
| Bundle version | `CFBundleShortVersionString` and `CFBundleVersion` of `0.2.1` |
| Assembly version | `0.2.1` |

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
runs the tests again on the merged result, publishes all three runtime
identifiers, signs and notarizes the two macOS ones into disk images, checksums
everything, pushes the annotated tag `v<version>`, opens a GitHub Deployment,
publishes the release with the three downloads, `SHA256SUMS.txt` and generated
notes, then closes the deployment.

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
ad-hoc signs the macOS launcher, which satisfies that much, but only when the
build host is macOS; the condition in `Microsoft.NET.Sdk.targets` is literally
`IsOSPlatform(OSX) and Exists('/usr/bin/codesign')`.

So `dotnet publish -r osx-arm64` on a Linux runner exits 0 and produces a
download that is dead on arrival, with nothing in the logs saying so. The
packaging action verifies the signature with `codesign --verify` for exactly
that reason, before anything else is built on top of it.

Everything after the publish is macOS-only anyway: `codesign`, `hdiutil`,
`notarytool`, `stapler` and `spctl` exist nowhere else.

macOS runners are free on public repositories. On a private one they bill at ten
times the Linux rate, so making this repository private again has a cost
attached to it.

## Signing and notarization

### What was wrong

Up to and including `v0.2.0` the macOS download was ad-hoc signed and nothing
else, and **it could not be run on a current Mac at all**. Reproduced on macOS
26.5.1, Apple silicon, from the published `v0.2.0` archive:

```
$ ./UrDatabase.App
Killed: 9
```

No dialog, no stdout, no stderr. The kernel log says why:

```
kernel: (AppleMobileFileIntegrity) AMFI: '.../UrDatabase.App' is adhoc signed.
kernel: (AppleSystemPolicy) ASP: Security policy would not allow process
```

`codesign --verify` passed the whole time — the signature was intact, it was
simply ad-hoc, and that is what was refused.

**Every document in this repository used to give the fix as
`xattr -dr com.apple.quarantine`, and it did not work.** Measured, on the same
machine and the same archive:

| Attempt | Result |
| --- | --- |
| Launch as downloaded | `Killed: 9` |
| `xattr -dr com.apple.quarantine …` | `Killed: 9` |
| Re-sign ad-hoc locally with `codesign --force --deep --sign -` | `Killed: 9` |
| `open ./UrDatabase.App` | rejected |

Quarantine was never the blocker. Removing the flag cannot help when the
signature itself is what macOS refuses, and printing that command on the
downloads page sent people away certain the application was broken.

### What it does now

`scripts/package-macos-app.sh` is called by the packaging action for each macOS
runtime identifier. It:

1. imports the Developer ID certificate into a keychain created for that run,
   calling `security set-key-partition-list` so `codesign` does not block on a
   GUI prompt no runner can answer;
2. signs every file under `Contents/MacOS` and then the bundle, with
   `--timestamp --options runtime`;
3. notarizes the app with `xcrun notarytool submit --wait` and staples the
   ticket into it;
4. builds the disk image around the stapled app, signs it, notarizes and
   staples that too;
5. asks `spctl` what a user's machine will conclude, and fails the release if
   the answer is anything but "accepted".

The hardened runtime — `--options runtime` — is required for notarization and
forbids the writable-executable memory a JIT needs, so
`src/UrDatabase.App/UrDatabase.App.entitlements` grants `allow-jit` and
`allow-unsigned-executable-memory` back. Signing without them produces a build
that verifies, notarizes and staples perfectly and then dies at startup with
`Failed to create CoreCLR, HRESULT: 0x80070008`.

### Why a disk image and not a zip

Because a zip loses most of the signature, silently.

`codesign` treats every file under `Contents/MacOS` as the bundle's code, and a
self-contained .NET publish puts about 225 of them there. Only the 18 Mach-O
files can carry an embedded signature; the managed assemblies, the
runtimeconfig and `Data/schema.sql` are signed in the "generic" format, which
stores the signature in extended attributes. Measured on a real build:

| Archived with | Extracted with | `codesign --verify --strict` |
| --- | --- | --- |
| `ditto -c -k` | `ditto -x -k` | valid on disk |
| `ditto -c -k` | `unzip` | code object is not signed at all |
| `zip -r -y` | `unzip` | code object is not signed at all |

A zip therefore works for somebody who opens it in Finder and breaks for
somebody who opens it in a terminal — which is the same shape of bug as the one
being fixed. A disk image is a filesystem, so nothing can be dropped in
transit, and it is what Apple's own guidance assumes for Developer ID
distribution. It also gives a Mac user the drag-to-Applications window they
already know.

The `.app` bundle is not cosmetic either: `stapler` staples a ticket to a bundle
or an image and has nowhere to put one on a loose executable, so the bundle is a
prerequisite for notarization rather than a nicety. It is assembled by
`tool/make_macos_bundle.py`, which is Python rather than shell so that its tests
can run on Linux in the `Version` job.

### Without the secrets

A fork gets no secrets, and neither does this repository until the owner adds
them. Both still produce a build — an unsigned artifact is honest, a failed
release is not — and the pipeline says so in four places: a workflow warning,
the job summary, a `[!WARNING]` block in the release notes themselves stating
that the macOS downloads will not open, and the downloads page.

That last one needs a mechanism, because the downloads page is static HTML
deployed when `web/downloads/` changes and never rebuilt for a release. It
therefore cannot know from its own markup whether the build it is offering was
signed, and a page with *"it is signed and notarized"* written into it would
keep saying so through a release where the certificate was missing — which
would be a worse failure than the wrong `xattr` advice it replaced. Being wrong
about the remedy is bad; being wrong about there being a problem is worse.

So `release.yml` writes one word into its own notes:

```html
<!-- urdatabase:macos-signing=notarized -->
```

`notarized`, `signed` or `unsigned`, taken from what packaging actually
reported. `macosSigning()` in `web/downloads/releases.js` reads it back out of
the releases API and the page renders the matching sentence. A release with no
marker — everything up to 0.2.0, and anything made by hand — reads as `unknown`
and is described as unsigned, because that is what those are.

Nothing about the human-readable text depends on the marker; it is the same
fact in a form a script can act on, and `summariseNotes` strips it so it never
appears under "What's new".

Windows is still unsigned. SmartScreen shows *"Windows protected your PC"*,
which is a reputation check rather than a signature one: **More info**, then
**Run anyway**. Fixing it needs a Windows code signing certificate and is not
covered here.

## Secrets

| Secret | Used by | Status |
| --- | --- | --- |
| `TMDB_API_KEY` | `pr.yml` test builds, `release.yml` | Set |
| `OMDB_API_KEY` | `pr.yml` test builds, `release.yml` | Set |
| `MACOS_DEVELOPER_ID_CERT_P12_BASE64` | macOS signing, both workflows | **Missing — see below** |
| `MACOS_DEVELOPER_ID_CERT_PASSWORD` | macOS signing, both workflows | **Missing — see below** |
| `APP_STORE_CONNECT_KEY_ID` | notarization, `release.yml` | **Missing — see below** |
| `APP_STORE_CONNECT_ISSUER_ID` | notarization, `release.yml` | **Missing — see below** |
| `APP_STORE_CONNECT_PRIVATE_KEY` | notarization, `release.yml` | **Missing — see below** |
| `FIREBASE_SERVICE_ACCOUNT` | `deploy-downloads.yml` | **Missing — see below** |
| `GITHUB_TOKEN` | tagging, releases, deployments | Provided automatically |

`MACOS_PROVISIONING_PROFILE_BASE64` is deliberately **not** in that list. A
provisioning profile is for App Store distribution and for restricted
entitlements; Developer ID plus notarization needs neither, and requiring one
would be a step that fails for a reason nobody could act on.

### The five signing secrets are actually secret

Unlike the two API keys below, these are not published in any build and must
never be. The certificate's private key and the App Store Connect key can each
be used to sign software as this developer, which is a different and much larger
thing than reading somebody's film metadata. They are used only on the macOS
runner, imported into a keychain created for that one run and deleted
afterwards whether the run succeeded or not.

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

### 1. Add the five macOS signing secrets — required before a Mac download opens

Nothing else on this list is as visible: without these, every macOS release is
a download that cannot be run. A key cannot be read back out of GitHub, so
these have to be exported afresh rather than copied from another repository.

The certificate, from a Mac that has it in its login keychain:

1. **Keychain Access** → **My Certificates** → the
   *Developer ID Application: …* entry. If there is none, create one at
   [developer.apple.com](https://developer.apple.com/account/resources/certificates)
   → **Certificates** → **+** → **Developer ID Application**. It must be a
   *Developer ID Application* certificate: an *Apple Development* one cannot
   sign for distribution outside the App Store, and a *Developer ID Installer*
   one signs `.pkg` files rather than apps.
2. Right-click it → **Export…** → `.p12`, and set a password. Export the
   certificate **with its private key** — the disclosure triangle should have
   shown one underneath it.
3. `base64 -i Certificate.p12 | pbcopy`, and paste that as
   `MACOS_DEVELOPER_ID_CERT_P12_BASE64`. The password goes in
   `MACOS_DEVELOPER_ID_CERT_PASSWORD`.

The notarization credentials, from App Store Connect:

4. [appstoreconnect.apple.com](https://appstoreconnect.apple.com/access/integrations/api)
   → **Users and Access** → **Integrations** → **App Store Connect API** →
   **+**. Access: **Developer** is enough to notarize.
5. Download the `AuthKey_XXXXXXXXXX.p8`. **It can be downloaded once**; there is
   no second chance.
6. `APP_STORE_CONNECT_KEY_ID` is the ten-character key id, shown in the table.
   `APP_STORE_CONNECT_ISSUER_ID` is the UUID above the table, shared by every
   key in the account. `APP_STORE_CONNECT_PRIVATE_KEY` is the entire contents of
   the `.p8`, `-----BEGIN PRIVATE KEY-----` line included.

Then bump the version and merge anything, and check the run's summary: it says
either "macOS: signed and notarized" or exactly which secret is missing.

### 2. Restore `FIREBASE_SERVICE_ACCOUNT` — required before the site can deploy

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

### 3. Set the required status checks on `main`

Settings → Branches → branch protection for `main` → **Require status checks to
pass before merging**, and select, by these exact names:

- `Build and test`
- `Test builds`
- `Version`
- `Downloads site`

Without this the version check is advisory, and a pull request can merge red.

### 4. Nothing else

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
