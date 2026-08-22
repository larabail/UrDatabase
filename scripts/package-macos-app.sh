#!/usr/bin/env bash
#
# Signs a UrDatabase.app with a Developer ID, notarizes it, staples the ticket
# into it and ships it in a signed, notarized, stapled disk image.
#
# Why any of this is necessary
# ----------------------------
#
# Until v0.2.1 the macOS download was ad-hoc signed, which is what the .NET SDK
# does for free on a macOS host. That is enough for the kernel to let an arm64
# binary start at all, and it is *not* enough for anything downloaded. On a
# current Mac, launching the published v0.2.0 build gives:
#
#     Killed: 9
#
# with no dialog, no stdout and no stderr, and the kernel log says why:
#
#     AMFI: '.../UrDatabase.App' is adhoc signed.
#     ASP: Security policy would not allow process
#
# The rejection is of the ad-hoc signature itself. That matters because every
# document this project shipped told people to run
# `xattr -dr com.apple.quarantine`, and clearing the quarantine flag does
# nothing here -- the build stays dead, and the one instruction meant to rescue
# somebody sends them away certain the app is broken. Re-signing ad-hoc locally
# does not help either. Only a real Developer ID signature plus notarization
# does.
#
# Why a disk image rather than a zip
# ----------------------------------
#
# Because a zip loses the signature, and it does so silently.
#
# `codesign` treats *every* file under `Contents/MacOS` as nested code, and a
# self-contained .NET publish puts about 225 of them there -- managed
# assemblies, a runtimeconfig, a schema. Only the 18 Mach-O files among them
# can carry an embedded signature; the rest are signed in the "generic" format,
# which stores the signature in extended attributes. Measured on this build:
#
#     ditto -c -k  ->  ditto -x -k    valid on disk
#     ditto -c -k  ->  unzip          "code object is not signed at all"
#     zip -r -y    ->  unzip          "code object is not signed at all"
#
# So a zip works if the person opening it uses Finder and breaks if they use
# the terminal, which is the same class of bug as the one being fixed: it works
# for whoever tested it. A disk image is a filesystem, so nothing about the
# bundle can be dropped in transit, and it is also the container Apple's own
# guidance assumes for Developer ID distribution -- and the one that gives a
# Mac user the drag-to-Applications window they already know.
#
# What it does, in order, and why that order
# ------------------------------------------
#
#   1. Imports the Developer ID certificate into a keychain created for this
#      run, so nothing is left behind on a shared machine.
#   2. Signs every nested file, then the bundle. Inside out: signing the bundle
#      seals what is under it, so anything signed afterwards invalidates the
#      seal, and the failure surfaces much later with a message that names none
#      of this.
#   3. Notarizes the app and staples the ticket into it, so the copy the user
#      drags to /Applications carries its own proof and starts on a machine
#      with no network.
#   4. Builds the disk image around the stapled app, signs it, and notarizes
#      and staples that too. A quarantined disk image is assessed when it is
#      mounted, so an unnotarized one is refused before the app inside it is
#      ever reached.
#
# Two notarization submissions rather than one is a deliberate cost. Notarizing
# only the image would register the app with Apple as well, but would leave no
# ticket inside the app -- and Gatekeeper would then have to ask Apple on first
# launch, so the download would fail for anybody offline.
#
# Degrading without secrets
# -------------------------
#
# A fork gets no secrets, and this repository has none until the owner adds
# them. Both must still produce a build: an unsigned artifact is honest, a
# failed release is not. So missing secrets are reported and skipped, and the
# caller is told which of signing and notarization happened so it can say so on
# the release. Once the secrets *are* present, every failure below is fatal --
# publishing an unsigned build while claiming it is signed is the one outcome
# worth failing a release over.
#
# Usage
# -----
#
#   scripts/package-macos-app.sh path/to/UrDatabase.app path/to/output.dmg
#
# Reads from the environment:
#
#   MACOS_CERT_P12_BASE64   Developer ID Application certificate and key,
#                           as a base64 .p12. Absent means "do not sign".
#   MACOS_CERT_PASSWORD     The .p12 password.
#   MACOS_SIGNING_IDENTITY  Optional. Use an identity already in the login
#                           keychain instead of importing a .p12 -- how this
#                           script is run on a developer's own machine, so
#                           that what CI runs can be reproduced by hand.
#   ASC_KEY_ID              App Store Connect API key, for notarization.
#   ASC_ISSUER_ID           Absent means "sign but do not notarize".
#   ASC_PRIVATE_KEY         The .p8 contents.
#   ENTITLEMENTS            Optional path; defaults to the app's own file.
#
# Writes `signed=`, `notarized=` and `reason=` to $GITHUB_OUTPUT when that is
# set, and prints the same to stdout either way.

set -euo pipefail

APP=${1:-}
DMG=${2:-}
if [ -z "$APP" ] || [ -z "$DMG" ]; then
  echo "usage: $0 path/to/UrDatabase.app path/to/output.dmg" >&2
  exit 2
fi
if [ ! -d "$APP" ]; then
  echo "error: $APP is not a bundle directory." >&2
  exit 2
fi

HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO=$(dirname -- "$HERE")
ENTITLEMENTS=${ENTITLEMENTS:-$REPO/src/UrDatabase.App/UrDatabase.App.entitlements}

SIGNED=false
NOTARIZED=false
REASON=''

WORK=$(mktemp -d)
KEYCHAIN=''

cleanup() {
  # Unconditional. A failed run leaves exactly the same private key on the
  # machine as a successful one, and on a self-hosted or shared runner that is
  # the whole risk.
  if [ -n "$KEYCHAIN" ] && [ -f "$KEYCHAIN" ]; then
    security delete-keychain "$KEYCHAIN" >/dev/null 2>&1 || true
  fi
  rm -rf "$WORK"
  return 0
}
trap cleanup EXIT

# Reported through one function so the "we did not sign" paths cannot forget to
# say so, and so the caller always has all three values to render.
report() {
  echo "signed=$SIGNED"
  echo "notarized=$NOTARIZED"
  echo "reason=$REASON"
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    {
      echo "signed=$SIGNED"
      echo "notarized=$NOTARIZED"
      echo "reason=$REASON"
    } >> "$GITHUB_OUTPUT"
  fi
}

# Notarizes whatever it is given and waits for the answer. Split out because
# the app and the disk image go through exactly the same submission, and a
# second copy of it is a second place for the credential handling to drift.
notarize() {
  local target=$1
  local key_path="$WORK/asc.p8"

  # An App Store Connect key rather than an app-specific password. The password
  # route is what most guides show and it breaks whenever the Apple ID's
  # password or its second factor changes -- which surfaces months later,
  # mid-release, with nobody left who remembers setting it up.
  printf '%s' "$ASC_PRIVATE_KEY" > "$key_path"
  chmod 600 "$key_path"

  if ! xcrun notarytool submit "$target" \
        --key "$key_path" \
        --key-id "$ASC_KEY_ID" \
        --issuer "$ASC_ISSUER_ID" \
        --wait \
        --timeout 45m; then
    rm -f "$key_path"
    echo "::error title=Notarization failed::Apple refused $(basename "$target"). Run 'xcrun notarytool log <submission-id>' with the same credentials for the per-file reason; the usual causes are a binary signed without --options runtime and a nested file that was missed." >&2
    return 1
  fi
  rm -f "$key_path"
}

# Builds the disk image. Called from both the signed and the unsigned paths, so
# what a fork produces has the same shape as what a release produces and the
# packaging is exercised either way.
make_dmg() {
  local staging="$WORK/dmg"
  rm -rf "$staging"
  mkdir -p "$staging"
  cp -R "$APP" "$staging/"
  # The Applications symlink is what turns the window into a drag target rather
  # than a folder somebody has to work out what to do with.
  ln -s /Applications "$staging/Applications"

  rm -f "$DMG"
  mkdir -p "$(dirname "$DMG")"
  # UDZO: compressed, and read-only. Read-only matters beyond the size -- a
  # writable image cannot carry a stable signature, because mounting one can
  # change it.
  hdiutil create -volname 'UrDatabase' -srcfolder "$staging" \
    -ov -format UDZO "$DMG" > /dev/null
  rm -rf "$staging"
}

# ---------------------------------------------------------------------------
# 1. An identity to sign with.
# ---------------------------------------------------------------------------

IDENTITY=''

if [ -n "${MACOS_SIGNING_IDENTITY:-}" ]; then
  IDENTITY=$MACOS_SIGNING_IDENTITY
  echo "Using the identity already in the keychain: $IDENTITY"

elif [ -n "${MACOS_CERT_P12_BASE64:-}" ]; then
  if [ -z "${MACOS_CERT_PASSWORD:-}" ]; then
    echo "::error title=Incomplete signing secrets::MACOS_DEVELOPER_ID_CERT_P12_BASE64 is set but MACOS_DEVELOPER_ID_CERT_PASSWORD is not. A .p12 cannot be imported without its password." >&2
    exit 1
  fi

  KEYCHAIN="$WORK/urdatabase-signing.keychain-db"
  KEYCHAIN_PASSWORD=$(uuidgen)
  echo "::add-mask::$KEYCHAIN_PASSWORD"

  security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
  # Without a timeout the keychain relocks partway through, and codesign then
  # fails somewhere in the middle of a few hundred files.
  security set-keychain-settings -lut 21600 "$KEYCHAIN"
  security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"

  echo "$MACOS_CERT_P12_BASE64" | base64 --decode > "$WORK/cert.p12"
  security import "$WORK/cert.p12" \
    -k "$KEYCHAIN" \
    -P "$MACOS_CERT_PASSWORD" \
    -T /usr/bin/codesign \
    -T /usr/bin/security
  rm -f "$WORK/cert.p12"

  # The step everybody misses. Without it codesign finds the key, asks the
  # window server for permission to use it, and waits forever for a dialog no
  # runner can answer -- so the job burns its whole timeout and the log ends
  # mid-signature with nothing that looks like an error.
  security set-key-partition-list \
    -S apple-tool:,apple:,codesign: \
    -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN" > /dev/null

  # Added to the search list rather than replacing it. Replacing drops the
  # system keychain, which is where Apple's intermediate certificates live, and
  # the signature then fails to chain to anything.
  # shellcheck disable=SC2046
  security list-keychains -d user -s "$KEYCHAIN" \
    $(security list-keychains -d user | tr -d '"')

  IDENTITY=$(security find-identity -v -p codesigning "$KEYCHAIN" \
    | sed -n 's/.*"\(Developer ID Application:.*\)"$/\1/p' | head -1)

  if [ -z "$IDENTITY" ]; then
    echo "::error title=No Developer ID in the certificate::The imported .p12 holds no 'Developer ID Application' identity. An 'Apple Development' or 'Mac Developer' certificate cannot sign a build for distribution outside the App Store, and neither can a 'Developer ID Installer' one. Export the Developer ID Application certificate together with its private key." >&2
    security find-identity -v -p codesigning "$KEYCHAIN" >&2
    exit 1
  fi
  echo "Signing as: $IDENTITY"

else
  REASON='no Developer ID certificate was available to this run'
  echo "::warning title=Unsigned macOS build::MACOS_DEVELOPER_ID_CERT_P12_BASE64 is not set, so this macOS build is only ad-hoc signed. A current Mac kills an ad-hoc signed download on launch, with no dialog at all. Expected for a pull request from a fork; on a release it means the repository secrets are missing."
  make_dmg
  report
  exit 0
fi

# ---------------------------------------------------------------------------
# 2. Sign, inside out.
# ---------------------------------------------------------------------------

EXECUTABLE=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' \
  "$APP/Contents/Info.plist")
MAIN="$APP/Contents/MacOS/$EXECUTABLE"

echo "::group::Signing the contents of the bundle"
# Everything under Contents/MacOS, not only the obvious binaries.
#
# codesign treats that directory as the bundle's code, so it refuses to sign
# the bundle while anything in there is unsigned -- and it means *anything*: a
# managed assembly, `createdump`, and equally
# `UrDatabase.App.runtimeconfig.json` and `Data/schema.sql`. Miss one and the
# outer signature fails with
#
#     code object is not signed at all
#     In subcomponent: .../UrDatabase.App.runtimeconfig.json
#
# which reads like a corrupt file rather than a missing step. Signing them all
# is what `--force --deep` would do; it is done explicitly instead, because
# --deep is deprecated for distribution and hands the same entitlements to
# every nested object rather than only to the executable that needs them.
NESTED=0
while IFS= read -r item; do
  [ "$item" = "$MAIN" ] && continue
  codesign --force --timestamp --options runtime --sign "$IDENTITY" "$item"
  NESTED=$((NESTED + 1))
done < <(find "$APP/Contents/MacOS" -type f)
echo "Signed $NESTED files inside the bundle."
echo "::endgroup::"

# Normalised through plutil before codesign sees it. The entitlements file is
# commented, because what each exception buys and costs is not guessable from
# the key name -- and the parser AMFI uses to read entitlements is a minimal
# one that treats a comment as a syntax error, reported as
# `AMFIUnserializeXML: syntax error near line 5` with no mention of comments.
# plutil reads it with the real property list parser and writes back the
# canonical form, so the file can stay readable and codesign still gets what it
# wants.
plutil -convert xml1 -o "$WORK/entitlements.plist" "$ENTITLEMENTS"

# The bundle last, which seals everything above. The entitlements land here and
# nowhere else: they are what lets the .NET JIT map executable memory under the
# hardened runtime, and without them the app dies at startup with
# `Failed to create CoreCLR, HRESULT: 0x80070008`.
codesign --force --timestamp --options runtime \
  --entitlements "$WORK/entitlements.plist" \
  --sign "$IDENTITY" "$APP"

codesign --verify --strict --verbose=2 "$APP"
codesign --display --verbose=2 "$APP" 2>&1 | head -12

SIGNED=true

# ---------------------------------------------------------------------------
# 3. Notarize the app, or stop here and say so.
# ---------------------------------------------------------------------------

if [ -z "${ASC_KEY_ID:-}" ] || [ -z "${ASC_ISSUER_ID:-}" ] || [ -z "${ASC_PRIVATE_KEY:-}" ]; then
  REASON='the App Store Connect API key was not available to this run'
  echo "::warning title=Signed but not notarized::APP_STORE_CONNECT_KEY_ID, APP_STORE_CONNECT_ISSUER_ID or APP_STORE_CONNECT_PRIVATE_KEY is missing, so this build is Developer ID signed but not notarized. macOS refuses an unnotarized download -- with a dialog rather than in silence, which is an improvement and is still refusal."
  make_dmg
  codesign --force --timestamp --sign "$IDENTITY" "$DMG"
  report
  exit 0
fi

echo "::group::Notarizing the app"
# `ditto` rather than `zip`. The notary service is sent an archive of a signed
# bundle, and `zip` drops the extended attributes that carry most of the nested
# signatures; the upload is then rejected over a signature that is perfectly
# valid on disk. This archive is thrown away -- the ticket is stapled to the
# bundle, and the disk image is built around that.
ditto -c -k --keepParent "$APP" "$WORK/app.zip"
notarize "$WORK/app.zip"

# Stapling writes the ticket into the bundle, so the copy a user drags to
# /Applications proves itself without asking Apple. Without it, first launch
# needs a working network -- which is exactly the wrong moment to need one.
xcrun stapler staple "$APP"
xcrun stapler validate "$APP"
echo "::endgroup::"

# ---------------------------------------------------------------------------
# 4. The disk image, signed and notarized in its own right.
# ---------------------------------------------------------------------------

echo "::group::Notarizing the disk image"
make_dmg
codesign --force --timestamp --sign "$IDENTITY" "$DMG"
notarize "$DMG"
xcrun stapler staple "$DMG"
xcrun stapler validate "$DMG"
echo "::endgroup::"

NOTARIZED=true

# ---------------------------------------------------------------------------
# 5. What a user's machine will conclude, asked in the same terms.
# ---------------------------------------------------------------------------

# This is the assertion the whole change exists to make true, so it is fatal: a
# release that reaches here and is still refused is a release that would be
# dead on arrival, and shipping it would repeat the bug being fixed.
REFUSED=false

# `--type open --context context:primary-signature` is how Gatekeeper assesses
# a disk image somebody double-clicked. `--type execute` is the wrong question
# to ask of one, and it answers it wrongly.
if ! spctl --assess --type open --context context:primary-signature \
      --verbose=2 "$DMG"; then
  echo "::error title=Gatekeeper refuses the disk image::The .dmg is signed, notarized and stapled, and spctl rejects it anyway. Do not publish it." >&2
  REFUSED=true
fi

if ! spctl --assess --type execute --verbose=2 "$APP"; then
  echo "::error title=Gatekeeper refuses the app::The bundle is signed, notarized and stapled, and spctl rejects it anyway. Do not publish it: it will not open on a user's Mac. Check that the certificate is a Developer ID Application one and that the Apple Worldwide Developer Relations intermediate it chains to has not expired." >&2
  REFUSED=true
fi

if [ "$REFUSED" = true ]; then
  exit 1
fi

report
