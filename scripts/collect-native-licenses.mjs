import { readFile, readdir, mkdir, cp } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] || 'artifacts/payload-win-x64', 'Licenses');
const runtime = process.argv[3] || 'win-x64';
const assets = JSON.parse(await readFile('src/Mdv.App/obj/project.assets.json', 'utf8'));
const packages = new Set(Object.entries(assets.libraries).filter(([, value]) => value.type === 'package').map(([key]) => key.toLowerCase()));
for (const framework of Object.values(assets.project.frameworks)) {
  for (const dependency of framework.downloadDependencies || []) {
    if (dependency.name === `Microsoft.NETCore.App.Runtime.${runtime}` || dependency.name === `Microsoft.WindowsDesktop.App.Runtime.${runtime}`) {
      packages.add(`${dependency.name.toLowerCase()}/${dependency.version.match(/[\d]+\.[\d]+\.[\d]+[^,\]]*/)[0]}`);
    }
  }
}
await mkdir(output, { recursive: true });
for (const name of packages) {
  let copied = false;
  for (const root of Object.keys(assets.packageFolders)) {
    const directory = join(root, name);
    const files = await readdir(directory).catch(() => []);
    for (const file of files.filter(file => /^(license|third-party-notices|notice)(\.|$)/i.test(file))) {
      await cp(join(directory, file), join(output, name.replace('/', '-') + '-' + file));
      copied = true;
    }
  }
  if (!copied) throw new Error(`Missing native dependency license: ${name}`);
}
console.log(`Native dependency licenses written to ${output}`);
