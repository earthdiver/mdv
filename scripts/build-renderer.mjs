import { build } from 'esbuild';
import { mkdir, cp, readFile, writeFile, readdir, rm } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const out = resolve('src/Mdv.App/Renderer');
await rm(out, { recursive: true, force: true });
await mkdir(out, { recursive: true });
await build({
  entryPoints: ['renderer/browser.mjs'], bundle: true, format: 'esm', splitting: true,
  outdir: out, entryNames: 'viewer', chunkNames: 'chunks/[name]-[hash]',
  target: 'es2022', minify: true, legalComments: 'linked', metafile: true,
}).then(async result => {
  await writeFile(join(out, 'bundle-manifest.json'), JSON.stringify({ inputs: Object.keys(result.metafile.inputs) }, null, 2));
});
await cp('renderer/index.html', join(out, 'index.html'));
await cp('renderer/styles.css', join(out, 'styles.css'));
await cp('node_modules/katex/dist/katex.min.css', join(out, 'katex.min.css'));
await cp('node_modules/katex/dist/fonts', join(out, 'fonts'), { recursive: true });

// Ship dependency license texts alongside the executable, including transitive packages.
const lock = JSON.parse(await readFile('package-lock.json', 'utf8'));
const licenses = [];
for (const [path, metadata] of Object.entries(lock.packages)) {
  if (!path || metadata.dev) continue;
  const names = await readdir(path).catch(() => []);
  const files = names.filter(name => /^(licen[sc]e|copying|notice)([.-]|$)/i.test(name));
  const texts = await Promise.all(files.map(name => readFile(join(path, name), 'utf8').catch(() => '')));
  licenses.push(`${path.replace(/^node_modules\//, '')} ${metadata.version}\n${metadata.license || ''}\n${texts.join('\n')}`);
}
await writeFile(join(out, 'THIRD-PARTY-NOTICES.txt'), licenses.join('\n\n' + '='.repeat(72) + '\n\n'));
console.log(`Renderer and dependency licenses written to ${out}`);
