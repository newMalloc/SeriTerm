// 内联 index.html 的图标和界面截图，产出可离线播放的单文件 HTML。
import fs from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const out = path.join(root, 'out');
fs.mkdirSync(out, { recursive: true });

let html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
const images = new Map();
html = html.replace(/(?:\.\.\/images\/|shots\/)[a-z0-9.-]+\.(png|webp)/g, (ref, type) => {
  if (!images.has(ref)) {
    const data = fs.readFileSync(path.join(root, ref)).toString('base64');
    images.set(ref, 'data:image/' + type + ';base64,' + data);
  }
  return images.get(ref);
});

const target = path.join(out, 'seriterm-intro.html');
fs.writeFileSync(target, html, 'utf8');
console.log('生成', target, (fs.statSync(target).size / 1048576).toFixed(2), 'MB');
