import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';

const HERE = dirname(fileURLToPath(import.meta.url));
const readPolicy = () => readFileSync(join(HERE, 'privacy.html'), 'utf8');

describe('the website privacy policy', () => {
  it('is a dated, standalone document with accessible navigation', () => {
    const html = readPolicy();
    assert.match(html, /<html lang="en">/);
    assert.match(html, /<meta name="viewport" content="width=device-width, initial-scale=1">/);
    assert.match(html, /<title>Privacy policy \| UrDatabase<\/title>/);
    assert.equal((html.match(/<h1\b/g) || []).length, 1);
    assert.match(html, /<h1>Privacy policy<\/h1>/);
    assert.match(html, /<time datetime="\d{4}-\d{2}-\d{2}">[^<]+<\/time>/);
    assert.match(html, /class="skip" href="#main"/);
    assert.match(html, /<main id="main">/);
    assert.ok(html.trimEnd().endsWith('</html>'));
  });

  it('names the automatic third-party requests and links to their privacy information', () => {
    const html = readPolicy();
    for (const provider of ['Firebase Hosting', 'Google Fonts', 'GitHub']) {
      assert.ok(html.includes(provider), `missing disclosure for ${provider}`);
    }
    for (const url of [
      'https://firebase.google.com/support/privacy',
      'https://fonts.google.com/faq#privacy',
      'https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement',
    ]) {
      assert.ok(html.includes(`href="${url}"`), `missing privacy link for ${url}`);
    }
    assert.match(html, /IP address/i);
  });

  it('explains device detection and browser storage', () => {
    const text = readPolicy().replace(/\s+/g, ' ');
    for (const detail of [
      'user agent', 'touch points', 'processor architecture', 'WebGL',
      'localStorage', 'sessionStorage', 'IndexedDB',
    ]) {
      assert.ok(text.includes(detail), `missing disclosure for ${detail}`);
    }
    assert.match(text, /not sent to us or attached to the GitHub API request/);
    assert.match(text, /does not set cookies/);
  });

  it('provides a private contact and distinguishes the website from the app', () => {
    const html = readPolicy();
    assert.match(html, /href="mailto:larabailen\.lb@gmail\.com"/);
    assert.match(html, /public[^<]*GitHub issue/i);
    assert.match(html, /href="https:\/\/github\.com\/larabail\/UrDatabase#features"/);
    for (const id of ['scope', 'requests', 'device', 'cookies', 'contact', 'retention', 'rights', 'changes']) {
      assert.ok(html.includes(`id="${id}"`), `missing policy section ${id}`);
    }
  });

  it('can be read without scripts, forms or third-party embedded resources', () => {
    const html = readPolicy();
    assert.doesNotMatch(html, /<(?:script|iframe|img|object|embed|form)\b/i);
    assert.doesNotMatch(html, /<link\b[^>]*href="https?:/i);
    assert.doesNotMatch(html, /@import\b|url\(\s*['"]?https?:/i);
    assert.doesNotMatch(html, /\bon\w+\s*=/i);
  });

  it('links back to the downloads page and only uses existing local targets', () => {
    const html = readPolicy();
    assert.match(html, /<a\b[^>]*href="index\.html"[^>]*>Back to downloads<\/a>/);
    for (const [, href] of html.matchAll(/href="(?!https?:|data:|mailto:)([^"]+)"/g)) {
      const [file, fragment] = href.split('#');
      const target = file ? readFileSync(join(HERE, file), 'utf8') : html;
      if (fragment) assert.ok(target.includes(`id="${fragment}"`), `missing target ${href}`);
    }
  });
});
