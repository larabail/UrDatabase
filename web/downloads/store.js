export function storeUrl({ productId, live }) {
  if (typeof live !== 'boolean') throw new Error('Store listing live flag must be boolean.');
  if (!live && !productId) return null;
  if (typeof productId !== 'string' || !/^9[A-Z0-9]{11}$/.test(productId)) {
    throw new Error('Store product ID must be the 12-character ID from Partner Center.');
  }
  return live ? `https://apps.microsoft.com/detail/${productId}` : null;
}

export function applyStoreLinks(config, platform, find) {
  const url = storeUrl(config);
  if (!url) return;

  const store = find('store-win-x64');
  store.href = url;
  store.hidden = false;
  find('store-description').hidden = false;
  const zip = find('button-win-x64');
  if (zip.textContent !== 'No build yet') zip.textContent = 'Download unsigned ZIP';

  if (platform === 'win-x64') {
    const hero = find('hero-button');
    hero.href = url;
    hero.textContent = 'Get it from Microsoft Store';
    hero.hidden = false;
    hero.removeAttribute('aria-disabled');
    find('hero-meta').textContent = 'Store-managed updates. See the Store for the available version.';
  }
}
