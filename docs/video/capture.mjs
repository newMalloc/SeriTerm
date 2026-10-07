// 逐帧截取网页动画。用法：
//   node capture.mjs <起始秒> <结束秒> [fps] [输出目录] [jpeg质量]
import { chromium } from 'playwright-core';
import fs from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const start = Number(process.argv[2] ?? 0);
const end = Number(process.argv[3] ?? 56);
const fps = Number(process.argv[4] ?? 30);
const outDir = process.argv[5] ?? 'frames';
const quality = Number(process.argv[6] ?? 95);

const url = 'file:///' + path.join(root, 'index.html').replace(/\\/g, '/') + '?static=1';

const browser = await chromium.launch({
  channel: 'msedge',
  args: ['--hide-scrollbars', '--force-color-profile=srgb', '--disable-gpu-vsync'],
});
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
page.on('pageerror', e => console.error('PAGEERROR:', e.message));
page.on('console', m => { if (m.type() === 'error') console.error('CONSOLE:', m.text()); });

await page.goto(url, { waitUntil: 'load' });
await page.waitForFunction(() => window.__ready === true, null, { timeout: 60000 });

fs.mkdirSync(outDir, { recursive: true });
const n0 = Math.round(start * fps);
const n1 = Math.round(end * fps);
const began = Date.now();
for (let i = n0; i < n1; i++) {
  await page.evaluate(ms => window.seek(ms), (i * 1000) / fps);
  await page.screenshot({
    path: path.join(outDir, 'f' + String(i).padStart(5, '0') + '.jpg'),
    type: 'jpeg',
    quality,
  });
  const done = i - n0 + 1;
  if (done % 90 === 0 || i === n1 - 1) {
    const el = (Date.now() - began) / 1000;
    console.log(`帧 ${i} t=${(i / fps).toFixed(2)}s 已用 ${el.toFixed(1)}s 预计总 ${(el / done * (n1 - n0)).toFixed(0)}s`);
  }
}
console.log(`完成 ${n1 - n0} 帧，用时 ${((Date.now() - began) / 1000).toFixed(1)}s -> ${outDir}`);
await browser.close();
