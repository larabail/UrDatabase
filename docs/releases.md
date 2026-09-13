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
| Store MSIX identity version | `1.2.1.0` (Store-only major offset, final component reserved) |

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

The one thing no pull request may do, whether it ships anything or not, is
leave `<Version>` unreadable — blank, deleted, `0.15.0-preview`,
`$(BuildVersion)`. The release workflow reads that single line to decide what
to tag; with nothing usable there it tags nothing and reports success on every
merge afterwards, including the ones that do change `src/`. The `Version` check
refuses it for that reason alone.

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

"Currently on `main`" is meant literally: the check fetches the base branch and
reads it at the moment it runs, rather than using the base commit the pull
request event recorded when the branch opened. Those drift apart as soon as
anything else merges, and it is the live one the tag has to clear.

It still cannot make the collision impossible. A check reports the state it ran
in, and GitHub does not re-run an open pull request's checks when something
lands on `main`, so two branches carrying the same version can both be green
and merge seconds apart — the first takes the tag and the second publishes
nothing. Only a merge queue would close that gap. What the release run does
instead is refuse to let it happen quietly; see below.

### What happens when the version does not move

`release.yml` refuses to publish over an existing tag. That is what makes it
safe to run on every merge — a documentation change lands, the workflow sees
`v0.1.0` already tagged, writes a line saying so and exits green.

Whether that skip is green or red turns on the same question the pull request
check asks — did anything under `src/` change — measured this time between the
tagged commit and `main` as it now stands:

| The tag exists, and since it was published | The run |
| --- | --- |
| nothing under `src/` has changed | says so and passes. The ordinary docs, workflow or downloads-site merge. |
| something under `src/` has changed | **fails.** Shipped code is on `main` and in no release, and only a version bump can rescue it. |

The second row is not a guard. By the time it runs the merge has happened and
nothing can be prevented; what it prevents is the *silence*. That state used to
report success, and it was found twice by somebody wondering why the download
was still the old one — once after two pull requests took `0.4.1` and merged
twelve seconds apart, and again at `0.4.2` hours later. A failed run on `main`
mails the owner; a green one saying "already tagged" teaches everybody to read
nothing.

To clear it: raise `<Version>` in a pull request of its own and merge that. The
release it triggers carries everything that accumulated since the last tag,
including whatever was stranded. Do not move the existing tag onto the newer
commit — its assets are already published under that name, and a skipped
version number is cheaper than two different builds sharing one.

The rule and every message it prints live in `tool/check_release_gate.py`,
tested alongside the bump check by the same command.

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

Reads the version, and stops immediately if there is nothing to do — quietly
when nothing shipped has changed since the tag, and *loudly* when something
has. Otherwise: runs the tests again on the merged result, publishes all three
runtime identifiers, signs and notarizes the two macOS ones into disk images,
checksums everything, pushes the annotated tag `v<version>`, opens a GitHub
Deployment, publishes the release with the three downloads, `SHA256SUMS.txt`
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

### `store.yml` — Microsoft Store upload artifacts

A separate **Store upload package** job runs on `windows-2022`, with .NET 8,
Python 3.12 and Windows SDK `10.0.26100.0`. It runs on pull requests and relevant
pushes to `main`, and supports manual dispatch once the workflow exists on the
default branch. A narrowly scoped push trigger on
`larabail-microsoft-store-packaging` bootstraps the first Windows build without
merging or opening a pull request. Pushing that branch requires owner approval.
After this workflow is on `main`, the bootstrap branch entry can be removed.

It compiles and tests the Store distribution, generates the icon resource index
with MakePri, packs with MakeAppx's semantic validation enabled, and checks the
actual archive against every staged byte. It uploads
`UrDatabase-store-upload-x64-<run_number>` for 14 days. The artifact contains
`UrDatabase-<version>-store-x64.msix` and its `.sha256` checksum, produced under
`dist/store/` on the runner.

This workflow has read-only repository permissions. Ordinary artifact builds
never sign, create a release, upload to Partner Center or accept publishing terms.
Only the opted-in release caller can start its separate submission job.
Branch and PR
builds are keyless; `main` builds use only the existing optional `TMDB_API_KEY`,
`OMDB_API_KEY` and `URACTOR_API_KEY` secrets via the existing MSBuild properties.
No signing credential or new metadata key is required.

Branch/PR packages therefore need user-supplied metadata keys. Trusted `main`
builds are the production submission path; they are not interchangeable with
the keyless bootstrap artifact merely because the version is the same.

The original four required check names and the macOS-hosted ZIP/DMG workflows
are unchanged. Store artifacts are not attached to GitHub releases or advertised
as signed downloads.

## Microsoft Store MSIX

### Identity, version and desktop permissions

`packaging/windows/AppxManifest.xml` carries the public Partner Center identity:

| Field | Exact value |
| --- | --- |
| `Identity/Name` | `UrActor.UrDatabase` |
| `Identity/Publisher` | `CN=53FDA7CE-E84E-4A01-A833-B1C55FBA5540` |
| `Properties/PublisherDisplayName` | `UrActor` |

The display name is `UrDatabase`; confirm it matches the reserved Store name.
The package is x64, targets `Windows.Desktop` from `10.0.17763.0` (Windows 10
1809), and uses `Windows.FullTrustApplication` with restricted `runFullTrust`.
It runs at the interactive user's normal permission level, not as administrator
or in a UWP AppContainer. No `broadFileSystemAccess` or `unvirtualizedResources`
capability is requested. Default-player launches, external VLC, local folder
access, Jellyfin and SFTP stay desktop operations, subject to OS policy.

Store requires a nonzero first version component and reserves the fourth as
zero. The tooling derives **`(major + 1).minor.patch.0`** from the existing
`Directory.Build.props` version, so product `0.22.0` becomes package `1.22.0.0`,
and future product `1.0.0` becomes package `2.0.0.0`. This mapping is our
convention, not a second version to maintain; it stays monotonic across 1.0.
The tooling rejects values outside the 16-bit component range. Compare against
any previously submitted package before adopting a different mapping.

The 100/200/400% square logos, all five Store logo scales and a 44px unplated
taskbar asset come from the existing 1024px app artwork, not a new icon design.
Regenerate them on macOS with `bash scripts/make-store-assets.sh`; MakePri builds
`resources.pri` on Windows for the qualified assets.

### Build locally or obtain the CI artifact

On Windows, install .NET 8 SDK, Python **3.12+**, PowerShell **7+**, and Windows
SDK `10.0.26100.0` including MakeAppx and MakePri. From the repository root:

```powershell
pwsh -File scripts/package-store.ps1
# For another installed SDK, explicitly select it:
# pwsh -File scripts/package-store.ps1 -SdkVersion <installed-version>
```

The script publishes into a newly created temporary directory and removes it
afterwards. It refuses to overwrite an existing output MSIX. It includes the
self-contained .NET runtime and native dependencies, `Data/schema.sql` and the
tracked blank `appsettings.example.json`, excluding debug symbols. It refuses
unknown publish content, links, a non-x64 launcher, a framework-dependent build,
modified templates or a standalone distribution. A developer's ignored
`appsettings.json` is excluded by the Store build itself and rejected again by
the packager. No catalogue, poster cache, logs or personal paths are copied.

For CI, open **Actions > Microsoft Store package > successful run > Artifacts**,
download `UrDatabase-store-upload-x64-<run_number>`, and extract its MSIX. A local
Mac can cross-publish and stage the Windows payload, but **cannot produce the
MSIX with this tooling**: the exact Windows step is **Build and inspect the
unsigned MSIX**, which invokes `scripts/package-store.ps1`.

**Unsigned is intentional.** Partner Center accepts `.msix` uploads and
Microsoft signs them after certification. The unsigned artifact is not a
double-click installer, does not remove SmartScreen warnings from the separate
GitHub ZIP, and is not evidence of certification. Do not add test-certificate
identities or an unsigned-testing publisher OID to the Store manifest.

### Data and updates

The compiled `DistributionChannel=MicrosoftStore` disables the GitHub release
request and cached update offer even if `CheckForUpdates` is true. Store handles
servicing; ordinary ZIP/macOS builds keep their existing updater.

The Store's default logical profile is `%APPDATA%\UrDatabase.Store`, not the
ZIP install's `%APPDATA%\UrDatabase`. Windows' AppData redirection and merged view
vary by OS version; on modern Windows existing files can be modified in place.
Relying on virtualization alone would therefore risk changing the old catalogue.
The separate profile avoids reading it in the first place. There is no automatic
import, move, legacy uninstall or cleanup; the new profile gets first-run setup.
Do not promise that Store and ZIP copies share settings or scan results.

Windows may keep the Store profile in package-private storage and remove it on
uninstall. Back up the actual resolved profile before uninstalling; do not infer
its physical location from the logical AppData path alone. Explicit config paths
and `URDATABASE_DATA_DIR` still override defaults, so a person can deliberately
share data, but simultaneous copies must not share a catalogue. Test only in a
disposable Windows account/VM, with explicit scratch paths and fixture catalogues,
never against a maintainer's real app data.

### Before the owner submits

1. On a disposable Windows test account/VM, unpack with `makeappx unpack /p
   <package.msix> /d <scratch-layout>`. Microsoft documents development-mode
   registration using `Add-AppxPackage -Register <scratch-layout>\AppxManifest.xml`
   without changing the Store identity or signing the upload. Launch from Start
   so the process has package identity; launching the loose EXE is not this test.
2. Test Windows 10 1809 and current Windows 11 as a standard user, without
   preinstalled .NET. Confirm first-run setup/restart persistence, folder scan,
   local/default-player playback, VLC presence and absence, Jellyfin streaming
   and downloads, offline behavior, no GitHub updater, and an upgrade preserving
   the Store profile. Use fixture legacy data to check ZIP coexistence and
   uninstall preservation. `MaxVersionTested` is a compatibility declaration,
   not a claim that CI exercised that desktop; complete these checks before
   submitting. WACK can be run as an additional diagnostic where available, but
   Microsoft now marks it deprecated; neither it nor MakeAppx replaces review.
3. Open the reserved **MSIX or PWA app** in Partner Center and upload the `.msix`
   under **Packages**, not the EXE/MSI form asking for a package URL and silent
   install parameters. Review all package warnings and version ordering.
4. Complete pricing/availability, age ratings, description, screenshots, support
   contact and privacy policy fields. Reuse and review the existing
   [privacy page](https://urdatabase-downloads.web.app/privacy.html): it currently
   describes the website, so its scope must accurately cover desktop data and
   services before using it for the Store listing. Disclose the external-player
   requirement at the beginning of the description (VLC for Jellyfin playback).
5. Explain `runFullTrust` in **Submission options > Restricted capabilities**:
   "UrDatabase is an existing .NET/Avalonia desktop media catalogue. It scans
   user-selected local folders, persists a SQLite catalogue and settings, opens
   local films in the user's default player, launches installed VLC for Jellyfin
   streams, and downloads media at the user's request. It runs as the current
   user without elevation. It does not download or install application updates;
   Microsoft Store manages those." Supply reproducible test instructions and
   appropriate test media/server access without exposing private credentials.
6. The owner must explicitly approve submission. Store security, technical,
   content and restricted-capability review, signing and publication are still
   Microsoft's process. The first submission remains manual; opt-in CI can
   request later updates, but cannot approve or bypass certification.

Official references: [Store package/version requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements),
[desktop manifest and MakePri](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion),
[MSIX Store minimum OS](https://learn.microsoft.com/en-us/windows/msix/supported-platforms#microsoft-store-submissions),
[AppData virtualization](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization#default-msix-behavior),
[restricted-capability review](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/manage-submission-options#restricted-capabilities),
[Store servicing and privacy policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies).

### Automatic Store updates

Automation is **off by default**. `release.yml` calls the reusable `store.yml`
only after a new version has actually been released successfully on `main`,
and only when repository variable `STORE_PUBLISH_ENABLED` is exactly `true`.
The existing macOS ZIP/DMG release job stays on macOS. The called workflow builds
and inspects the self-contained MSIX on Windows, then a separate Linux job uses
the Store submission REST API. Ordinary pushes, pull requests and direct
**Microsoft Store package** dispatches build artifacts only.

Complete these prerequisites before enabling it:

1. Publish the first free MSIX submission manually. Finish the listing,
   privacy/age ratings, desktop acceptance and `runFullTrust` approval above.
   Prefer a trusted `main` artifact with the existing optional metadata defaults.
   If the keyless branch package was already published, a later key-equipped
   package needs a new product version: the updater refuses equal/older x64
   versions rather than silently substituting different bytes.
2. Associate an Entra tenant with Partner Center. Register an Entra application,
   add it to Partner Center account users, and assign the **Manager** role.
   Create a client secret and record its expiry for rotation. This REST flow
   requires no Seller ID and no code-signing certificate.
3. In GitHub, create environment **`microsoft-store`**, restrict its deployment
   branches to `main`, and add its secrets: `AZURE_AD_TENANT_ID`,
   `AZURE_AD_APPLICATION_CLIENT_ID`, and `AZURE_AD_APPLICATION_SECRET`.
   Enter values directly in GitHub, never in source, issues, command examples or
   chat. These credentials never enter the app, MSIX, website or artifacts.
   Required reviewers are optional; enabling them makes submission and status
   jobs wait for approval instead of operating unattended.
4. Set repository Actions variable **`STORE_PUBLISH_ENABLED=true`**. The next
   successful new-version release can submit an update. Removing the variable
   or setting it to `false` disables submission and scheduled status checks,
   without changing ordinary releases or artifact builds.

`packaging/windows/store-product.json` is the public product-ID source:
**`9N6B4KTL3LB2`**. The API response must also match the manifest's identity name
and publisher. The tool requires a `Published` baseline, ordinary free pricing
and recognizable individual x64 packages. It clones the published submission,
preserves listings/pricing and other architectures, marks only old x64 packages
for replacement, uploads a ZIP containing the new MSIX, and requests immediate
publication **after successful certification**. Advanced pricing, ambiguous
bundles/architectures and unsupported states fail closed for manual handling.
The single-blob upload is capped at 64 MiB to work with older SAS service
versions; larger packages need a manual upload or separately tested block-upload
support, and are refused before creating a draft.

**Draft safety and concurrency.** Publishing runs share a non-canceling lock,
separate from cancelable artifact-only builds. A pending submission, including
an unfinished manual draft or one still in certification, stops CI before it
creates anything. The tool never sends DELETE, never adopts an existing draft,
and never blindly retries a create/commit after a timeout. It checks ownership
and unchanged draft contents before updating and committing its new draft.
We deliberately do not use `msstore publish`: it deletes an existing pending
draft before recreating one.

Do not edit Partner Center, or run another publisher outside this workflow,
while submission CI is running. Microsoft's REST API does not document an
ETag/conditional-create contract, so CI serialization and rechecks cannot
guarantee an atomic lock against a human editing the portal. Microsoft also
warns that portal edits to an API-created draft can make it uncommittable.
On conflict or uncertainty this implementation stops, leaves the draft intact,
and asks for operator investigation rather than destructive recovery.

The submitting run saves **`Store-submission-state-<run_number>-<run_attempt>`**
for 90 days, including the returned submission ID, commit, package version,
checksum and last phase, but never access tokens or upload SAS URLs. A
`creating` phase without an ID means the create response was lost: inspect
Partner Center instead of blindly rerunning. A `committing` phase likewise
requires checking the known submission's status before any retry. Do not
delete/recreate drafts automatically to make a red run green.

The submit job waits up to ten minutes for commit processing. `PreProcessing`
or `Certification` is **not** a claim that the app is live. **Microsoft Store
status** (`store-status.yml`) checks the current pending/latest published
submission every six hours, or by manual dispatch on `main`; it performs only
GETs after authentication. It reports processing versus `Published` in the
Actions summary and fails on certification/publishing failures or cancellation.
Status checks do not resume, edit, commit or delete a draft. Follow failures
in Partner Center; the published GitHub release remains available even when its
separate Store update is blocked. A full release rerun with an existing tag
does not manufacture another release; after investigation, use failed-job
reruns where safe or publish a higher version.

Offline coverage: `python3 -m unittest discover -s tool -p '*store*.py'`.
Tests inject a fake HTTP transport and fixture submission data; no credentials,
live API calls or local app-data access are required. Live authentication,
ingestion and certification still need the owner's configured account and
cannot be established by these tests.

Official references: [submission API prerequisites and OAuth](https://learn.microsoft.com/en-us/windows/uwp/monetize/create-and-manage-submissions-using-windows-store-services),
[submission lifecycle and portal-edit warning](https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-app-submissions),
[create a submission](https://learn.microsoft.com/en-us/windows/uwp/monetize/create-an-app-submission),
[update its packages](https://learn.microsoft.com/en-us/windows/uwp/monetize/update-an-app-submission),
[commit status](https://learn.microsoft.com/en-us/windows/uwp/monetize/get-status-for-an-app-submission),
[CLI draft replacement behavior](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/commands#publish-command).

### Enable the website Store link

The permanent destination is
`https://apps.microsoft.com/detail/9N6B4KTL3LB2`, not an unsigned artifact URL.
It is configured but **hidden by default**, because an assigned ID does not
prove the first listing is public. After verifying it is live, set repository
Actions variable **`STORE_LISTING_LIVE=true`** and dispatch **Deploy the downloads
site** on `main`. Variable changes alone do not redeploy the site.

Deployment runs `node tool/configure_store_site.mjs` before the existing site
tests. It generates `web/downloads/store-config.js` from the public product JSON
and updates the static HTML Store link, so the Windows card also works without
JavaScript. Only the product ID and live flag enter the site, never publishing
credentials. Invalid enabled IDs or flag values fail deployment. To preview
locally: `STORE_LISTING_LIVE=true node tool/configure_store_site.mjs`; run the
same command with `false` to restore the disabled configuration.

With the link enabled, Windows visitors get a Store hero and card link;
the GitHub ZIP stays an unsigned alternative and macOS is unchanged. The
Store link works even when GitHub has no releases, fails or does not respond,
and never labels the Store build with GitHub's version. Each Store update
continues to use the same URL after certification; no redeploy is needed for
each release. The card explains the separate first-run setup/catalogue.
Set `STORE_LISTING_LIVE=false` and redeploy to hide it again.

## Why the builds are made on macOS

Both `pr.yml`'s download packaging job and `release.yml` run on `macos-14`, and that
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

## Windows signing and antivirus warnings

**The Windows release is still unsigned.** Nothing in the current packaging
action performs Authenticode signing. The Apple certificate cannot sign the
Windows executable, and putting a signature on the ZIP is not a substitute for
signing the executable inside it.

There are two different reports to distinguish:

- **SmartScreen: "Windows protected your PC" / "unrecognized app".** This is
  an application-reputation warning. Publicly trusted signing identifies the
  publisher and lets reputation accumulate across releases, but new signed
  files can still warn. Buying an EV certificate does not buy an instant
  SmartScreen bypass.
- **Antivirus: a named threat or quarantined file.** Record the antivirus
  vendor, detection name, app version and exact file that was blocked. Investigate
  that release before calling it a false positive. If it is clean, submit the
  affected file to the vendor; Microsoft's
  [software-developer submission portal](https://www.microsoft.com/en-us/wdsi/filesubmission)
  is the route for Defender. Signing does not override a malware detection.
  Do not ask users to disable protection or exclude the app's directory.

### Choosing a provider

For this open-source project, consider
[SignPath Foundation](https://signpath.org/terms) first: it offers free signing
to qualifying projects, subject to application, licensing and supply-chain
requirements, a published signing policy and release approvals. Acceptance is
not automatic, and its certificate identifies SignPath Foundation as publisher.

For signing under a verified individual or organisation identity,
[Azure Artifact Signing](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)
(formerly Trusted Signing) is Microsoft's recommended non-Store option.
Microsoft currently lists approximately US$9.99/month, available to individuals
in the US and Canada and organisations in the US, Canada, EU and UK. Identity
verification and an Azure account are required. A public-CA code-signing
certificate with a supported hardware token or cloud HSM is another option.
Self-signed certificates are for development or managed internal deployments,
not public downloads. An MSIX submitted through the Microsoft Store is a
different distribution path: the Store signs it after certification.

### Where signing belongs in this pipeline

Provider enrolment and CI integration **have not been done yet**. Once a
provider is chosen:

1. Keep the macOS publishing/signing job on macOS. For an Azure/SignTool
   integration, hand the Windows publish output to a separate Windows job;
   for SignPath, use its approved artifact-signing integration.
2. Sign `UrDatabase.App.exe` and the project's own shipped DLLs with
   Authenticode and a SHA-256 timestamp. Preserve third-party signatures;
   do not re-sign dependencies as though this project authored them.
3. Verify the returned binaries, for example with `signtool verify /pa /v`
   on a Windows runner, and fail the release if required signatures are
   missing or invalid.
4. Create the Windows ZIP **from the signed output**, then generate
   `SHA256SUMS.txt` and publish. Signing after checksumming changes the bytes
   and invalidates the recorded hashes.

Restrict signing to approved release builds. Use GitHub OIDC with narrowly
scoped Azure permissions where supported, or the provider's protected signing
credentials and approval flow; do not expose signing access to untrusted pull
requests or commit private keys. Keep one publisher identity across releases
so its reputation can build.

## Secrets

| Secret | Used by | Status |
| --- | --- | --- |
| `TMDB_API_KEY` | `pr.yml` test builds, `release.yml` | Set |
| `OMDB_API_KEY` | `pr.yml` test builds, `release.yml` | Set |
| `URACTOR_API_KEY` | `pr.yml` test builds, `release.yml` | Optional; absent means no awards |
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

Unlike the API keys below, these are not published in any build and must
never be. The certificate's private key and the App Store Connect key can each
be used to sign software as this developer, which is a different and much larger
thing than reading somebody's film metadata. They are used only on the macOS
runner, imported into a keychain created for that one run and deleted
afterwards whether the run succeeded or not.

### The metadata API keys are not secret once shipped

`TMDB_API_KEY`, `OMDB_API_KEY` and `URACTOR_API_KEY` are compiled into the
published binaries so
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
  test builds are produced with every key empty and start with no metadata,
  posters, ratings or awards. That is expected, it is not a failure, and nothing in the
  pipeline treats it as one. The build log says so, and so does the pull
  request's job summary.
- `dotnet test` is **never** given the keys. The test suite drives the TMDB,
  OMDb and UrActor clients through fake HTTP handlers, so it must keep passing
  with all of them unset — that is what proves the fakes are actually in the path. Please do not
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

### 4. Complete the first Store submission, then opt in to updates

Follow [Microsoft Store MSIX](#microsoft-store-msix) for the package and manual
certification steps. Generating an artifact does not submit or publish it.
[Automatic Store updates](#automatic-store-updates) and
[the public website link](#enable-the-website-store-link) have separate opt-in
variables; neither is activated just by adding a product ID.

No Firestore, no Cloud Functions, no emulators, no Firebase Authentication. The
application uses no Firebase at runtime at all; Hosting serves one static page
and that is the entire relationship.

## When something goes wrong

**A merge published nothing.** Almost always the version did not move. Which
kind of "nothing" it was is on the run: green with *Nothing released* means the
merge changed nothing shippable and there was genuinely nothing to publish; red
with *This merge shipped nothing, and it should have* means code under `src/`
is now on `main` and in no release. Raise `<Version>` in a pull request of its
own and merge it — the release that follows carries everything stranded since
the last tag. Do not move the existing tag onto the newer commit.

**The release failed halfway, and the tag exists.** Re-running the workflow
does nothing, because it sees the tag — and now says so in whichever of the two
ways above applies. Delete the tag and the partial release on GitHub, then
re-run — or, more simply, raise the version and release the next one.

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
