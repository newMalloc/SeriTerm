// 校验单文件 HTML：能否加载、能否跳到任意时刻、画面是否非空。
import { chromium } from 'playwright-core';
import fs from 'node:fs';
import path from 'node:path';

const file = path.resolve(process.argv[2] ?? 'out/seriterm-intro.html');
const times = [2.2, 16.0, 48.5];
const browser = await chromium.launch({ channel: 'msedge', args: ['--hide-scrollbars'] });
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
page.on('pageerror', e => console.error('PAGEERROR:', e.message));
await page.goto('file:///' + file.replace(/\\/g, '/') + '?static=1', { waitUntil: 'load' });
await page.waitForFunction(() => window.__ready === true, null, { timeout: 30000 });
fs.mkdirSync('verify', { recursive: true });
for (const t of times) {
  await page.evaluate(ms => window.seek(ms), t * 1000);
  const name = 'single-' + t.toFixed(1).replace('.', '_') + '.png';
  await page.screenshot({ path: path.join('verify', name) });
  console.log('ok', t, name);
}
await browser.close();
console.log('单文件 HTML 正常');
