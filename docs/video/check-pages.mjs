// 检查页面（默认线上 Pages）：能否加载、资源是否都取到、在非 16:9 的窗口里有没有被裁。
// 用法：node check-pages.mjs [url] [宽] [高]
import { chromium } from 'playwright-core';
import fs from 'node:fs';
import path from 'node:path';

const url = process.argv[2] ?? 'https://newmalloc.github.io/SeriTerm/video/?static=1';
const vw = Number(process.argv[3] ?? 1280);
const vh = Number(process.argv[4] ?? 720);

const browser = await chromium.launch({ channel: 'msedge', args: ['--hide-scrollbars'] });
const page = await browser.newPage({ viewport: { width: vw, height: vh } });
const bad = [];
page.on('pageerror', e => bad.push('PAGEERROR ' + e.message));
page.on('response', r => { if (r.status() >= 400) bad.push(`HTTP ${r.status()} ${r.url()}`); });
await page.goto(url, { waitUntil: 'load', timeout: 90000 });
await page.waitForFunction(() => window.__ready === true, null, { timeout: 90000 });

// 舞台缩放后应当完整落在视口里（宽高都不超过窗口）
const box = await page.evaluate(() => {
  const r = document.getElementById('stage').getBoundingClientRect();
  return { w: r.width, h: r.height, x: r.x, y: r.y, vw: innerWidth, vh: innerHeight };
});
if (box.w > box.vw + 1 || box.h > box.vh + 1 || box.x < -1 || box.y < -1) {
  bad.push(`舞台超出视口：stage=${box.w.toFixed(1)}x${box.h.toFixed(1)} @ ${box.x.toFixed(1)},${box.y.toFixed(1)} 视口=${box.vw}x${box.vh}`);
}

fs.mkdirSync('verify', { recursive: true });
for (const t of [2.2, 48.5]) {
  await page.evaluate(ms => window.seek(ms), t * 1000);
  await page.screenshot({ path: path.join('verify', `pages-${vw}x${vh}-${t}.png`) });
}
console.log(`视口 ${vw}x${vh}：舞台 ${box.w.toFixed(0)}x${box.h.toFixed(0)} @ (${box.x.toFixed(0)},${box.y.toFixed(0)})`);
console.log(bad.length ? '问题：\n' + bad.join('\n') : '正常：无页面报错、无 4xx/5xx 资源、画面完整在窗口内');
await browser.close();
