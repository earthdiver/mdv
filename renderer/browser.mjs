import { renderMarkdown } from './markdown.mjs';
import DOMPurify from 'dompurify';

const article = document.getElementById('document');
let generation = 0;
let currentOutline = [];
let searchRanges = [];
let searchIndex = -1;
let mermaidModule;
let currentRequestId = 0;
let restoration;
const post = message => window.chrome?.webview?.postMessage(message);

function captureView() {
  if (restoration && (Math.abs(window.scrollY - restoration.lastY) > 1 || Math.abs(window.scrollX - restoration.lastX) > 1))
    cancelRestoration();
  if (restoration) return restoration.view;
  const y = window.scrollY;
  let anchor = null;
  let offset = 0;
  for (const heading of article.querySelectorAll('h1[id], h2[id], h3[id], h4[id], h5[id], h6[id]')) {
    if (!heading.getClientRects().length) continue;
    const top = heading.getBoundingClientRect().top;
    if (top > 1) break;
    anchor = heading.id;
    offset = -top;
  }
  return { x: window.scrollX, y, anchor, offset,
    ratio: y / Math.max(1, document.documentElement.scrollHeight - innerHeight),
    openDetails: [...article.querySelectorAll('details')].flatMap((item, index) => item.open ? [index] : []) };
}

function cancelRestoration() { restoration = null; }

function applyView(view) {
  const heading = view.anchor && document.getElementById(view.anchor);
  let y = view.y || 0;
  if (heading && article.contains(heading) && heading.getClientRects().length)
    y = heading.getBoundingClientRect().top + window.scrollY + (view.offset || 0);
  else if (view.anchor)
    y = (view.ratio || 0) * Math.max(0, document.documentElement.scrollHeight - innerHeight);
  window.scrollTo({ left: view.x || 0, top: y, behavior: 'instant' });
  if (restoration) { restoration.lastX = window.scrollX; restoration.lastY = window.scrollY; }
}

function restoreView(view) {
  const details = [...article.querySelectorAll('details')];
  if (Array.isArray(view.openDetails)) {
    const open = new Set(view.openDetails);
    details.forEach((item, index) => { item.open = open.has(index); });
  }
  restoration = { view: { ...view, openDetails: details.flatMap((item, index) => item.open ? [index] : []) }, generation };
  applyView(view);
}

// Images and fonts can change layout after the first render. Keep the saved reading
// position until the reader starts interacting; do not fight their scrolling/search.
new ResizeObserver(() => {
  if (restoration?.generation === generation) applyView(restoration.view);
}).observe(article);
for (const type of ['wheel', 'touchstart', 'pointerdown', 'keydown'])
  document.addEventListener(type, cancelRestoration, { capture: true, passive: true });

function scrollToAnchor(anchor) {
  cancelRestoration();
  let id;
  try { id = decodeURIComponent(anchor.replace(/^#/, '')); } catch { return false; }
  const candidates = [id, `mdv-heading-${id}`, `mdv-${id}`, `mdv-user-content-${id}`];
  for (const candidate of candidates) {
    const target = document.getElementById(candidate);
    if (!target || !article.contains(target)) continue;
    let parent = target.parentElement;
    while (parent) { if (parent.tagName === 'DETAILS') parent.open = true; parent = parent.parentElement; }
    target.scrollIntoView({ block: 'start' });
    return true;
  }
  return false;
}

function prepareImages(root, allowRemoteImages) {
  for (const img of root.querySelectorAll('img')) {
    const src = img.getAttribute('src') || '';
    img.removeAttribute('src');
    img.referrerPolicy = 'no-referrer';
    img.loading = 'lazy';
    let resolved = '';
    if (/^https?:\/\//i.test(src) && allowRemoteImages) resolved = src;
    else if (/^data:image\/(?:png|jpeg|gif|webp|avif|bmp);base64,[a-z\d+/=\s]+$/i.test(src)) resolved = src;
    else if (src && !/^(?:[a-z][\w+.-]*:|[\\/])/i.test(src) && !src.includes('\\')) {
      resolved = `https://document.mdv.invalid/image/${encodeURIComponent(src)}`;
    }
    if (resolved) img.src = resolved;
    else {
      img.classList.add('blocked-image');
      img.alt = `[画像] ${img.alt || src}${/^https?:/i.test(src) ? '（外部画像を有効にすると表示）' : ''}`;
    }
  }
}

async function diagrams(localGeneration, theme, diagnostics) {
  const sources = [...article.querySelectorAll('pre.mermaid-source')];
  if (!sources.length) return;
  mermaidModule ||= import('mermaid');
  const { default: mermaid } = await mermaidModule;
  // Markdown cannot override these options using frontmatter or init directives.
  mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', htmlLabels: false, theme: theme === 'dark' ? 'dark' : 'default',
    maxTextSize: 50_000, maxEdges: 500, suppressErrorRendering: true,
    secure: ['secure', 'securityLevel', 'startOnLoad', 'maxTextSize', 'maxEdges', 'suppressErrorRendering', 'htmlLabels'],
    flowchart: { htmlLabels: false }, });
  for (let i = 0; i < sources.length; i++) {
    if (localGeneration !== generation) return;
    const source = sources[i];
    const code = source.textContent;
    try {
      if (code.length > 50_000 || i >= 40) throw new Error('Diagram limit');
      const { svg } = await mermaid.render(`mdv-diagram-${localGeneration}-${i}`, code);
      if (localGeneration !== generation) return;
      const container = document.createElement('div');
      container.className = 'diagram';
      // Diagram callbacks and foreign HTML are deliberately excluded.
      container.innerHTML = DOMPurify.sanitize(svg, { USE_PROFILES: { svg: true, svgFilters: true },
        FORBID_TAGS: ['foreignObject', 'script', 'a', 'image'], FORBID_ATTR: ['href', 'xlink:href'] });
      source.replaceWith(container);
    } catch {
      source.classList.add('diagram-error');
      source.title = 'Mermaidの描画に失敗しました。記法と図の大きさを確認してください。';
      diagnostics.push('描画できないMermaid図があります。該当箇所はコードとして表示しています。');
    }
  }
}

function copyButtons() {
  for (const pre of article.querySelectorAll('pre')) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'copy-code';
    button.textContent = 'コピー';
    button.addEventListener('click', async () => {
      const text = pre.querySelector('code')?.textContent ?? pre.textContent;
      if (window.chrome?.webview) post({ type: 'copy', text });
      else await navigator.clipboard.writeText(text).catch(() => {});
      button.textContent = 'コピーしました';
      setTimeout(() => { button.textContent = 'コピー'; }, 1500);
    });
    pre.append(button);
  }
}

async function render(options) {
  const view = options.view || (options.resetScroll ? { x: 0, y: 0 } : captureView());
  cancelRestoration();
  const localGeneration = ++generation;
  const theme = options.theme === 'dark' ? 'dark' : 'light';
  const started = performance.now();
  document.documentElement.dataset.theme = theme;
  article.className = `markdown-body ${options.mode === 'qiita' ? 'qiita' : 'github'}`;
  const result = renderMarkdown(options.markdown ?? '', { mode: options.mode });
  currentOutline = result.outline;
  // Set sources on a detached template so blocked images cannot start a network request.
  const template = document.createElement('template');
  template.innerHTML = result.html;
  prepareImages(template.content, options.allowRemoteImages);
  article.replaceChildren(template.content);
  await diagrams(localGeneration, theme, result.diagnostics);
  if (localGeneration !== generation) return;
  copyButtons();
  find('');
  restoreView(view);
  if (options.anchor != null) {
    if (scrollToAnchor(options.anchor)) restoreView(captureView());
  }
  currentRequestId = options.requestId ?? 0;
  const message = { type: 'rendered', requestId: options.requestId, outline: result.outline,
    diagnostics: [...new Set(result.diagnostics)], duration: Math.round(performance.now() - started) };
  post(message);
  return message;
}

function find(query, direction = 0, scroll = true) {
  if (direction === 0) {
    searchRanges = [];
    searchIndex = -1;
    if (query) {
      const expression = new RegExp(query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'giu');
      const walker = document.createTreeWalker(article, NodeFilter.SHOW_TEXT, {
        acceptNode(node) { return node.parentElement.closest('button, svg, math, .katex-mathml') ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT; },
      });
      let node;
      while ((node = walker.nextNode()) && searchRanges.length < 10_000) {
        for (const match of node.textContent.matchAll(expression)) {
          if (searchRanges.length >= 10_000) break;
          const range = new Range();
          range.setStart(node, match.index); range.setEnd(node, match.index + match[0].length);
          searchRanges.push(range);
        }
      }
    }
  }
  if (searchRanges.length) searchIndex = (searchIndex + (direction || 1) + searchRanges.length) % searchRanges.length;
  CSS.highlights?.set('mdv-search', new Highlight(...searchRanges));
  CSS.highlights?.set('mdv-current', new Highlight(...(searchRanges.length ? [searchRanges[searchIndex]] : [])));
  if (searchRanges.length && scroll) {
    cancelRestoration();
    const parent = searchRanges[searchIndex].startContainer.parentElement;
    const details = parent.closest('details');
    if (details) details.open = true;
    parent.scrollIntoView({ block: 'center' });
  }
  const result = { type: 'findResult', total: searchRanges.length, index: searchIndex + 1 };
  post(result);
  return result;
}

function openLink(event, newTab) {
  const anchor = event.target.closest('a[href]');
  if (!anchor) return;
  event.preventDefault();
  const href = anchor.getAttribute('href');
  if (href.startsWith('#') && !newTab) scrollToAnchor(href);
  else post({ type: 'openLink', href, newTab, requestId: currentRequestId });
}
article.addEventListener('click', event => openLink(event, event.ctrlKey || event.metaKey));
article.addEventListener('auxclick', event => { if (event.button === 1) openLink(event, true); });
article.addEventListener('mousedown', event => {
  if (event.button === 1 && event.target.closest('a[href]')) event.preventDefault();
});

async function receive(message) {
  try {
    if (message.type === 'render') await render(message);
    if (message.type === 'scroll') scrollToAnchor(message.anchor);
    if (message.type === 'find') find(message.query, message.direction, message.scroll !== false);
  } catch (error) {
    article.textContent = `表示できませんでした: ${error.message}`;
    post({ type: 'error', requestId: message.requestId, message: error.message });
  }
}
// Serialize renders: Mermaid maintains shared state. Keep only the newest pending render.
let running = false;
let pending;
async function enqueue(message) {
  if (message.type !== 'render') return receive(message);
  pending = message;
  if (running) return;
  running = true;
  try { while (pending) { const next = pending; pending = null; await receive(next); } }
  finally { running = false; }
}
window.mdv = { render, find, scrollToAnchor, captureView,
  get requestId() { return currentRequestId; }, get outline() { return currentOutline; } };
document.addEventListener('keydown', event => {
  let key;
  if (event.altKey && !event.ctrlKey && !event.shiftKey && !event.metaKey)
    key = ({ ArrowLeft: 'back', ArrowRight: 'forward' })[event.key];
  if (event.ctrlKey && !event.altKey) key = ({ o: 'open', t: 'newTab', w: 'closeTab', tab: event.shiftKey ? 'previousTab' : 'nextTab',
    f: 'find', p: 'print', u: 'source', '1': 'github', '2': 'qiita', '0': 'zoomReset', '+': 'zoomIn', '=': 'zoomIn', '-': 'zoomOut' })[event.key.toLowerCase()];
  if (event.key === 'F5') key = 'reload';
  if (event.key === 'Escape') key = 'escape';
  if (event.key === 'F3') key = event.shiftKey ? 'findPrevious' : 'findNext';
  if (key) { event.preventDefault(); post({ type: 'shortcut', key }); }
});
document.addEventListener('dragover', event => { event.preventDefault(); });
document.addEventListener('drop', event => {
  event.preventDefault();
  if (event.dataTransfer.files.length) window.chrome?.webview?.postMessageWithAdditionalObjects({ type: 'drop' }, [...event.dataTransfer.files]);
});
window.chrome?.webview?.addEventListener('message', event => enqueue(event.data));
post({ type: 'ready' });
