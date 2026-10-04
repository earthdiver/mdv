import { test } from 'node:test';
import assert from 'node:assert/strict';
import { renderMarkdown } from '../markdown.mjs';

const render = (source, mode = 'github') => renderMarkdown(source, { mode });

for (const mode of ['github', 'qiita']) {
  test(`${mode}: CommonMark blocks, nested lists, literal code and escaped markers`, () => {
    const { html } = render('# H\n\n    <literal>\n\n1. one\n   - two\n\n\\*escaped\\*\n\n---', mode);
    for (const value of ['<h1', '<ol>', '<ul>', '*escaped*', '<hr>']) assert.ok(html.includes(value), value);
    assert.match(html, /&(?:lt;|#x3C;)literal>/);
  });
  test(`${mode}: GFM tables, tasks, autolinks and strikethrough`, () => {
    const { html } = render('| L | R |\n| :-- | --: |\n| a | b |\n\n- [x] yes\n- [ ] no\n\n~~deleted~~ https://example.com user@example.com', mode);
    for (const value of ['<table>', 'align="right"', 'type="checkbox"', 'checked', 'disabled', '<del>deleted</del>', 'href="https://example.com"', 'href="mailto:user@example.com"']) assert.ok(html.includes(value), value);
  });
  test(`${mode}: Japanese headings, duplicate slugs and footnotes`, () => {
    const result = render('# 日本語の見出し\n\n## 日本語の見出し\n\ntext[^x]\n\n[^x]: footnote', mode);
    assert.deepEqual(result.outline.map(h => h.anchor), ['日本語の見出し', '日本語の見出し-1']);
    assert.match(result.html, /id="mdv-user-content-fn-x"/);
    assert.match(result.html, /href="#user-content-fn-x"/);
    assert.match(result.html, /id="mdv-footnote-label"/);
  });
  test(`${mode}: safe HTML remains; active content and dangerous URLs are removed`, () => {
    const { html } = render('<details open><summary>Details</summary>\n\n**bold**\n\n</details>\n\n<script>alert(1)</script><iframe src="https://evil.test"></iframe>\n\n<img src="x" onerror="alert(1)" style="position:fixed" srcset="https://evil.test 2x">\n\n[x](javascript:alert%281%29)\n\n<form><input type="password" name="document"></form>', mode);
    assert.match(html, /<details open>/);
    assert.match(html, /<strong>bold<\/strong>/);
    assert.doesNotMatch(html, /<script|<iframe|onerror|style=|srcset=|javascript:|<form|type="password"|name="document"/);
  });
  test(`${mode}: inline dollars, backtick math, fenced math and code isolation`, () => {
    const { html } = render('$x^2$ and $`y_1`$\n\n```math\n\\frac{1}{2}\n```\n\n`$not_math$`', mode);
    assert.equal((html.match(/class="katex"/g) || []).length, 3);
    assert.match(html, /application\/x-tex">y_1</);
    assert.doesNotMatch(html, /application\/x-tex">`/);
    assert.match(html, /<code>\$not_math\$<\/code>/);
  });
  test(`${mode}: Mermaid is preserved for the offline diagram renderer`, () => {
    const { html } = render('```mermaid\nflowchart LR\n A-->B\n```', mode);
    assert.match(html, /class="mermaid-source"/);
    assert.match(html, /A--&#x3E;B|A--&gt;B|A-->B/);
  });
  test(`${mode}: emoji and mentions stay out of code and links`, () => {
    const { html } = render(':rocket: @alice\n\n`:rocket: @alice`\n\n[:rocket:](https://example.com)', mode);
    assert.match(html, /🚀/);
    assert.ok(html.includes(`href="https://${mode === 'github' ? 'github.com' : 'qiita.com'}/alice"`));
    assert.match(html, /<code>:rocket: @alice<\/code>/);
    assert.match(html, />:rocket:<\/a>/);
  });
  test(`${mode}: invalid math and unsupported diagrams retain readable source`, () => {
    const result = render('```math\n\\notacommand{x}\n```\n\n```plantuml\nAlice -> Bob\n```', mode);
    assert.equal(result.diagnostics.length, 2);
    assert.match(result.html, /notacommand/);
    assert.match(result.html, /Alice/);
  });
}

test('soft breaks and single-tilde behavior differ by mode', () => {
  assert.match(render('a\nb\n\n~x~').html, /<p>a\nb<\/p>/);
  assert.match(render('a\nb\n\n~x~').html, /<del>x<\/del>/);
  assert.match(render('a\nb\n\n~x~', 'qiita').html, /<p>a<br>\nb<\/p>/);
  assert.doesNotMatch(render('~x~', 'qiita').html, /<del>/);
});
test('all five GitHub alert types; Qiita preserves the quote text', () => {
  for (const type of ['NOTE', 'TIP', 'IMPORTANT', 'WARNING', 'CAUTION']) {
    const source = `> [!${type}]\n> **body**\n>\n> second paragraph`;
    assert.ok(render(source).html.includes(`markdown-alert-${type.toLowerCase()}`));
    assert.match(render(source).html, /<strong>body<\/strong>/);
    assert.doesNotMatch(render(source, 'qiita').html, /markdown-alert/);
  }
});
test('Qiita notes support lists, fenced code, nested notes, and all tones', () => {
  const source = ':::note\n**body**\n\n- first\n- second\n\n```text\n:::\n```\n\n:::note warn\nwarning\n:::\n:::\n\n:::note alert\nalert\n:::';
  const { html, diagnostics } = render(source, 'qiita');
  for (const tone of ['info', 'warn', 'alert']) assert.ok(html.includes(`qiita-note-${tone}`));
  assert.match(html, /<ul>/);
  assert.match(html, /<code class="language-text hljs">:::/);
  assert.equal(diagnostics.length, 0);
  assert.doesNotMatch(render(source).html, /qiita-note/);
});
test('Qiita fences in code and HTML are not interpreted', () => {
  for (const source of ['```text\n:::note warn\nx\n:::\n```', '    :::note info\n    x\n    :::', '<pre>\n:::note info\nx\n:::\n</pre>']) {
    assert.doesNotMatch(render(source, 'qiita').html, /class="qiita-note/);
  }
});
test('unclosed Qiita notes are readable and reported', () => {
  const result = render(':::note info\nbody', 'qiita');
  assert.match(result.html, /:::note info/);
  assert.equal(result.diagnostics.length, 1);
});
test('reference links and footnotes retain document-wide scope across Qiita notes', () => {
  const result = render('Before [label][ref]\n\n:::note info\nInside [label][ref] with footnote[^one]\n:::\n\n[ref]: https://example.com\n[^one]: Footnote', 'qiita');
  assert.equal((result.html.match(/href="https:\/\/example.com"/g) || []).length, 2);
  assert.match(result.html, /id="mdv-user-content-fn-one"/);
});
test('Qiita filenames are escaped; diff code highlights additions and removals', () => {
  const result = render('```diff_python:sample.py\n-print("old")\n+print("new")\n```\n\n```js:<img>\nx\n```', 'qiita');
  assert.match(result.html, /<figcaption>sample.py<\/figcaption>/);
  assert.match(result.html, /diff-add/);
  assert.match(result.html, /diff-remove/);
  assert.match(result.html, /<figcaption>&#x3C;img>|<figcaption>&lt;img>/);
  assert.doesNotMatch(render('```js:file.js\nx\n```').html, /figcaption/);
});
test('frontmatter values are displayed without executing or expanding unsafe YAML', () => {
  const result = render('---\ntitle: "<script>evil</script>"\ntags: [one, two]\n---\n\n# Body');
  assert.match(result.html, /<th>title<\/th>/);
  assert.doesNotMatch(result.html, /<script>/);
  assert.equal(result.outline.length, 1);
});
test('Qiita standalone links are cards without fetching metadata', () => {
  assert.match(render('https://example.com', 'qiita').html, /class="link-card"/);
  assert.doesNotMatch(render('[label](https://example.com)', 'qiita').html, /class="link-card"/);
  assert.doesNotMatch(render('https://example.com').html, /class="link-card"/);
});
test('mode and document size are validated', () => {
  assert.throws(() => renderMarkdown('x', { mode: 'unknown' }));
  assert.throws(() => renderMarkdown('x'.repeat(4 * 1024 * 1024 + 1)));
});
