/**
 * Turning GitHub's list of releases into the rows the downloads page shows.
 *
 * The page asks the releases API in the browser instead of being generated
 * when a release happens. A generated page is only ever as current as the last
 * run of the release workflow, and it can describe exactly one version:
 * deleting a bad release, re-tagging a build or publishing a fix leaves it
 * advertising something that is no longer true, with no way to correct it
 * except to cut another release. Asking at page load makes the page a view of
 * whatever is actually downloadable right now, and the older versions come
 * along for free because they are in the same response.
 *
 * Everything here is pure: it takes a decoded API payload and returns plain
 * objects. Fetching and the DOM live in `page.js`. That split is what makes
 * the fiddly parts testable without a browser -- which asset belongs to which
 * machine, whether `0.9.0` or `0.10.0` is newer, and whether a Mac is an Apple
 * silicon one, none of which can be checked by looking at the page.
 */

export const REPO = 'larabail/UrDatabase';

/** Newest first, and enough of them to cover a long time at this pace. */
export const RELEASES_API =
  `https://api.github.com/repos/${REPO}/releases?per_page=30`;

export const RELEASES_PAGE = `https://github.com/${REPO}/releases`;
export const REPO_PAGE = `https://github.com/${REPO}`;

/**
 * The three runtime identifiers the release workflow publishes.
 *
 * These are .NET RIDs and they are also, deliberately, the suffix of every
 * release asset name: `UrDatabase-0.1.0-osx-arm64.zip`. One string identifies
 * the build everywhere -- in the workflow, in the filename and here -- so
 * there is no table mapping one naming scheme onto another to get out of step.
 */
export const OSX_ARM64 = 'osx-arm64';
export const OSX_X64 = 'osx-x64';
export const WIN_X64 = 'win-x64';

/** Apple silicon first: it is what most visitors on a Mac are running. */
export const PLATFORMS = [OSX_ARM64, OSX_X64, WIN_X64];

/** Files that sit beside the builds and are not themselves downloads. */
const NOT_A_BUILD = ['.sha256', '.sha512', '.md5', '.asc', '.sig', '.txt'];

/** Whether [platform] is one of the two macOS builds. */
export function isMac(platform) {
  return platform === OSX_ARM64 || platform === OSX_X64;
}

/**
 * The `MAJOR.MINOR.PATCH` numbers in [raw], or null if it is not a version.
 *
 * A leading `v` is dropped so a tag and a version parse the same way, and a
 * `-preview` tail is dropped because it does not participate in ordering.
 */
export function parseVersion(raw) {
  if (typeof raw !== 'string') return null;

  let text = raw.trim();
  if (text.startsWith('v') || text.startsWith('V')) text = text.slice(1);
  text = text.split('+')[0].split('-')[0].trim();
  if (!text) return null;

  const parts = text.split('.');
  if (parts.length > 3) return null;

  const numbers = [];
  for (const part of parts) {
    // `Number('')` is 0 and `Number('1px')` is NaN, so both are refused here
    // rather than read as a version component.
    if (!/^\d+$/.test(part)) return null;
    numbers.push(Number(part));
  }
  while (numbers.length < 3) numbers.push(0);
  return numbers;
}

/** [raw] as a normalised `0.1.0`, or null if it is not a version. */
export function versionText(raw) {
  const parsed = parseVersion(raw);
  return parsed ? parsed.join('.') : null;
}

/**
 * Orders two versions, oldest first.
 *
 * Compared number by number rather than as text, because as text `0.9.0` sorts
 * after `0.10.0` and the page would offer the older build as the current one.
 * That only starts happening at the tenth minor release, which is exactly when
 * nobody is looking for it any more.
 */
export function compareVersions(a, b) {
  const left = parseVersion(a);
  const right = parseVersion(b);
  if (!left || !right) return 0;
  for (let i = 0; i < 3; i += 1) {
    if (left[i] !== right[i]) return left[i] - right[i];
  }
  return 0;
}

/** Which build an asset called [name] is, or null if it is not one. */
export function assetPlatform(name) {
  if (typeof name !== 'string') return null;
  const lower = name.toLowerCase();

  // Checked first, because `UrDatabase-0.1.0-osx-arm64.zip.sha256` also ends
  // in `-osx-arm64.zip` as far as a naive search is concerned, and offering a
  // 90-byte text file as the macOS build is worse than offering nothing.
  if (NOT_A_BUILD.some((suffix) => lower.endsWith(suffix))) return null;

  // Matched on the whole `-<rid>.zip` tail rather than on the architecture
  // alone. `-x64.zip` would match both the Intel Mac build and the Windows
  // one, and whichever was listed first would win.
  return PLATFORMS.find((rid) => lower.endsWith(`-${rid}.zip`)) || null;
}

/**
 * The three builds among a release's [assets].
 *
 * A platform with no build comes back as null rather than being left out, so a
 * caller can tell "this release had no Windows build" from "this is not a
 * release", and say so.
 */
export function pickDownloads(assets) {
  const downloads = {};
  for (const platform of PLATFORMS) downloads[platform] = null;
  if (!Array.isArray(assets)) return downloads;

  for (const asset of assets) {
    if (!asset || typeof asset !== 'object') continue;
    const platform = assetPlatform(asset.name);
    // First one wins. A release carrying two builds for the same RID is not
    // something this pipeline produces, and guessing between them would be
    // worse than taking the one GitHub listed first.
    if (!platform || downloads[platform]) continue;

    const url = asset.browser_download_url;
    // Only ever put an https link on the page. The API is read over https, but
    // an asset URL is still data from a server, and a `javascript:` URL
    // smuggled into an href would run when somebody clicked it.
    if (typeof url !== 'string' || !url.startsWith('https://')) continue;

    const size = Number(asset.size);
    downloads[platform] = {
      name: asset.name,
      url,
      size: Number.isFinite(size) && size > 0 ? size : 0,
    };
  }
  return downloads;
}

/**
 * A release from the API as the page wants it, or null if it is not one.
 *
 * Drafts are invisible to anyone without push access, so listing one would
 * offer a download that 404s for every visitor. Pre-releases are excluded for
 * the mirror-image reason: they are visible, but this pipeline does not
 * publish them, so one appearing is a hand-made release that was deliberately
 * not meant for the front page.
 */
export function normalizeRelease(raw) {
  if (!raw || typeof raw !== 'object') return null;
  if (raw.draft || raw.prerelease) return null;

  const version = versionText(raw.tag_name);
  if (!version) return null;

  const downloads = pickDownloads(raw.assets);
  // A release with no builds at all is a tag with notes attached, and has
  // nothing to offer a page about downloads.
  if (!PLATFORMS.some((platform) => downloads[platform])) return null;

  const tag = typeof raw.tag_name === 'string' ? raw.tag_name : `v${version}`;
  const page = typeof raw.html_url === 'string' &&
      raw.html_url.startsWith('https://github.com/')
    ? raw.html_url
    : `${RELEASES_PAGE}/tag/${encodeURIComponent(tag)}`;

  return {
    version,
    tag,
    page,
    published: typeof raw.published_at === 'string' ? raw.published_at : '',
    notes: summariseNotes(raw.body),
    downloads,
  };
}

/**
 * Every release worth listing in [payload], newest first.
 *
 * The API returns them in the order they were created, which is usually but
 * not reliably version order: a fix tagged after a larger release that was
 * prepared earlier arrives out of sequence, and the page would then open with
 * an older version than the one it should be offering.
 */
export function selectReleases(payload, { limit = 20 } = {}) {
  if (!Array.isArray(payload)) return [];

  const releases = [];
  const seen = new Set();
  for (const raw of payload) {
    const release = normalizeRelease(raw);
    if (!release || seen.has(release.version)) continue;
    seen.add(release.version);
    releases.push(release);
  }

  releases.sort((a, b) => compareVersions(b.version, a.version));
  return releases.slice(0, limit);
}

/**
 * The newest release in [releases] that actually has a [platform] build.
 *
 * Not simply the newest release. All three builds come out of one job, so they
 * normally arrive together, but a release can be edited by hand and an asset
 * can be deleted. Falling back to the last version that did ship for this
 * machine offers something that works, which beats an empty card.
 */
export function latestFor(releases, platform) {
  if (!Array.isArray(releases)) return null;
  return releases.find((release) => release.downloads[platform]) || null;
}

/**
 * The release notes with the "Downloads" section removed, as plain text.
 *
 * The release body opens with a Downloads section: a table of the three
 * builds, the macOS quarantine command and the SmartScreen note. All of that
 * is written for somebody reading the release on GitHub, and all of it is
 * already on this page, in better shape, a few centimetres higher up.
 * Repeating it under "What's new" reads as though it were news, every release.
 *
 * Headings keep their text and lose their `#`s, because the notes are rendered
 * as text -- see `page.js` for why nothing here goes near innerHTML.
 */
export function summariseNotes(body) {
  if (typeof body !== 'string') return '';

  const kept = [];
  let skippingBelow = 0;
  for (const line of body.replace(/\r\n/g, '\n').split('\n')) {
    const heading = /^(#{1,6})\s+(.*)$/.exec(line);
    if (heading) {
      const depth = heading[1].length;
      const text = heading[2].trim();
      // A deeper heading is still inside the section being skipped; one at the
      // same level or shallower ends it.
      if (skippingBelow && depth > skippingBelow) continue;
      skippingBelow = /^downloads?\b/i.test(text) ? depth : 0;
      if (skippingBelow) continue;
      kept.push(text);
      continue;
    }
    // "**Full Changelog**: https://github.com/..." closes GitHub's generated
    // notes. As plain text it is a bare URL, and the page already links to the
    // release it points at.
    if (/^\s*\*\*full changelog\*\*/i.test(line)) continue;
    if (!skippingBelow) kept.push(line);
  }

  return kept.join('\n').replace(/\n{3,}/g, '\n\n').trim();
}

/**
 * Which build [hints] describes a machine wanting, and how sure that is.
 *
 * Returns `{ platform, confident }`. `platform` is null for anything this does
 * not ship for, and `confident` is false when the page should offer an
 * alternative rather than assume it got it right.
 *
 * Telling an Apple silicon Mac from an Intel one is the whole difficulty here,
 * and it cannot be done from the user agent: every browser on macOS still
 * reports `Intel Mac OS X 10_15_7`, on every Mac, because changing it broke
 * too many sites. So three sources are tried in order of how much they can be
 * trusted:
 *
 *   1. `architecture` from the User-Agent Client Hints API, which is the
 *      browser answering the question directly. Chromium only.
 *   2. The WebGL renderer string, which names the GPU -- `Apple M2` on Apple
 *      silicon, `Intel Iris` or `AMD Radeon` on the Macs that came before it.
 *      Safari masks this in recent versions, hence the third case.
 *   3. Nothing, in which case Apple silicon is the guess, because every Mac
 *      sold since November 2020 is one. The page says so and puts the Intel
 *      build next to it rather than quietly hoping.
 */
export function detectPlatform(hints) {
  const {
    userAgent = '',
    architecture = '',
    renderer = '',
    touchPoints = 0,
  } = hints && typeof hints === 'object' ? hints : {};

  if (typeof userAgent !== 'string' || !userAgent) {
    return { platform: null, confident: false };
  }

  // Phones and tablets get nothing on purpose: there is no build they could
  // run. An iPad in its default "request desktop site" mode reports itself as
  // `Macintosh`, so the touch points are what tells it apart -- a Mac reports
  // 0, an iPad reports 5.
  if (/iPhone|iPad|iPod|Android/i.test(userAgent)) {
    return { platform: null, confident: false };
  }

  if (/Windows|Win64|Win32/i.test(userAgent)) {
    // Windows on ARM reports itself as x64 too, and runs x64 binaries under
    // emulation, so there is nothing to distinguish and nothing to gain from
    // distinguishing it: win-x64 is the only Windows build there is.
    return { platform: WIN_X64, confident: true };
  }

  if (!/Mac OS X|Macintosh|macOS/i.test(userAgent)) {
    return { platform: null, confident: false };
  }
  if (Number(touchPoints) > 0) {
    return { platform: null, confident: false };
  }

  if (/arm/i.test(architecture)) return { platform: OSX_ARM64, confident: true };
  if (/x86|x64|amd64/i.test(architecture)) {
    return { platform: OSX_X64, confident: true };
  }

  if (/Apple\s*(M\d|GPU|Silicon)/i.test(renderer)) {
    return { platform: OSX_ARM64, confident: true };
  }
  if (/Intel|AMD|Radeon|NVIDIA|GeForce/i.test(renderer)) {
    return { platform: OSX_X64, confident: true };
  }

  return { platform: OSX_ARM64, confident: false };
}

/**
 * Asset names this is willing to put inside a command it invites people to run.
 *
 * The name comes from the releases API, which is to say from whoever created
 * the release. It is only ever written to the page as text, so it cannot
 * execute anything there -- but the whole point of the line it appears in is
 * that a visitor copies it into a terminal, where `; rm -rf ~` in a filename
 * would execute perfectly well. Anything outside this alphabet falls back to
 * the generic command below.
 */
const SAFE_ASSET_NAME = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;

/**
 * The command that clears macOS's quarantine flag from [download].
 *
 * The builds are ad-hoc signed -- the release pipeline verifies that with
 * `codesign --verify` and refuses to publish without it -- but they are not
 * notarized, and Gatekeeper refuses anything quarantined that Apple has not
 * seen. It does so without saying anything: the process is killed on launch
 * with no dialog and nothing in the interface, which reads as a crash. Somebody
 * who thinks the app crashed never goes looking for a terminal command, so the
 * page has to put this in front of them before they try.
 *
 * The path is the folder the archive expands to. The release workflow builds
 * each zip around exactly one top-level folder named after the archive, so
 * stripping `.zip` gives the directory every unzip tool produces.
 */
export function quarantineCommand(download) {
  const generic = 'xattr -dr com.apple.quarantine ~/Downloads/UrDatabase-*';
  const name = download && typeof download.name === 'string' ? download.name : '';
  if (!SAFE_ASSET_NAME.test(name)) return generic;
  return `xattr -dr com.apple.quarantine ~/Downloads/${name.replace(/\.zip$/i, '')}`;
}

/** [bytes] as something a person reads, or '' when the size is unknown. */
export function formatSize(bytes) {
  const size = Number(bytes);
  if (!Number.isFinite(size) || size <= 0) return '';

  const mb = size / (1024 * 1024);
  if (mb < 1) return `${Math.max(1, Math.round(size / 1024))} KB`;
  if (mb < 1024) return `${mb < 10 ? mb.toFixed(1) : Math.round(mb)} MB`;
  return `${(mb / 1024).toFixed(1)} GB`;
}

/**
 * An ISO timestamp as a readable date, or '' if it is not one.
 *
 * Formatted in UTC. Read in the visitor's own zone, a release published at
 * 01:00 UTC shows as the previous day everywhere west of Greenwich, which does
 * not match the date on the GitHub release the page links to.
 */
export function formatDate(value, locale = 'en-GB') {
  if (typeof value !== 'string' || !value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return new Intl.DateTimeFormat(locale, {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
    timeZone: 'UTC',
  }).format(date);
}
