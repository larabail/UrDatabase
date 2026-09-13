import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { applyStoreLinks, storeUrl } from './store.js';
import { configureStoreSite } from '../../tool/configure_store_site.mjs';
import { STORE_CONFIG } from './store-config.js';

const productId = '9TESTAPP0001';

test('the checked-in site configuration uses the canonical public Store product ID', () => {
  const product = JSON.parse(readFileSync(new URL('../../packaging/windows/store-product.json', import.meta.url)));
  assert.equal(STORE_CONFIG.productId, product.productId);
});

test('Store links require both a valid product ID and explicit live-listing confirmation', () => {
  assert.equal(storeUrl({ productId: '', live: false }), null);
  assert.equal(storeUrl({ productId, live: false }), null);
  assert.equal(storeUrl({ productId, live: true }), `https://apps.microsoft.com/detail/${productId}`);
  for (const value of ['', 'UrActor.UrDatabase', '../bad', 'https://example.test', '9BAD<script>']) {
    assert.throws(() => storeUrl({ productId: value, live: true }));
  }
});

function elements() {
  return new Map(['store-win-x64', 'store-description', 'hero-button', 'hero-meta',
    'button-win-x64'].map((id) => [id, {
    hidden: true,
    textContent: id === 'hero-meta' ? 'Version 99.0.0 from GitHub' : '',
    href: 'https://github.com/larabail/UrDatabase/releases',
    removeAttribute(name) { delete this[name]; },
  }]));
}

test('Windows Store CTA works with no GitHub release and never borrows its version', () => {
  const nodes = elements();
  applyStoreLinks({ productId, live: true }, 'win-x64', (id) => nodes.get(id));
  assert.equal(nodes.get('hero-button').href, `https://apps.microsoft.com/detail/${productId}`);
  assert.equal(nodes.get('hero-button').hidden, false);
  assert.equal(nodes.get('store-win-x64').hidden, false);
  assert.match(nodes.get('hero-meta').textContent, /Store-managed updates/);
  assert.doesNotMatch(nodes.get('hero-meta').textContent, /99\.0\.0/);
  assert.match(nodes.get('button-win-x64').textContent, /ZIP/);
  assert.match(nodes.get('button-win-x64').href, /github\.com/);
});

test('Store link does not replace the Mac hero or an unconfigured Windows download', () => {
  for (const [config, platform] of [
    [{ productId, live: true }, 'osx-arm64'],
    [{ productId, live: true }, 'osx-x64'],
    [{ productId: '', live: false }, 'win-x64'],
  ]) {
    const nodes = elements();
    applyStoreLinks(config, platform, (id) => nodes.get(id));
    assert.match(nodes.get('hero-button').href, /github\.com/);
  }
});

test('deployment injects only the public Store ID and also enables a no-JavaScript link', () => {
  const root = mkdtempSync(join(tmpdir(), 'urdb-store-site-'));
  try {
    writeFileSync(join(root, 'index.html'), readFileSync(new URL('./index.html', import.meta.url)));
    configureStoreSite(root, productId, 'true');
    const html = readFileSync(join(root, 'index.html'), 'utf8');
    assert.match(html, new RegExp(`id="store-win-x64" href="https://apps.microsoft.com/detail/${productId}"`));
    assert.doesNotMatch(html, /id="store-win-x64"[^>]*hidden/);
    assert.match(readFileSync(join(root, 'store-config.js'), 'utf8'), /"live":true/);
    configureStoreSite(root, '', 'false');
    assert.match(readFileSync(join(root, 'index.html'), 'utf8'), /id="store-win-x64" hidden/);
    assert.throws(() => configureStoreSite(root, productId, 'yes'));
    assert.throws(() => configureStoreSite(root, '', 'true'));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('the actual page keeps the Store CTA usable when GitHub is empty, failing or never answers', () => {
  const root = mkdtempSync(join(tmpdir(), 'urdb-store-page-'));
  try {
    for (const file of ['index.html', 'page.js', 'releases.js', 'store.js', 'package.json']) {
      writeFileSync(join(root, file), readFileSync(new URL(file, import.meta.url)));
    }
    configureStoreSite(root, productId, 'true');
    const ids = [...readFileSync(join(root, 'index.html'), 'utf8').matchAll(/id="([^"]+)"/g)]
      .map((match) => match[1]);
    for (const mode of ['empty', 'failed', 'pending']) {
      const result = spawnSync(process.execPath, ['--input-type=module', '-e', `
        import assert from 'node:assert/strict';
        const nodes = new Map(${JSON.stringify(ids)}.map(id => [id, {
          hidden: true, href: '', textContent: '', classList: { add() {}, toggle() {} },
          setAttribute(key, value) { this[key] = value; },
          removeAttribute(key) { delete this[key]; }
        }]));
        globalThis.document = {
          getElementById: id => nodes.get(id),
          createElement: () => ({ getContext: () => null })
        };
        Object.defineProperty(globalThis, 'navigator', { value: { userAgent: 'Windows NT 10.0' } });
        globalThis.fetch = async () => {
          if (${JSON.stringify(mode)} === 'failed') throw new Error('offline fixture');
          if (${JSON.stringify(mode)} === 'pending') return new Promise(() => {});
          return { ok: true, json: async () => [] };
        };
        await import(${JSON.stringify(pathToFileURL(join(root, 'page.js')).href)});
        await new Promise(resolve => setTimeout(resolve, 20));
        assert.equal(nodes.get('hero-button').href, 'https://apps.microsoft.com/detail/${productId}');
        assert.equal(nodes.get('hero-button').hidden, false);
        assert.equal(nodes.get('store-win-x64').hidden, false);
        assert.match(nodes.get('hero-meta').textContent, /Store-managed updates/);
      `], { encoding: 'utf8', timeout: 5000 });
      assert.equal(result.status, 0, result.stderr || result.error?.message);
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
