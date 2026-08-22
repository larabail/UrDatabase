/**
 * Tests for the downloads page's release logic.
 *
 * Run with: node --test web/downloads/*.test.js
 *
 * These cover the decisions the page cannot be eyeballed for. Whether it looks
 * right is answered by opening it. Whether it offers the newest build, the
 * right file for the machine asking, a command that is safe to paste into a
 * terminal, and something at all when GitHub is unreachable, is answered here
 * -- because each of those only misbehaves on a payload or a browser nobody
 * has in front of them.
 */

import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
  OSX_ARM64,
  OSX_X64,
  PLATFORMS,
  WIN_X64,
  assetPlatform,
  compareVersions,
  detectPlatform,
  formatDate,
  formatSize,
  isMac,
  latestFor,
  macosSigning,
  MACOS_NOTARIZED,
  MACOS_SIGNED,
  MACOS_UNKNOWN,
  MACOS_UNSIGNED,
  normalizeRelease,
  parseVersion,
  pickDownloads,
  selectReleases,
  summariseNotes,
  versionText,
} from './releases.js';

const DOWNLOAD = 'https://github.com/larabail/UrDatabase/releases/download';

/**
 * An asset the API would return, named the way the release workflow names one.
 *
 * macOS builds are disk images and Windows is a zip. That split is not
 * cosmetic: a notarization ticket staples to a bundle or an image and has
 * nowhere to go in a zip, and a zip loses the extended attributes that carry
 * most of a .NET bundle's nested signatures.
 */
function asset(version, rid, size = 74_000_000) {
  const name = `UrDatabase-${version}-${rid}${rid.startsWith('osx-') ? '.dmg' : '.zip'}`;
  return {
    name,
    size,
    browser_download_url: `${DOWNLOAD}/v${version}/${name}`,
  };
}

/** A release the API would return, with all three builds attached. */
function release(version, extra = {}) {
  return {
    tag_name: `v${version}`,
    html_url: `https://github.com/larabail/UrDatabase/releases/tag/v${version}`,
    published_at: '2026-08-21T09:00:00Z',
    body: 'Something changed.',
    assets: PLATFORMS.map((rid) => asset(version, rid)),
    ...extra,
  };
}

/** The user agent every browser on macOS reports, on every Mac ever made. */
const MAC_UA =
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 '
  + '(KHTML, like Gecko) Version/17.0 Safari/605.1.15';

const WINDOWS_UA =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 '
  + '(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36';

describe('parseVersion', () => {
  it('reads a plain version', () => {
    assert.deepEqual(parseVersion('0.1.0'), [0, 1, 0]);
  });

  it('reads a tag', () => {
    assert.deepEqual(parseVersion('v1.4.2'), [1, 4, 2]);
  });

  it('fills in a missing patch', () => {
    assert.deepEqual(parseVersion('2.1'), [2, 1, 0]);
  });

  it('refuses anything that is not a version', () => {
    for (const raw of ['', 'latest', 'v', '1.2.3.4', '1.x.0', null, 42]) {
      assert.equal(parseVersion(raw), null, `accepted ${JSON.stringify(raw)}`);
    }
  });

  it('normalises through versionText', () => {
    assert.equal(versionText('v2.1'), '2.1.0');
    assert.equal(versionText('nope'), null);
  });
});

describe('compareVersions', () => {
  it('orders by number rather than as text', () => {
    // The whole reason this is not a string comparison: as text "0.9.0" sorts
    // after "0.10.0", and the page would open with the older build.
    assert.ok(compareVersions('0.9.0', '0.10.0') < 0);
    assert.ok(compareVersions('1.0.0', '0.99.99') > 0);
    assert.equal(compareVersions('1.2.3', 'v1.2.3'), 0);
  });

  it('leaves an unreadable version where it is rather than reordering', () => {
    assert.equal(compareVersions('nonsense', '1.0.0'), 0);
  });
});

describe('assetPlatform', () => {
  it('recognises all three builds', () => {
    assert.equal(assetPlatform('UrDatabase-0.2.1-osx-arm64.dmg'), OSX_ARM64);
    assert.equal(assetPlatform('UrDatabase-0.2.1-osx-x64.dmg'), OSX_X64);
    assert.equal(assetPlatform('UrDatabase-0.2.1-win-x64.zip'), WIN_X64);
  });

  it('still recognises the zips released before 0.2.1', () => {
    // The page lists older releases too, and those shipped the macOS builds
    // as zips. Dropping them would leave every historical row with no macOS
    // download rather than with an old one, which is a worse answer than the
    // truth: it shipped, and it will not open on a current Mac.
    assert.equal(assetPlatform('UrDatabase-0.2.0-osx-arm64.zip'), OSX_ARM64);
    assert.equal(assetPlatform('UrDatabase-0.1.0-osx-x64.zip'), OSX_X64);
  });

  it('does not confuse the two x64 builds', () => {
    // Both names end in "-x64". Matching on the architecture alone would hand
    // Windows users an Intel Mac build, or the reverse.
    assert.notEqual(
      assetPlatform('UrDatabase-0.2.1-osx-x64.dmg'),
      assetPlatform('UrDatabase-0.2.1-win-x64.zip'),
    );
    assert.notEqual(
      assetPlatform('UrDatabase-0.2.0-osx-x64.zip'),
      assetPlatform('UrDatabase-0.2.0-win-x64.zip'),
    );
  });

  it('ignores checksums and the sums file', () => {
    // "...-osx-arm64.dmg.sha256" contains the whole macOS suffix. Offering a
    // 90-byte text file as the application is worse than offering nothing.
    assert.equal(assetPlatform('UrDatabase-0.2.1-osx-arm64.dmg.sha256'), null);
    assert.equal(assetPlatform('UrDatabase-0.1.0-osx-arm64.zip.sha256'), null);
    assert.equal(assetPlatform('SHA256SUMS.txt'), null);
  });

  it('ignores anything that is not one of ours', () => {
    for (const name of ['UrDatabase-0.2.1-linux-x64.zip', 'source.tar.gz',
                        'UrDatabase-0.2.1-win-x64.dmg.txt', '', null]) {
      assert.equal(assetPlatform(name), null, `accepted ${JSON.stringify(name)}`);
    }
  });
});

describe('pickDownloads', () => {
  it('finds each build and keeps its size', () => {
    const downloads = pickDownloads([asset('0.2.1', OSX_ARM64, 1234)]);
    assert.equal(downloads[OSX_ARM64].name, 'UrDatabase-0.2.1-osx-arm64.dmg');
    assert.equal(downloads[OSX_ARM64].size, 1234);
  });

  it('reports a missing build as null rather than leaving it out', () => {
    // "there is no Windows build" and "this is not a release" have to be
    // distinguishable, because the page says different things about them.
    const downloads = pickDownloads([asset('0.1.0', OSX_ARM64)]);
    assert.equal(downloads[WIN_X64], null);
    assert.ok(WIN_X64 in downloads);
  });

  it('refuses a link that is not https', () => {
    // An asset URL is data from a server, and a `javascript:` URL in an href
    // runs when somebody clicks it.
    const downloads = pickDownloads([
      {
        name: 'UrDatabase-0.1.0-win-x64.zip',
        size: 10,
        browser_download_url: 'javascript:alert(1)',
      },
    ]);
    assert.equal(downloads[WIN_X64], null);
  });

  it('survives junk in the asset list', () => {
    const downloads = pickDownloads([null, 'nope', {}, asset('0.1.0', WIN_X64)]);
    assert.equal(downloads[WIN_X64].name, 'UrDatabase-0.1.0-win-x64.zip');
  });

  it('answers with three nulls when handed nothing', () => {
    const downloads = pickDownloads(undefined);
    for (const platform of PLATFORMS) assert.equal(downloads[platform], null);
  });
});

describe('normalizeRelease', () => {
  it('reads a release', () => {
    const normalized = normalizeRelease(release('0.2.0'));
    assert.equal(normalized.version, '0.2.0');
    assert.equal(normalized.tag, 'v0.2.0');
    assert.equal(normalized.downloads[OSX_X64].size, 74_000_000);
  });

  it('drops a draft, which nobody but a maintainer can download', () => {
    assert.equal(normalizeRelease(release('0.2.0', { draft: true })), null);
  });

  it('drops a pre-release, which this pipeline never publishes', () => {
    assert.equal(normalizeRelease(release('0.2.0', { prerelease: true })), null);
  });

  it('drops a release with no builds attached', () => {
    assert.equal(normalizeRelease(release('0.2.0', { assets: [] })), null);
  });

  it('drops a tag that is not a version', () => {
    assert.equal(normalizeRelease(release('0.2.0', { tag_name: 'nightly' })), null);
  });

  it('falls back to a release page it builds itself', () => {
    const normalized = normalizeRelease(release('0.2.0', { html_url: 'ftp://x' }));
    assert.ok(normalized.page.startsWith('https://github.com/larabail/UrDatabase'));
  });
});

describe('selectReleases', () => {
  it('sorts newest first regardless of the order the API used', () => {
    // The API lists them in creation order, which is not version order when a
    // fix is tagged after a larger release that was prepared earlier.
    const releases = selectReleases([release('0.9.0'), release('0.10.0')]);
    assert.deepEqual(releases.map((r) => r.version), ['0.10.0', '0.9.0']);
  });

  it('keeps one row per version', () => {
    const releases = selectReleases([release('1.0.0'), release('1.0.0')]);
    assert.equal(releases.length, 1);
  });

  it('honours a limit', () => {
    const many = ['1.0.0', '1.1.0', '1.2.0', '1.3.0'].map((v) => release(v));
    assert.equal(selectReleases(many, { limit: 2 }).length, 2);
  });

  it('answers with nothing when handed nothing', () => {
    assert.deepEqual(selectReleases(null), []);
    assert.deepEqual(selectReleases({ message: 'API rate limit exceeded' }), []);
  });
});

describe('latestFor', () => {
  it('skips back to the last release that had this build', () => {
    // All three normally ship together, but an asset can be deleted from a
    // release by hand. An older working build beats an empty card.
    const releases = selectReleases([
      release('0.3.0', { assets: [asset('0.3.0', WIN_X64)] }),
      release('0.2.0'),
    ]);
    assert.equal(latestFor(releases, WIN_X64).version, '0.3.0');
    assert.equal(latestFor(releases, OSX_ARM64).version, '0.2.0');
  });

  it('answers null when no release ever had one', () => {
    const releases = selectReleases([
      release('0.3.0', { assets: [asset('0.3.0', WIN_X64)] }),
    ]);
    assert.equal(latestFor(releases, OSX_ARM64), null);
    assert.equal(latestFor(null, OSX_ARM64), null);
  });
});

describe('detectPlatform', () => {
  it('recognises Windows', () => {
    assert.deepEqual(detectPlatform({ userAgent: WINDOWS_UA }), {
      platform: WIN_X64,
      confident: true,
    });
  });

  it('believes the client hint over the user agent', () => {
    // The user agent says "Intel Mac OS X" on every Mac ever made, including
    // every Apple silicon one. The architecture hint is the browser answering
    // the question directly, so it wins.
    assert.deepEqual(
      detectPlatform({ userAgent: MAC_UA, architecture: 'arm' }),
      { platform: OSX_ARM64, confident: true },
    );
    assert.deepEqual(
      detectPlatform({ userAgent: MAC_UA, architecture: 'x86' }),
      { platform: OSX_X64, confident: true },
    );
  });

  it('falls back to the GPU name where there is no client hint', () => {
    for (const renderer of [
      'Apple GPU',
      'ANGLE (Apple, ANGLE Metal Renderer: Apple M2 Pro, Unspecified)',
    ]) {
      assert.deepEqual(detectPlatform({ userAgent: MAC_UA, renderer }), {
        platform: OSX_ARM64,
        confident: true,
      }, renderer);
    }

    for (const renderer of [
      'ANGLE (Intel, Intel(R) Iris(TM) Plus Graphics, OpenGL 4.1)',
      'AMD Radeon Pro 5500M OpenGL Engine',
    ]) {
      assert.deepEqual(detectPlatform({ userAgent: MAC_UA, renderer }), {
        platform: OSX_X64,
        confident: true,
      }, renderer);
    }
  });

  it('guesses Apple silicon when nothing will say, and admits it', () => {
    // Safari masks the renderer string and has no client hints, so this is
    // the ordinary case on the most common Mac browser. Apple silicon is the
    // better guess -- every Mac sold since late 2020 is one -- but the page
    // has to offer the alternative rather than assume, which is what
    // `confident: false` is for.
    assert.deepEqual(detectPlatform({ userAgent: MAC_UA }), {
      platform: OSX_ARM64,
      confident: false,
    });
  });

  it('offers nothing to a phone or tablet', () => {
    const iphone = 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)';
    const android = 'Mozilla/5.0 (Linux; Android 14; Pixel 8)';
    for (const userAgent of [iphone, android]) {
      assert.equal(detectPlatform({ userAgent }).platform, null, userAgent);
    }
  });

  it('is not fooled by an iPad claiming to be a Mac', () => {
    // iPadOS in its default "desktop site" mode reports itself as Macintosh,
    // byte for byte. The touch points are the only thing that differs, and
    // without this the page offers a 90MB macOS build to a device that cannot
    // open it.
    assert.equal(
      detectPlatform({ userAgent: MAC_UA, touchPoints: 5 }).platform,
      null,
    );
    assert.equal(detectPlatform({ userAgent: MAC_UA, touchPoints: 0 }).platform,
      OSX_ARM64);
  });

  it('offers nothing on a platform there is no build for', () => {
    assert.equal(
      detectPlatform({ userAgent: 'Mozilla/5.0 (X11; Linux x86_64)' }).platform,
      null,
    );
  });

  it('survives being handed nothing at all', () => {
    for (const hints of [undefined, null, {}, { userAgent: '' }, 'nope']) {
      assert.deepEqual(detectPlatform(hints), { platform: null, confident: false });
    }
  });
});

describe('isMac', () => {
  it('covers both Mac builds and nothing else', () => {
    assert.ok(isMac(OSX_ARM64));
    assert.ok(isMac(OSX_X64));
    assert.ok(!isMac(WIN_X64));
    assert.ok(!isMac(null));
  });
});

describe('macosSigning', () => {
  it('reads the marker the release workflow writes', () => {
    assert.equal(macosSigning('## Downloads\n\n<!-- urdatabase:macos-signing=notarized -->\n'),
      MACOS_NOTARIZED);
    assert.equal(macosSigning('<!-- urdatabase:macos-signing=signed -->'),
      MACOS_SIGNED);
    assert.equal(macosSigning('<!-- urdatabase:macos-signing=unsigned -->'),
      MACOS_UNSIGNED);
  });

  it('calls a release with no marker unknown, not signed', () => {
    // Every release up to 0.2.0 is one of these, and every one of them is
    // ad-hoc signed and killed on launch by a current Mac. Defaulting the
    // other way would put "it opens normally" on the page for exactly the
    // builds that do not.
    for (const body of ['', null, undefined, 42, '## Downloads\n\nnothing here',
                        '<!-- urdatabase:something-else=notarized -->']) {
      assert.equal(macosSigning(body), MACOS_UNKNOWN,
        `read a state out of ${JSON.stringify(body)}`);
    }
  });

  it('refuses a state the page has no sentence for', () => {
    // The body comes from whoever created the release. Passing an unrecognised
    // value through would put a bare word into the DOM where a sentence
    // belongs.
    assert.equal(macosSigning('<!-- urdatabase:macos-signing=probably -->'),
      MACOS_UNKNOWN);
    assert.equal(macosSigning('<!-- urdatabase:macos-signing= -->'),
      MACOS_UNKNOWN);
  });

  it('reaches the page through normalizeRelease', () => {
    const raw = release('0.2.1');
    raw.body = '## Downloads\n\n<!-- urdatabase:macos-signing=notarized -->\n';
    assert.equal(normalizeRelease(raw).macosSigning, MACOS_NOTARIZED);

    const older = release('0.2.0');
    older.body = '## Downloads\n\nRun xattr.\n';
    assert.equal(normalizeRelease(older).macosSigning, MACOS_UNKNOWN);
  });
});

describe('summariseNotes', () => {
  it('drops the machine-readable marker rather than printing it', () => {
    // These notes are rendered as text, so an HTML comment left in them shows
    // up verbatim under "What's new".
    const notes = summariseNotes([
      '<!-- urdatabase:macos-signing=notarized -->',
      "## What's Changed",
      '* Faster scanning',
    ].join('\n'));
    assert.ok(!notes.includes('urdatabase:macos-signing'), notes);
    assert.ok(!notes.includes('<!--'), notes);
    assert.ok(notes.includes('Faster scanning'), notes);
  });

  it('drops the Downloads section the release workflow writes', () => {
    // That section is a table of the three builds and the SmartScreen note --
    // all of which this page already says, higher up and in better shape.
    // Repeated under "What's new" it reads as news.
    const body = [
      '## Downloads',
      '',
      '| Machine | File |',
      '| --- | --- |',
      '| Mac | `UrDatabase-0.2.1-osx-arm64.dmg` |',
      '',
      '### Notarization',
      'Signed and notarized.',
      '',
      "## What's Changed",
      '* Faster scanning',
    ].join('\n');

    const notes = summariseNotes(body);
    assert.ok(!notes.includes('osx-arm64'), notes);
    assert.ok(!notes.includes('Signed and notarized'), notes);
    assert.ok(notes.includes('Faster scanning'), notes);
    assert.ok(notes.startsWith("What's Changed"), notes);
  });

  it('drops the Full Changelog line, which is a bare URL as text', () => {
    const notes = summariseNotes(
      "## What's Changed\n* A fix\n\n"
      + '**Full Changelog**: https://github.com/larabail/UrDatabase/compare/v1...v2',
    );
    assert.ok(!notes.includes('compare'), notes);
    assert.ok(notes.includes('A fix'));
  });

  it('strips the hashes off headings it keeps', () => {
    // The notes are rendered with textContent, so a literal "## " would show.
    assert.equal(summariseNotes('## Fixes\nOne thing.'), 'Fixes\nOne thing.');
  });

  it('collapses the run of blank lines a removed section leaves', () => {
    assert.ok(!summariseNotes('## Downloads\n\n\na\n\n\n\nb').includes('\n\n\n'));
  });

  it('survives a release with no body', () => {
    assert.equal(summariseNotes(undefined), '');
    assert.equal(summariseNotes(''), '');
  });
});

describe('formatSize', () => {
  it('reads as a person would say it', () => {
    assert.equal(formatSize(74 * 1024 * 1024), '74 MB');
    assert.equal(formatSize(9.5 * 1024 * 1024), '9.5 MB');
    assert.equal(formatSize(2 * 1024 * 1024 * 1024), '2.0 GB');
    assert.equal(formatSize(4096), '4 KB');
  });

  it('says nothing rather than "0 B" when the size is unknown', () => {
    for (const bytes of [0, -1, null, undefined, 'big']) {
      assert.equal(formatSize(bytes), '', `formatted ${JSON.stringify(bytes)}`);
    }
  });
});

describe('formatDate', () => {
  it('formats in UTC, matching the date on the GitHub release', () => {
    // Read in the visitor's own zone, a release published at 01:00 UTC shows
    // as the previous day everywhere west of Greenwich.
    assert.equal(formatDate('2026-08-21T01:00:00Z'), '21 August 2026');
  });

  it('says nothing rather than "Invalid Date"', () => {
    for (const value of ['', 'soon', null, undefined]) {
      assert.equal(formatDate(value), '', `formatted ${JSON.stringify(value)}`);
    }
  });
});
