import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { requiresSession } from '../src/modules/identity/route-canonical';
import { destinationLabel } from '../src/modules/identity/identity-workflow';

const root = fileURLToPath(new URL('../src/app', import.meta.url));
function pages(): { route: string; source: string }[] {
  const found: { route: string; source: string }[] = [];
  const walk = (directory: string) => {
    for (const name of readdirSync(directory)) {
      const path = join(directory, name);
      if (statSync(path).isDirectory()) walk(path);
      else if (name === 'page.tsx') found.push({ route: '/' + relative(root, directory).split(sep).filter(Boolean).join('/'), source: readFileSync(path, 'utf8') });
    }
  };
  walk(root);
  return found;
}
const legacy = new Set(['/account', '/account/orders', '/account/orders/[id]', '/checkout', '/catalog/browse']);
// FIX-RULES owns /catalog/public and retitles it "Contoh katalog untuk tamu" in its own change; only its robots rule is checked here.
const titledElsewhere = new Set(['/catalog/public']);

test('every signed-in page has its own title and is kept out of search indexes (G-026)', () => {
  const missing: string[] = [];
  for (const { route, source } of pages()) {
    if (!requiresSession(route) || legacy.has(route)) continue;
    const title = /title:\s*(['"])([^'"]+)\1/.exec(source)?.[2];
    if (!title || !title.endsWith(' | Unit Toko')) missing.push(route + ': title');
    else if (destinationLabel(route) && !titledElsewhere.has(route) && title !== destinationLabel(route) + ' | Unit Toko') missing.push(route + ': title "' + title + '" is not the page name "' + destinationLabel(route) + '"');
    if (!/robots:\s*\{\s*index:\s*false/.test(source)) missing.push(route + ': robots');
  }
  assert.deepEqual(missing, []);
});
