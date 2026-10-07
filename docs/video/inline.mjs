// 把 index.html 引用的两张图内联成 data URI，产出一个单文件 HTML（双击即可播放，不依赖仓库其它文件）。
import fs from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const out = path.join(root, 'out');
fs.mkdirSync(out, { recursive: true });

const URI = {
  '../images/main.png': path.join(root, '..', 'images', 'main.png'),
  '../../src/SeriTerm.App/Assets/seriterm.png': path.join(root, '..', '..', 'src', 'SeriTerm.App', 'Assets', 'seriterm.png'),
};

let html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
for (const [ref, file] of Object.entries(URI)) {
  const data = 'data:image/png;base64,' + fs.readFileSync(file).toString('base64');
  const before = html.split(ref).length - 1;
  html = html.split(ref).join(data);
  console.log(`内联 ${path.basename(file)}：替换 ${before} 处`);
}

const target = path.join(out, 'seriterm-intro.html');
fs.writeFileSync(target, html, 'utf8');
console.log('生成', target, (fs.statSync(target).size / 1048576).toFixed(2), 'MB');
