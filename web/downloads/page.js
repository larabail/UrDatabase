/**
 * Filling the downloads page in from whatever is actually downloadable.
 *
 * The page is served with every button already pointing at the GitHub releases
 * page, which is a correct if unhelpful answer. This replaces them with the
 * specific builds from the newest release, works out which one this machine
 * wants, and lists the older versions underneath. When that cannot be done the
 * page is left as it was served rather than emptied: a page that still leads
 * somewhere beats a blank one.
 *
 * Nothing here is written with innerHTML. Release names and notes are text
 * that came from a server, and a page whose entire purpose is handing out
 * executables is the last place that should be executing markup it was given.
 */

import {
  MACOS_NOTARIZED,
  MACOS_SIGNED,
  MACOS_UNSIGNED,
  OSX_ARM64,
  OSX_X64,
  PLATFORMS,
  RELEASES_API,
  RELEASES_PAGE,
  WIN_X64,
  detectPlatform,
  formatDate,
  formatSize,
  isMac,
  latestFor,
  selectReleases,
} from './releases.js';

/** What each build is called where there is room to say it properly. */
const PLATFORM_NAMES = {
  [OSX_ARM64]: 'Mac with Apple silicon',
  [OSX_X64]: 'Mac with an Intel processor',
  [WIN_X64]: 'Windows',
};

/** And where there is not -- on a button, in a table cell. */
const SHORT_NAMES = {
  [OSX_ARM64]: 'Apple silicon',
  [OSX_X64]: 'Intel Mac',
  [WIN_X64]: 'Windows',
};

const el = (id) => document.getElementById(id);

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) throw new Error(`${url} answered ${response.status}`);
  return response.json();
}

/**
 * Everything that can be found out about the machine asking.
 *
 * Gathered here rather than inside `detectPlatform` so that the decision stays
 * a pure function of its inputs and can be tested against an Apple silicon Mac
 * without owning one. Every source is optional and every failure is ignored:
 * the fallback is a guess the page admits to, which is much better than a
 * blank page because a WebGL context could not be created.
 */
async function platformHints() {
  const hints = {
    userAgent: navigator.userAgent || '',
    touchPoints: navigator.maxTouchPoints || 0,
    architecture: '',
    renderer: '',
  };

  try {
    const data = navigator.userAgentData;
    if (data && typeof data.getHighEntropyValues === 'function') {
      const high = await data.getHighEntropyValues(['architecture']);
      hints.architecture = high.architecture || '';
    }
  } catch (error) {
    // Chromium can refuse the request outright in some embedded contexts.
    console.debug('Client hints were unavailable:', error);
  }

  try {
    // The GPU name is the only thing a non-Chromium browser exposes that
    // differs between an Apple silicon Mac and an Intel one. Safari masks it
    // in recent versions, which is handled by there being a third case in
    // detectPlatform rather than by trying harder here.
    const canvas = document.createElement('canvas');
    const gl = canvas.getContext('webgl') || canvas.getContext('experimental-webgl');
    const info = gl && gl.getExtension('WEBGL_debug_renderer_info');
    if (info) {
      hints.renderer = String(gl.getParameter(info.UNMASKED_RENDERER_WEBGL) || '');
    }
  } catch (error) {
    console.debug('The WebGL renderer was unavailable:', error);
  }

  return hints;
}

/**
 * Every published release, and whether the answer is trustworthy.
 *
 * There is no second source to fall back to, on purpose. The obvious one is a
 * manifest written into the site at release time, but this site is deployed on
 * its own schedule and never by a release, so such a file would be stale by
 * construction -- and a stale download link is worse than an honest "could not
 * load", because nothing about it looks wrong.
 */
async function loadReleases() {
  try {
    const payload = await fetchJson(RELEASES_API, {
      // A safelisted header, so this stays a simple request and no preflight
      // is needed.
      headers: { Accept: 'application/vnd.github+json' },
    });
    const releases = selectReleases(payload);
    // "GitHub answered and there is genuinely nothing yet" is a different
    // thing to tell somebody than "we could not ask".
    return { releases, source: releases.length ? 'github' : 'empty' };
  } catch (error) {
    console.warn('The releases API could not be read:', error);
    return { releases: [], source: 'unavailable' };
  }
}

function showStatus(message) {
  const status = el('status');
  status.textContent = message;
  status.hidden = false;
}

/** A line somebody can act on: which version, how big, and when. */
function describeDownload(release, download) {
  return [
    `Version ${release.version}`,
    formatSize(download.size),
    formatDate(release.published),
  ].filter(Boolean).join(' \u00b7 ');
}

function renderHero(releases, detected) {
  const button = el('hero-button');
  const meta = el('hero-meta');
  const hint = el('hero-hint');
  const latest = releases[0];

  if (!latest) {
    button.textContent = 'Browse the releases on GitHub';
    button.href = RELEASES_PAGE;
    meta.textContent = '';
    return;
  }

  const mine = detected.platform ? latestFor(releases, detected.platform) : null;
  if (!mine) {
    // A machine there is no build for -- Linux, or a phone. Neither wants a
    // headline button that has to guess, so the cards below do the work.
    button.hidden = true;
    meta.textContent =
      `Latest version ${latest.version}, released ${formatDate(latest.published)}. `
      + 'Every build is listed below.';
    return;
  }

  const download = mine.downloads[detected.platform];
  button.href = download.url;
  button.textContent = `Download for ${SHORT_NAMES[detected.platform]}`;
  meta.textContent = describeDownload(mine, download);

  // Said out loud when the guess is a guess. Offering the wrong Mac build is
  // not a small mistake: an Intel binary on Apple silicon starts under
  // Rosetta if it is installed and refuses to open at all if it is not, and
  // nothing in either outcome suggests "you took the wrong download".
  if (!detected.confident && isMac(detected.platform)) {
    const other = detected.platform === OSX_ARM64 ? OSX_X64 : OSX_ARM64;
    const otherRelease = latestFor(releases, other);

    hint.textContent =
      'This browser will not say which processor your Mac has, so the button '
      + 'above offers the Apple silicon build \u2014 every Mac sold since late '
      + '2020. On an older Intel Mac, take this one instead: ';
    if (otherRelease) {
      const link = document.createElement('a');
      link.href = otherRelease.downloads[other].url;
      link.textContent = `UrDatabase for an ${SHORT_NAMES[other]}`;
      hint.appendChild(link);
      hint.append('. In doubt? \uD83C\uDF4E menu \u2192 About This Mac.');
    } else {
      hint.append('see the builds below.');
    }
    hint.hidden = false;
  }
}

function renderCard(platform, releases, detected) {
  const card = el(`card-${platform}`);
  const button = el(`button-${platform}`);
  const asset = el(`asset-${platform}`);

  if (detected.platform === platform && detected.confident) {
    card.classList.add('yours');
    el(`badge-${platform}`).hidden = false;
  }

  const release = latestFor(releases, platform);
  if (!release) {
    button.setAttribute('aria-disabled', 'true');
    button.textContent = 'No build yet';
    asset.textContent = '';
    return;
  }

  const download = release.downloads[platform];
  button.href = download.url;
  button.textContent = `Download for ${SHORT_NAMES[platform]}`;
  asset.textContent = describeDownload(release, download);
  // The exact filename is what a checksum is verified against. It is long and
  // nearly identical on all three rows, so it goes somewhere it can be asked
  // for rather than taking up a line.
  asset.title = download.name;

  // Only worth saying when it is surprising: this platform's newest build is
  // older than the release the rest of the page is about.
  if (releases[0] && releases[0].version !== release.version) {
    asset.textContent += ` \u00b7 newest ${SHORT_NAMES[platform]} build`;
  }
}

/**
 * What the newest release says will happen when a Mac opens it.
 *
 * One sentence per state, and the page shows none of them until it knows which
 * one applies. The markup carries no claim about signing precisely because the
 * page is deployed independently of any release: baked in, "it is signed and
 * notarized" would keep being said through a release where the certificate was
 * missing and the build cannot start at all.
 *
 * Only `notarized` is good news. The other three are ordered by how much the
 * visitor can do about it: a signed build can be opened past Gatekeeper by
 * hand, an ad-hoc one cannot be opened at all.
 */
const MACOS_VERDICT = {
  [MACOS_NOTARIZED]: {
    bad: false,
    text: 'This build is signed with an Apple Developer ID and notarized by '
      + 'Apple, so it opens the first time like anything else. There is no '
      + 'terminal command and nothing to clear.',
  },
  [MACOS_SIGNED]: {
    bad: true,
    text: 'This build is signed but was not notarized, so macOS refuses it on '
      + 'first launch. Right-click UrDatabase in Applications, choose Open, '
      + 'and confirm. After that it opens normally.',
  },
  [MACOS_UNSIGNED]: {
    bad: true,
    text: 'This build is not signed, and macOS kills an unsigned download the '
      + 'moment it starts \u2014 no dialog, no error, nothing in the '
      + 'interface. There is no way around it: clearing the quarantine flag '
      + 'does not help, because it is the signature being refused. Build it '
      + 'from source, or wait for a signed release.',
  },
};

/** Said when the release does not say, which is every release before 0.2.1. */
const MACOS_UNKNOWN_VERDICT = {
  bad: true,
  text: 'This release does not record whether its Mac builds were signed, '
    + 'which means it was published before 0.2.1 or made by hand. Assume not: '
    + 'macOS kills an unsigned download the moment it starts, with nothing '
    + 'shown at all, and clearing the quarantine flag does not help.',
};

function renderFirstRun(latest) {
  // Left hidden when there is no release to describe. That happens when the
  // API could not be reached, and the page already says so in its status line
  // -- warning somebody that a build might be unsigned when the real problem
  // is GitHub's rate limit would be its own kind of wrong.
  if (!latest) return;

  const verdict = MACOS_VERDICT[latest.macosSigning] || MACOS_UNKNOWN_VERDICT;
  const node = el('macos-signing');
  node.textContent = verdict.text;
  node.classList.toggle('bad', verdict.bad);
  node.hidden = false;
}

function renderNotes(latest) {
  if (!latest) return;

  el('notes-link').href = latest.page;
  el('checksums-link').href = latest.page;
  if (!latest.notes) return;

  el('notes-heading').textContent = `What\u2019s new in ${latest.version}`;
  el('notes-body').textContent = latest.notes;
  el('notes').hidden = false;
}

function renderHistory(releases) {
  // One row under a heading reading "Every version" tells nobody anything the
  // buttons above have not already said.
  if (releases.length < 2) return;

  const list = el('history-list');
  for (const release of releases) {
    const row = document.createElement('li');

    const version = document.createElement('span');
    version.className = 'ver';
    version.textContent = release.version;
    row.appendChild(version);

    const when = document.createElement('span');
    when.className = 'when';
    when.textContent = formatDate(release.published);
    row.appendChild(when);

    const links = document.createElement('span');
    links.className = 'links';
    for (const platform of PLATFORMS) {
      const download = release.downloads[platform];
      if (download) {
        const link = document.createElement('a');
        link.href = download.url;
        link.textContent = SHORT_NAMES[platform];
        link.title = download.name;
        links.appendChild(link);
      } else {
        // Kept in place rather than dropped, so the columns line up and a
        // release that shipped for two platforms out of three is visibly that.
        const missing = document.createElement('span');
        missing.textContent = SHORT_NAMES[platform];
        links.appendChild(missing);
      }
    }
    row.appendChild(links);

    list.appendChild(row);
  }
  el('history').hidden = false;
}

function render({ releases, source }, detected) {
  renderHero(releases, detected);
  for (const platform of PLATFORMS) renderCard(platform, releases, detected);
  renderFirstRun(releases[0]);
  renderNotes(releases[0]);
  renderHistory(releases);

  if (source === 'empty') {
    showStatus('No build has been published yet. The buttons lead to the '
      + 'releases page, where the first one will appear.');
    el('hero-meta').textContent = '';
  } else if (source === 'unavailable') {
    showStatus('The list of downloads could not be loaded \u2014 GitHub limits '
      + 'how often it can be asked. Every build is on the releases page.');
    el('hero-meta').textContent = '';
  }
}

platformHints()
  .then(async (hints) => render(await loadReleases(), detectPlatform(hints)))
  .catch((error) => {
    // Getting here is a bug in this file rather than a network problem, and it
    // must not leave the page half-updated: "Checking..." under a button that
    // still says "Download" claims a load is in progress that has already
    // given up.
    console.error('The downloads page could not be filled in:', error);
    el('hero-meta').textContent = '';
    for (const platform of PLATFORMS) el(`asset-${platform}`).textContent = '';
    showStatus('The list of downloads could not be loaded. Every build is on '
      + 'the releases page.');
  });
