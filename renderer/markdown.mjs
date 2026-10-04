import { unified } from 'unified';
import remarkParse from 'remark-parse';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math';
import remarkFrontmatter from 'remark-frontmatter';
import remarkBreaks from 'remark-breaks';
import remarkRehype from 'remark-rehype';
import rehypeRaw from 'rehype-raw';
import rehypeSanitize, { defaultSchema } from 'rehype-sanitize';
import rehypeStringify from 'rehype-stringify';
import { visit } from 'unist-util-visit';
import { toString } from 'mdast-util-to-string';
import GithubSlugger from 'github-slugger';
import { gemoji } from 'gemoji';
import hljs from 'highlight.js';
import katex from 'katex';
import { parseDocument } from 'yaml';

const emojis = new Map(gemoji.flatMap(({ names, emoji }) => names.map(name => [name, emoji])));
const el = (tagName, properties = {}, children = []) => ({ type: 'element', tagName, properties, children });
const txt = value => ({ type: 'text', value });
const htmlEscape = value => value.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const stringify = unified().use(rehypeStringify);
const rawParser = unified().use(rehypeRaw);
const schema = {
  ...defaultSchema,
  // There are no named global DOM lookups in the renderer. IDs are remapped below.
  clobberPrefix: '',
  tagNames: [...defaultSchema.tagNames, 'details', 'summary', 'kbd', 'samp', 'ruby', 'rt', 'rp', 'figure', 'figcaption', 'caption'],
  attributes: {
    ...defaultSchema.attributes,
    code: [['className', /^language-/, 'math-inline', 'math-display']],
    div: [['className', /^markdown-alert/, /^qiita-note/, 'code-frame', 'frontmatter']],
    figure: [['className', 'code-frame']],
    table: [...(defaultSchema.attributes.table || []), ['className', 'frontmatter']],
    span: [],
    pre: [],
    details: ['open'],
    img: [...(defaultSchema.attributes.img || []), 'width', 'height'],
  },
  protocols: { ...defaultSchema.protocols, src: ['http', 'https', 'data'] },
};

function parserFor(mode) {
  return unified().use(remarkParse).use(remarkGfm, { singleTilde: mode === 'github' })
    .use(remarkMath).use(remarkFrontmatter, ['yaml']);
}

/** Only recognize standalone Qiita fences outside code, HTML, math and other containers. */
function parseQiita(source, parser, diagnostics, depth = 0, inheritedDefinitions = '') {
  const fullSource = source + inheritedDefinitions;
  const original = parser.parse(fullSource);
  if (depth > 16) return original;
  const definitions = [];
  visit(original, node => {
    if (node.type === 'definition' || node.type === 'footnoteDefinition') definitions.push(node);
  });
  const referenceText = '\n\n' + definitions.map(node => fullSource.slice(node.position.start.offset, node.position.end.offset)).join('\n\n');
  const parseSegment = segment => parser.parse(segment + referenceText).children.filter(node => !['definition', 'footnoteDefinition'].includes(node.type));
  const protectedRanges = [];
  visit(original, node => {
    if (['code', 'html', 'math', 'yaml', 'list', 'blockquote'].includes(node.type) && node.position) {
      protectedRanges.push([node.position.start.offset, node.position.end.offset]);
      return 'skip';
    }
  });
  const protectedAt = offset => protectedRanges.some(([start, end]) => offset >= start && offset < end);
  const fences = [...source.matchAll(/^ {0,3}(:{3,})(?:note(?: +(info|warn|alert))?)?[ \t]*$/gm)]
    .filter(m => !protectedAt(m.index));
  const children = [];
  let cursor = 0;
  for (let i = 0; i < fences.length; i++) {
    const open = fences[i];
    if (!open[0].includes('note') || open.index < cursor) continue;
    let nesting = 1;
    let close;
    let j = i + 1;
    for (; j < fences.length; j++) {
      const candidate = fences[j];
      if (candidate[0].includes('note')) nesting++;
      else if (candidate[1].length >= open[1].length && --nesting === 0) { close = candidate; break; }
    }
    if (!close) {
      diagnostics.add('閉じる ::: がないQiitaの補足枠は、元の文字列として表示しました。');
      continue;
    }
    children.push(...parseSegment(source.slice(cursor, open.index)));
    const bodyStart = open.index + open[0].length + 1;
    children.push({ type: 'mdvNote', tone: open[2] || 'info', children: parseQiita(source.slice(bodyStart, close.index), parser, diagnostics, depth + 1, referenceText).children });
    cursor = close.index + close[0].length;
    i = j;
  }
  if (!children.length) return original;
  children.push(...parseSegment(source.slice(cursor)));
  // Reference links and footnotes have document-wide scope, including inside notes.
  children.push(...definitions);
  return { type: 'root', children };
}

function decorateMarkdown(tree, mode, diagnostics) {
  visit(tree, 'inlineMath', node => {
    if (node.value.startsWith('`') && node.value.endsWith('`')) {
      node.value = node.value.slice(1, -1);
      node.data.hChildren = [txt(node.value)];
    }
  });
  visit(tree, 'blockquote', node => {
    if (mode !== 'github') return;
    const first = node.children[0];
    if (first?.type !== 'paragraph' || first.children[0]?.type !== 'text') return;
    const match = first.children[0].value.match(/^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\](?:\n|$)/);
    if (!match) return;
    first.children[0].value = first.children[0].value.slice(match[0].length);
    if (!toString(first)) node.children.shift();
    const tone = match[1].toLowerCase();
    node.data = { hName: 'div', hProperties: { className: ['markdown-alert', `markdown-alert-${tone}`] } };
    node.children.unshift({ type: 'paragraph', children: [{ type: 'strong', children: [{ type: 'text', value: match[1][0] + match[1].slice(1).toLowerCase() }] }] });
  });
  visit(tree, 'code', node => {
    if (mode === 'qiita' && node.lang?.includes(':')) {
      const colon = node.lang.indexOf(':');
      node.filename = node.lang.slice(colon + 1) + (node.meta ? ` ${node.meta}` : '');
      node.lang = node.lang.slice(0, colon);
    }
    if (['plantuml', 'geojson', 'topojson', 'stl'].includes(node.lang?.toLowerCase())) {
      diagnostics.add(`${node.lang}: この形式の図は未対応のため、コードとして表示しています。`);
    }
  });
}

const handlers = {
  mdvNote(state, node) { return el('div', { className: ['qiita-note', `qiita-note-${node.tone}`] }, state.all(node)); },
  code(state, node) {
    const code = el('code', node.lang ? { className: [`language-${node.lang}`] } : {}, [txt(`${node.value}\n`)]);
    const pre = el('pre', {}, [code]);
    return node.filename ? el('figure', { className: ['code-frame'] }, [el('figcaption', {}, [txt(node.filename)]), pre]) : pre;
  },
  yaml(state, node) {
    try {
      const doc = parseDocument(node.value);
      if (doc.errors.length) throw doc.errors[0];
      const values = doc.toJS({ maxAliasCount: 20 });
      if (!values || typeof values !== 'object' || Array.isArray(values)) return el('pre', {}, [txt(node.value)]);
      const rows = Object.entries(values).map(([key, value]) => el('tr', {}, [
        el('th', {}, [txt(key)]), el('td', {}, [txt(typeof value === 'string' ? value : JSON.stringify(value))]),
      ]));
      return el('table', { className: ['frontmatter'] }, [el('tbody', {}, rows)]);
    } catch { return el('pre', {}, [txt(node.value)]); }
  },
};

function textOf(node) {
  return node.type === 'text' ? node.value : (node.children || []).map(textOf).join('');
}

function fragment(html) {
  return rawParser.runSync({ type: 'root', children: [{ type: 'raw', value: html }] }).children;
}

function mathMarkup(value, display, diagnostics) {
  try {
    return fragment(katex.renderToString(value, {
      displayMode: display, throwOnError: true, strict: 'ignore', trust: false,
      maxExpand: 1000, maxSize: 20, output: 'htmlAndMathml',
    }));
  } catch {
    diagnostics.add('解釈できない数式があります。該当箇所は元の数式を表示しています。');
    return [el('code', { className: ['math-error'], title: '数式を解釈できませんでした' }, [txt(value)])];
  }
}

function highlight(value, language) {
  if (!language || !hljs.getLanguage(language) || value.length > 100_000) return htmlEscape(value);
  try { return hljs.highlight(value, { language, ignoreIllegals: true }).value; }
  catch { return htmlEscape(value); }
}

function decorateHtml(tree, mode, diagnostics, outline) {
  const slugger = new GithubSlugger();
  const qiitaSlugs = new Map();
  // Use an app-private prefix for every user-authored ID. Fragment links are translated in browser.mjs.
  visit(tree, 'element', node => {
    if (node.properties.id) node.properties.id = `mdv-${node.properties.id}`;
    if (node.properties.ariaDescribedBy) node.properties.ariaDescribedBy = node.properties.ariaDescribedBy.map(id => `mdv-${id}`);
    if (node.properties.name) delete node.properties.name;
    if (node.tagName === 'input') { node.properties.disabled = true; node.properties.type = 'checkbox'; }
    if (/^h[1-6]$/.test(node.tagName) && !node.properties.className?.includes('sr-only')) {
      const title = textOf(node);
      let slug;
      if (mode === 'qiita') {
        const base = title.toLowerCase().replace(/[^\p{L}\p{M}\p{N}\p{Pc}\- ]/gu, '').replace(/ /g, '-');
        const count = qiitaSlugs.get(base) || 0;
        qiitaSlugs.set(base, count + 1);
        slug = base + (count ? `-${count}` : '');
      } else slug = slugger.slug(title);
      node.properties.id = `mdv-heading-${slug}`;
      outline.push({ id: node.properties.id, anchor: slug, text: title, level: Number(node.tagName[1]) });
    }
  });
  // Transform text without modifying code, links, math, or HTML attributes.
  function inline(node, blocked = false) {
    if (!node.children) return;
    blocked ||= ['code', 'pre', 'a', 'math'].includes(node.tagName);
    if (blocked) return;
    const next = [];
    for (const child of node.children) {
      if (child.type !== 'text') { inline(child); next.push(child); continue; }
      const rx = /:([\w+-]+):|(^|[\s(])@([a-z\d](?:[a-z\d_-]{0,38}))(?![\w/-])/gi;
      let cursor = 0;
      for (const match of child.value.matchAll(rx)) {
        const emoji = match[1] ? emojis.get(match[1]) : null;
        if (match[1] && !emoji) continue;
        next.push(txt(child.value.slice(cursor, match.index)));
        if (emoji) next.push(txt(emoji));
        else next.push(txt(match[2]), el('a', { href: `https://${mode === 'qiita' ? 'qiita.com' : 'github.com'}/${match[3]}` }, [txt(`@${match[3]}`)]));
        cursor = match.index + match[0].length;
      }
      next.push(txt(child.value.slice(cursor)));
    }
    node.children = next;
  }
  inline(tree);
  visit(tree, 'element', (node, index, parent) => {
    if (node.tagName !== 'code') return;
    const classes = node.properties.className || [];
    const language = classes.find(c => c.startsWith('language-'))?.slice(9).toLowerCase();
    const value = textOf(node).replace(/\n$/, '');
    if (language === 'math' || classes.includes('math-inline') || classes.includes('math-display')) {
      const display = parent?.tagName === 'pre' || classes.includes('math-display');
      const output = mathMarkup(value, display, diagnostics);
      node.tagName = 'span';
      node.properties = { className: [display ? 'math-block' : 'math-inline'] };
      node.children = output;
      if (parent?.tagName === 'pre') { parent.tagName = 'div'; parent.properties = { className: ['math-container'] }; }
      return 'skip';
    }
    if (language === 'mermaid' && parent?.tagName === 'pre') {
      parent.properties.className = ['mermaid-source'];
      return 'skip';
    }
    if (parent?.tagName === 'pre') {
      let output;
      if (mode === 'qiita' && language?.startsWith('diff_')) {
        const underlying = language.slice(5);
        output = value.split('\n').map(line => {
          const kind = line.startsWith('+') ? 'diff-add' : line.startsWith('-') ? 'diff-remove' : 'diff-context';
          return `<span class="diff-line ${kind}">${htmlEscape(line[0] || '')}${highlight(line.slice(1), underlying)}</span>`;
        }).join('\n');
      } else output = highlight(value, language);
      node.children = fragment(output + '\n');
      node.properties.className = [...classes, 'hljs'];
      return 'skip';
    }
    if (mode === 'qiita' && /^(#[0-9a-f]{3,8}|(?:rgb|hsl)a?\([\d.,%\s+-]+\))$/i.test(value)) {
      node.children.push(el('span', { className: ['color-swatch'], style: `background-color:${value}`, ariaHidden: 'true' }));
    }
  });
  if (mode === 'qiita') {
    visit(tree, 'element', node => {
      if (node.tagName !== 'p' || node.children.length !== 1) return;
      const a = node.children[0];
      if (a.tagName === 'a' && /^https?:\/\//i.test(a.properties.href) && textOf(a) === a.properties.href) {
        node.properties.className = ['link-card'];
        a.children = [el('span', { className: ['link-card-host'] }, [txt(new URL(a.properties.href).hostname)]), el('span', {}, [txt(a.properties.href)])];
      }
    });
  }
}

export function renderMarkdown(source, { mode = 'github' } = {}) {
  if (!['github', 'qiita'].includes(mode)) throw new Error('Unknown Markdown mode');
  if (typeof source !== 'string' || source.length > 4 * 1024 * 1024) throw new Error('文書は4 Mi文字以下にしてください。');
  source = source.replace(/^\uFEFF/, '').replace(/\r\n?/g, '\n');
  const diagnostics = new Set();
  const outline = [];
  const parser = parserFor(mode);
  const mdast = mode === 'qiita' ? parseQiita(source, parser, diagnostics) : parser.parse(source);
  decorateMarkdown(mdast, mode, diagnostics);
  const converter = unified();
  if (mode === 'qiita') converter.use(remarkBreaks);
  converter.use(remarkRehype, {
    allowDangerousHtml: true, handlers, footnoteLabel: '脚注', footnoteBackLabel: '本文に戻る',
  }).use(rehypeRaw).use(rehypeSanitize, schema);
  const tree = converter.runSync(mdast);
  decorateHtml(tree, mode, diagnostics, outline);
  return { html: stringify.stringify(tree), outline, diagnostics: [...diagnostics] };
}
