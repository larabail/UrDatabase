/**
 * Tests that the page still gives page.js everything it reaches for.
 *
 * Run with: node --test web/downloads/*.test.js
 *
 * `releases.test.js` covers the logic. This covers the seam between that logic
 * and the document, which is what a restyle breaks: rename an id while moving
 * markup around and the page still looks finished, still deploys, and lists no
 * downloads at all. Nothing else would notice, because the failure is a `null`
 * in somebody else's browser.
 *
 * The required ids are read out of `page.js` rather than written down twice,
 * so this cannot drift from the script it is protecting.
 */

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';

const HERE = dirname(fileURLToPath(import.meta.url));
const html = readFileSync(join(HERE, 'index.html'), 'utf8');
const script = readFileSync(join(HERE, 'page.js'), 'utf8');

/** The runtime identifiers page.js loops over when it builds an id. */
const PLATFORMS = ['osx-arm64', 'osx-x64', 'win-x64'];

/** Every id page.js looks up, taken from the script itself. */
function requiredIds() {
  const ids = new Set();

  for (const match of script.matchAll(/el\('([^']+)'\)/g)) {
    ids.add(match[1]);
  }

  // ``el(`asset-${platform}`)`` and friends, expanded over the platform list.
  for (const match of script.matchAll(/el\(`([^`]+)`\)/g)) {
    for (const platform of PLATFORMS) {
      ids.add(match[1].replace('${platform}', platform));
    }
  }

  return [...ids];
}

describe('the markup page.js depends on', () => {
  it('finds every id the script looks up', () => {
    const missing = requiredIds().filter((id) => !html.includes(`id="${id}"`));
    assert.deepEqual(missing, [],
      `page.js looks these up and the page has none of them: ${missing.join(', ')}`);
  });

  it('looks up a plausible number of ids', () => {
    // Guards the patterns above: if they ever stop matching, every id would
    // "exist" and this file would pass while checking nothing.
    assert.ok(requiredIds().length >= 14,
      `only found ${requiredIds().length} ids in page.js, which suggests the `
      + 'patterns in this test stopped matching');
  });

  it('has a card, a button, a badge and an asset line per build', () => {
    for (const platform of PLATFORMS) {
      for (const prefix of ['card', 'button', 'badge', 'asset']) {
        assert.ok(html.includes(`id="${prefix}-${platform}"`),
          `no ${prefix}-${platform} in the page`);
      }
    }
  });

  it('styles the classes the script assigns', () => {
    // page.js sets these on elements it creates or changes, so they are not in
    // the committed markup and nothing else would catch the styling going.
    for (const cls of ['yours', 'secondary', 'links', 'ver', 'when']) {
      assert.ok(html.includes(`.${cls}`), `no styling for .${cls}`);
    }
  });

  it('keeps the rule that makes the hidden attribute win', () => {
    // Several sections are held back with `hidden` until they have content,
    // and a class that sets `display` outranks the browser's own rule for it.
    // Without this the "Yours" badge shows on all three cards at once.
    assert.match(html, /\[hidden\]\s*\{\s*display:\s*none\s*!important/);
  });
});

describe('the page without its script', () => {
  it('leads to the releases page from every button', () => {
    // The page is served fully written, so it is useful before page.js runs
    // and if it never does.
    const buttons = [...html.matchAll(/<a class="button"[^>]*id="([^"]+)"[^>]*href="([^"]+)"/g)];
    assert.ok(buttons.length >= 4, 'expected the hero button and all three builds');
    for (const [, id, href] of buttons) {
      assert.ok(href.startsWith('https://github.com/larabail/UrDatabase/releases'),
        `${id} does not fall back to the releases page`);
    }
  });

  it('tells somebody with no JavaScript where to go', () => {
    assert.match(html, /<noscript>/);
  });

  it('ships a quarantine command that is safe before it is filled in', () => {
    // The committed placeholder is what a visitor sees if the script never
    // runs, so it has to be a real command rather than a `<version>`
    // placeholder somebody would paste verbatim.
    assert.match(html, /id="quarantine-command">xattr -dr com\.apple\.quarantine /);
  });
});

describe('what the page says', () => {
  it('is honest that the builds are unsigned', () => {
    // Not a detail. Gatekeeper refuses to open an unsigned download and
    // reports it as damaged, so a page that implies these are signed sends
    // people off re-downloading a file that was never broken.
    assert.match(html, /not code signed and not notarized/i);
    assert.ok(html.includes('xattr -dr com.apple.quarantine'));
    assert.match(html, /Windows protected your PC/);
  });

  it('says what UrDatabase is and links to the repository', () => {
    assert.ok(html.includes('https://github.com/larabail/UrDatabase"'),
      'no link to the repository itself');
    assert.match(html, /SQLite/);
  });

  it('names all three builds in prose, not only in ids', () => {
    for (const label of ['Apple silicon', 'Intel Mac', 'Windows']) {
      assert.ok(html.includes(label), `the page never mentions ${label}`);
    }
  });

  it('credits both metadata sources', () => {
    for (const source of ['TMDB', 'OMDb']) {
      assert.ok(html.includes(source), `no credit for ${source}`);
    }
  });
});

describe('the page as it is drawn', () => {
  it('falls back to a system face for every webfont', () => {
    // Three families come from Google Fonts, because the character of this
    // page is the design rather than decoration on it. A blocked or failed
    // request has to cost the page its voice and nothing else, and that only
    // holds while every stack ends in a generic family the machine already
    // has. Without it a blocked request leaves the whole page in the
    // browser's default face, at sizes and spacing chosen for another one.
    const stacks = [...html.matchAll(/--(?:disp|prose|mono):\s*([^;]+);/g)]
      .map((match) => match[1].split(',').map((family) => family.trim()));

    assert.equal(stacks.length, 3, 'expected a display, a prose and a mono stack');
    for (const families of stacks) {
      assert.match(families.at(-1), /^(?:serif|sans-serif|monospace)$/,
        `${families[0]} ends in ${families.at(-1)}, which is not a generic family`);
    }
  });

  it('gives the "Yours" stamp a card to sit on', () => {
    // The stamp is positioned against its card rather than laid out in it,
    // so the card is its containing block. Take the positioning off the card
    // and the stamp flies to the corner of the page -- on a page that still
    // looks finished, still passes every other test here, and now tells
    // somebody the wrong thing about which build is theirs.
    assert.match(html, /\.card\s*\{[^}]*position:\s*relative/,
      'the .badge stamp is absolutely positioned and .card no longer contains it');
  });
});

describe('the page as a document', () => {
  it('is one well formed page with a single h1', () => {
    assert.ok(html.includes('<html lang="en">'));
    assert.equal((html.match(/<h1\b/g) || []).length, 1);
    assert.ok(html.trimEnd().endsWith('</html>'));
  });

  it('loads page.js as a module, which is how it is written', () => {
    assert.match(html, /<script type="module" src="page\.js"><\/script>/);
  });

  it('references no file the site does not ship', () => {
    // Three files are deployed: this page, page.js and releases.js. A local
    // reference to anything else is a 404 that only shows up in production.
    const shipped = new Set(['page.js', 'releases.js', 'index.html']);
    for (const match of html.matchAll(/(?:href|src)="(?!https?:|#|data:|mailto:)([^"]+)"/g)) {
      assert.ok(shipped.has(match[1].replace(/^\.?\//, '')),
        `${match[1]} is referenced but is not one of the deployed files`);
    }
  });
});
