import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';

async function render(page, markdown, mode = 'github', other = {}) {
  await page.goto('/');
  await page.waitForFunction(() => window.mdv);
  return page.evaluate(options => window.mdv.render(options), { markdown, mode, requestId: 1, ...other });
}

test('both modes display the sample, math, diagrams, notes and filenames offline', async ({ page }) => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('https://document.mdv.invalid/**', route => route.fulfill({ path: 'samples/images/viewer.svg', contentType: 'image/svg+xml' }));
  const source = await readFile('samples/welcome.md', 'utf8');
  const github = await render(page, source);
  expect(github.diagnostics).toEqual([]);
  await expect(page.locator('.markdown-alert-note')).toBeVisible();
  await expect(page.locator('.katex')).toHaveCount(2);
  await expect(page.locator('.diagram svg')).toHaveCount(1);
  await expect(page.locator('.diagram svg text').first()).toBeVisible();
  await expect(page.locator('img')).toHaveJSProperty('naturalWidth', 880);
  const qiita = await page.evaluate(markdown => window.mdv.render({ markdown, mode: 'qiita', theme: 'dark' }), source);
  expect(qiita.diagnostics).toEqual([]);
  await expect(page.locator('.qiita-note')).toHaveCount(2);
  await expect(page.locator('figcaption').first()).toHaveText('hello.js');
  await expect(page.locator('.diagram svg')).toHaveCount(1);
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  expect(errors).toEqual([]);
  await page.screenshot({ path: 'artifacts/preview-qiita.png', fullPage: true });
});

test('hostile HTML does not execute, request images by default, or access host objects', async ({ page }) => {
  const requests = [];
  page.on('request', request => { if (!request.url().startsWith('http://127.0.0.1:4179')) requests.push(request.url()); });
  await render(page, '<script>window.pwned=true</script>\n\n<img src="https://evil.test/tracker" onerror="window.pwned=true">\n\n<iframe src="https://evil.test"></iframe>\n\n[x](javascript:alert(1))');
  await page.waitForTimeout(100);
  expect(await page.evaluate(() => window.pwned)).toBeUndefined();
  expect(requests).toEqual([]);
  await expect(page.locator('iframe, #document script')).toHaveCount(0);
});

test('Japanese headings, explicit HTML anchors and footnote return links work', async ({ page }) => {
  await render(page, '# 日本語\n\n[go](#日本語) and text[^one]\n\n<a id="custom"></a>\n\n[^one]: A note');
  expect(await page.evaluate(() => window.mdv.scrollToAnchor('日本語'))).toBe(true);
  expect(await page.evaluate(() => window.mdv.scrollToAnchor('custom'))).toBe(true);
  expect(await page.evaluate(() => window.mdv.scrollToAnchor('user-content-fn-one'))).toBe(true);
  expect(await page.evaluate(() => window.mdv.scrollToAnchor('user-content-fnref-one'))).toBe(true);
});

test('search finds code and text, cycles results, and clears when rerendering', async ({ page }) => {
  await render(page, '# Needle\n\nneedle\n\n```text\nneedle\n```');
  expect(await page.evaluate(() => window.mdv.find('needle'))).toMatchObject({ total: 3, index: 1 });
  expect(await page.evaluate(() => window.mdv.find('needle', 1))).toMatchObject({ index: 2 });
  expect(await page.evaluate(() => window.mdv.find('needle', -1))).toMatchObject({ index: 1 });
  expect(await page.evaluate(() => window.mdv.find(''))).toMatchObject({ total: 0 });
});

test('invalid Mermaid falls back to its source with diagnostics', async ({ page }) => {
  const result = await render(page, '```mermaid\nnot-a-valid-diagram !@!@\n```');
  expect(result.diagnostics).toHaveLength(1);
  await expect(page.locator('pre.diagram-error')).toBeVisible();
});
test('search preserves Unicode offsets and treats punctuation as literal text', async ({ page }) => {
  await render(page, 'İİ needle (x+y) 日本語');
  expect(await page.evaluate(() => window.mdv.find('needle'))).toMatchObject({ total: 1 });
  expect(await page.evaluate(() => [...CSS.highlights.get('mdv-current')][0].toString())).toBe('needle');
  expect(await page.evaluate(() => window.mdv.find('(x+y)'))).toMatchObject({ total: 1 });
  expect(await page.evaluate(() => window.mdv.find('日本語'))).toMatchObject({ total: 1 });
});

test('relative images are mapped to the host, remote images require opt-in', async ({ page }) => {
  await page.route('https://document.mdv.invalid/**', route => route.fulfill({ path: 'samples/images/viewer.svg', contentType: 'image/svg+xml' }));
  await page.route('https://example.com/**', route => route.fulfill({ path: 'samples/images/viewer.svg', contentType: 'image/svg+xml' }));
  await render(page, '![local](images/viewer.svg)\n\n![remote](https://example.com/picture.png)');
  await expect(page.locator('img').first()).toHaveAttribute('src', 'https://document.mdv.invalid/image/images%2Fviewer.svg');
  await expect(page.locator('img').nth(1)).not.toHaveAttribute('src');
  await page.evaluate(() => window.mdv.render({ markdown: '![remote](https://example.com/picture.png)', mode: 'github', allowRemoteImages: true }));
  await expect(page.locator('img')).toHaveAttribute('src', 'https://example.com/picture.png');
});

const longDocument = '# Start\n\n' + 'Opening paragraph.\n\n'.repeat(35) + '\n## 日本語の見出し\n\n' + 'Reading paragraph.\n\n'.repeat(65);

test('reading position survives navigation, diagram rendering and content inserted above its heading', async ({ page }) => {
  const source = '```mermaid\nflowchart LR\n A --> B\n```\n\n' + longDocument;
  await render(page, source);
  const saved = await page.evaluate(() => {
    window.mdv.scrollToAnchor('日本語の見出し'); window.scrollBy(0, 180);
    return window.mdv.captureView();
  });
  expect(saved.y).toBeGreaterThan(500);
  await page.evaluate(() => window.mdv.render({ markdown: '# Other', resetScroll: true }));
  await page.evaluate(({ source, saved }) => window.mdv.render({ markdown: source, resetScroll: true, view: saved }), { source, saved });
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBeCloseTo(saved.y, 0);
  await page.evaluate(({ source, saved }) => window.mdv.render({ markdown: 'New material.\n\n'.repeat(10) + source, view: saved }), { source, saved });
  await expect.poll(() => page.evaluate(() => window.mdv.captureView().offset)).toBeCloseTo(saved.offset, 0);
  expect(await page.evaluate(() => window.scrollY)).toBeGreaterThan(saved.y + 100);
});

test('restoration preserves open details and does not undo a reader scroll or a search', async ({ page }) => {
  const source = '# Top\n\n<details><summary>Expand</summary>\n\n' + 'Inside details.\n\n'.repeat(60) + '\n</details>\n\n' + longDocument;
  await render(page, source);
  await page.locator('summary').click();
  const saved = await page.evaluate(() => { window.scrollTo(0, 900); return window.mdv.captureView(); });
  expect(saved.openDetails).toEqual([0]);
  await page.evaluate(({ source, saved }) => window.mdv.render({ markdown: source, view: saved }), { source, saved });
  await expect(page.locator('details')).toHaveAttribute('open', '');
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBeCloseTo(saved.y, 0);
  await page.mouse.move(700, 400); await page.mouse.wheel(0, 350);
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBeGreaterThan(saved.y + 200);
  const moved = await page.evaluate(() => window.scrollY);
  await page.evaluate(() => document.querySelector('#document').style.paddingBottom = '500px');
  await page.waitForTimeout(100);
  expect(await page.evaluate(() => window.scrollY)).toBeCloseTo(moved, 0);
  await page.evaluate(() => window.mdv.find('Opening'));
  expect(await page.evaluate(() => window.scrollY)).toBeGreaterThan(moved);
});

test('new documents preserve default-open details and history preserves a reader collapse', async ({ page }) => {
  const source = '# Top\n\n<details open><summary>Expanded by author</summary>\n\nContent.\n\n</details>';
  await render(page, source, 'github', { resetScroll: true });
  await expect(page.locator('details')).toHaveAttribute('open', '');
  expect(await page.evaluate(() => window.mdv.captureView().openDetails)).toEqual([0]);
  await page.locator('summary').click();
  const saved = await page.evaluate(() => window.mdv.captureView());
  await page.evaluate(() => window.mdv.render({ markdown: '# Other', resetScroll: true }));
  await page.evaluate(({ source, saved }) => window.mdv.render({ markdown: source, view: saved }), { source, saved });
  await expect(page.locator('details')).not.toHaveAttribute('open', '');
});

test('late image layout keeps the saved heading at the same viewport position', async ({ page }) => {
  await render(page, longDocument);
  const saved = await page.evaluate(() => { window.mdv.scrollToAnchor('日本語の見出し'); window.scrollBy(0, 130); return window.mdv.captureView(); });
  await page.evaluate(({ source, saved }) => window.mdv.render({ markdown: source, view: saved }), { source: longDocument, saved });
  await page.evaluate(() => {
    const image = document.createElement('img'); image.alt = 'Late image';
    image.style.cssText = 'display:block; width:200px; height:400px';
    document.querySelector('#document').prepend(image);
  });
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBeGreaterThan(saved.y + 390);
  await expect.poll(() => page.evaluate(() => -document.getElementById(window.mdv.captureView().anchor).getBoundingClientRect().top)).toBeCloseTo(saved.offset, 0);
});

test('Alt arrows are forwarded to native navigation and a restored search does not move the viewport', async ({ page }) => {
  await page.addInitScript(() => { window.messages = []; window.chrome = { webview: { postMessage: message => window.messages.push(message), addEventListener() {} } }; });
  await render(page, longDocument);
  await page.keyboard.press('Alt+ArrowLeft'); await page.keyboard.press('Alt+ArrowRight');
  expect(await page.evaluate(() => window.messages.filter(message => message.type === 'shortcut').map(message => message.key))).toEqual(['back', 'forward']);
  const y = await page.evaluate(() => { window.scrollTo(0, 1000); window.mdv.find('Reading', 0, false); return window.scrollY; });
  expect(y).toBeCloseTo(1000, 0);
});

test('ordinary links navigate in place; Ctrl and middle clicks request another tab', async ({ page }) => {
  await page.addInitScript(() => { window.messages = []; window.chrome = { webview: { postMessage: message => window.messages.push(message), addEventListener() {} } }; });
  await render(page, '# Top\n\n[Other](next.md#section)\n\n[Here](#top)');
  await page.getByRole('link', { name: 'Other' }).click();
  await page.getByRole('link', { name: 'Other' }).click({ modifiers: ['Control'] });
  await page.getByRole('link', { name: 'Other' }).click({ button: 'middle' });
  await page.getByRole('link', { name: 'Here' }).click();
  expect(await page.evaluate(() => window.messages.filter(message => message.type === 'openLink'))).toEqual([
    { type: 'openLink', href: 'next.md#section', newTab: false, requestId: 1 },
    { type: 'openLink', href: 'next.md#section', newTab: true, requestId: 1 },
    { type: 'openLink', href: 'next.md#section', newTab: true, requestId: 1 },
  ]);
});

test('tab shortcuts reach the native host while the preview has focus', async ({ page }) => {
  await page.addInitScript(() => { window.messages = []; window.chrome = { webview: { postMessage: message => window.messages.push(message), addEventListener() {} } }; });
  await render(page, '# Keyboard');
  for (const key of ['Control+t', 'Control+Tab', 'Control+Shift+Tab', 'Control+w']) await page.keyboard.press(key);
  expect(await page.evaluate(() => window.messages.filter(message => message.type === 'shortcut').map(message => message.key)))
    .toEqual(['newTab', 'nextTab', 'previousTab', 'closeTab']);
});
