// 抽查若干时刻的画面，输出 PNG 供人眼检查。用法：node snap.mjs 2.2 7.0 8.5 ...
import { chromium } from 'playwright-core';
import fs from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const times = process.argv.slice(2).map(Number);
const dir = 'snap';

const url = 'file:///' + path.join(root, 'index.html').replace(/\\/g, '/') + '?static=1';
const browser = await chromium.launch({ channel: 'msedge', args: ['--hide-scrollbars', '--force-color-profile=srgb'] });
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
page.on('pageerror', e => console.error('PAGEERROR:', e.message));
page.on('console', m => { if (m.type() === 'error') console.error('CONSOLE:', m.text()); });

await page.goto(url, { waitUntil: 'load' });
await page.waitForFunction(() => window.__ready === true, null, { timeout: 60000 });

fs.mkdirSync(dir, { recursive: true });
for (const t of times) {
  await page.evaluate(ms => window.seek(ms), t * 1000);
  const name = t.toFixed(2).replace('.', '_');
  await page.screenshot({ path: path.join(dir, name + '.png'), type: 'png' });
  console.log('snap', t);
}
await browser.close();
