// 生成动画的背景音（56 秒，48kHz 立体声 WAV）。全部是代码合成的，不涉及任何第三方素材。
//
// 音乐结构：120 BPM、4/4，一小节 2 秒，28 小节 = 56 秒。
//   Am7 – Fmaj7 – Cmaj7 – G6 四小节一循环，共 7 遍。
//   铺底 pad + 低音脉冲 + 八分音符铃声 + 弱底鼓 + 气声踩镲，转场处加噪声渐强（whoosh）。
//
// 用法：node music.mjs [输出路径]，默认 out/theme.wav
import fs from 'node:fs';
import path from 'node:path';

const SR = 48000;
const DUR = 56.05;                 // 比正片略长，混流时按视频长度截断
const N = Math.round(SR * DUR);
const TAU = Math.PI * 2;
const L = new Float32Array(N);
const R = new Float32Array(N);

/* ---------- 正弦波表（整数相位，够快也够准） ---------- */
const TAB_BITS = 13, TAB = new Float32Array((1 << TAB_BITS) + 1);
for (let i = 0; i <= (1 << TAB_BITS); i++) TAB[i] = Math.sin((TAU * i) / (1 << TAB_BITS));
const P2 = 4294967296;
const SHIFT = 32 - TAB_BITS;
const incOf = f => Math.round((f / SR) * P2);
const lpA = cut => Math.exp((-TAU * cut) / SR);

/* 固定种子的白噪声，保证每次生成的文件完全一致 */
let seed = 20260101;
const noise = () => {
  seed = (seed * 1103515245 + 12345) & 0x7fffffff;
  return seed / 0x3fffffff - 1;
};
const resetNoise = s => { seed = s; };

/* ---------- 各声部 ---------- */
// 在 [t0,t0+dur) 上叠一个音：partials 是 [{r: 倍频, a: 幅度}]，可带一个失谐副振荡器做轻微合唱
function voice(t0, dur, freq, amp, { atk = 0.5, rel = 0.7, pan = 0, cut = 1200, partials, detune = 0 } = {}) {
  const n0 = Math.max(0, Math.round(t0 * SR));
  const n1 = Math.min(N, Math.round((t0 + dur) * SR));
  if (n1 <= n0) return;
  const incs = [], gains = [];
  for (const p of partials) { incs.push(incOf(freq * p.r)); gains.push(p.a); }
  if (detune) { incs.push(incOf(freq * detune)); gains.push(0.22); }
  const ph = new Uint32Array(incs.length);
  const a = lpA(cut);
  let st = 0;
  const atkN = Math.max(1, atk * SR), relN = Math.max(1, rel * SR);
  const gl = amp * (1 - pan * 0.45), gr = amp * (1 + pan * 0.45);
  for (let i = n0; i < n1; i++) {
    const x = i - n0, y = n1 - i;
    const env = Math.min(1, x / atkN) * Math.min(1, y / relN);
    let s = 0;
    for (let k = 0; k < incs.length; k++) {
      ph[k] = (ph[k] + incs[k]) >>> 0;
      s += TAB[ph[k] >>> SHIFT] * gains[k];
    }
    s = s * (1 - a) + st * a; st = s;
    const v = s * env;
    L[i] += v * gl; R[i] += v * gr;
  }
}

// 噪声气声：两级一阶低通相减得到带通，中心频率从 f0 升到 f1
function swell(t0, dur, amp, f0, f1) {
  const n0 = Math.max(0, Math.round(t0 * SR));
  const n1 = Math.min(N, Math.round((t0 + dur) * SR));
  const total = Math.max(1, n1 - n0);
  let hiState = 0, loState = 0;
  for (let i = n0; i < n1; i++) {
    const k = (i - n0) / total;
    const f = f0 + (f1 - f0) * k;
    const x = noise();
    const ah = lpA(f), al = lpA(f * 0.22);
    hiState = x * (1 - ah) + hiState * ah;   // 留低频
    loState = x * (1 - al) + loState * al;   // 更低频
    const band = hiState - loState;          // 相减 = 只留 f*0.22 ~ f 这一段
    const env = Math.sin(Math.PI * Math.min(1, k * 1.25)) ** 2;
    const v = band * env * amp * 3.2;
    L[i] += v * 0.85; R[i] += v * 1.15;
  }
}

// 底鼓：正弦从 70Hz 扫到 45Hz，快衰减
function kick(t0, amp) {
  const n0 = Math.round(t0 * SR), n1 = Math.min(N, n0 + Math.round(0.34 * SR));
  let ph = 0;
  for (let i = n0; i < n1; i++) {
    const t = (i - n0) / SR;
    const f = 45 + 30 * Math.exp(-t * 34);
    ph = (ph + incOf(f)) >>> 0;
    const env = Math.exp(-t * 11) * Math.min(1, t * 400);
    const v = TAB[ph >>> SHIFT] * env * amp;
    L[i] += v; R[i] += v;
  }
}

// 踩镲：极短噪声
function hat(t0, amp) {
  const n0 = Math.round(t0 * SR), n1 = Math.min(N, n0 + Math.round(0.06 * SR));
  let hi = 0, prev = 0;
  for (let i = n0; i < n1; i++) {
    const t = (i - n0) / SR;
    const x = noise();
    const lp = x * 0.12 + prev * 0.88; prev = lp;
    const v = (x - lp) * Math.exp(-t * 65) * amp;
    L[i] += v * 0.7; R[i] += v * 1.3;
  }
}

/* ---------- 编曲 ---------- */
const BAR = 2.0;
const BARS = 28;
const KEYS = [
  { bass: 110.00, pad: [220.00, 261.63, 329.63, 392.00], arp: [220.00, 329.63, 392.00, 523.25] }, // Am7
  { bass: 87.31,  pad: [174.61, 220.00, 261.63, 329.63], arp: [174.61, 261.63, 349.23, 440.00] }, // Fmaj7
  { bass: 130.81, pad: [164.81, 196.00, 246.94, 329.63], arp: [261.63, 329.63, 392.00, 523.25] }, // Cmaj7
  { bass: 98.00,  pad: [146.83, 196.00, 246.94, 329.63], arp: [196.00, 246.94, 293.66, 392.00] }, // G6
];
const PAD = [{ r: 1, a: 0.62 }, { r: 2, a: 0.16 }, { r: 3, a: 0.06 }];
const BASS = [{ r: 1, a: 0.9 }, { r: 2, a: 0.12 }];
const BELL = [{ r: 1, a: 1 }, { r: 2.01, a: 0.22 }, { r: 3.02, a: 0.07 }];

resetNoise(1107);
for (let b = 0; b < BARS; b++) {
  const key = KEYS[b % 4];
  const t0 = b * BAR;
  // 铺底：比小节长一点，让和弦之间叠着过渡
  key.pad.forEach((f, i) => voice(t0 - 0.05, BAR + 0.75, f, 0.085, { atk: 0.45, rel: 0.85, pan: i % 2 ? 0.3 : -0.3, cut: 1050, partials: PAD, detune: 1.0035 }));
  // 低音：每小节第 1、3 拍
  for (const off of [0, 1.0]) voice(t0 + off, 0.9, key.bass, 0.105, { atk: 0.012, rel: 0.5, cut: 420, partials: BASS });
  // 底鼓 + 踩镲
  kick(t0, 0.100); kick(t0 + 1.0, 0.075);
  for (let k = 0; k < 4; k++) hat(t0 + 0.25 + k * 0.5, 0.017);
  // 铃声：四分音符为主，八分位置补一个高八度轻音，左右轻摆
  for (let k = 0; k < 8; k++) {
    const f = key.arp[(k >> 1) % 4] * (k % 2 ? 2 : 1);
    const amp = k % 2 ? 0.020 : 0.034;
    voice(t0 + k * 0.25, 0.65, f, amp, { atk: 0.006, rel: 0.5, pan: k % 2 ? 0.5 : -0.5, cut: 5200, partials: BELL });
  }
}

/* 转场气声：与动画的分段起点对齐 */
for (const t of [0, 4.4, 12.8, 20.4, 27.2, 33.4, 40.6, 49.6]) swell(t - 0.45, 0.9, 0.075, 600, 3200);

/* ---------- 混响：几个衰减的延迟拍（廉价但够用） ---------- */
const taps = [[0.113, 0.30, -0.2], [0.197, 0.22, 0.25], [0.311, 0.15, -0.3], [0.471, 0.10, 0.35]];
const dl = new Float32Array(N), dr = new Float32Array(N);
for (const [dt, g, pan] of taps) {
  const d = Math.round(dt * SR);
  for (let i = d; i < N; i++) {
    dl[i] += L[i - d] * g * (1 - pan * 0.5);
    dr[i] += R[i - d] * g * (1 + pan * 0.5);
  }
}
// 混响也过一遍低通，避免毛刺
let ls = 0, rs = 0; const ra = lpA(2600);
for (let i = 0; i < N; i++) {
  ls = dl[i] * (1 - ra) + ls * ra;
  rs = dr[i] * (1 - ra) + rs * ra;
  L[i] += ls; R[i] += rs;
}

/* ---------- 总线：去超低频 + 首尾淡入淡出 + 软限幅 ---------- */
const MASTER = 0.76;                 // 整体电平：成品约 -17 LUFS，做背景音不抢戏
const fadeIn = 1.6, fadeOut = 2.6;
const soft = x => Math.tanh(x * 1.25) / Math.tanh(1.25);
const hpA = lpA(38);                 // 38Hz 以下的高通：去掉听不见却占电平的隆隆声
let hpl = 0, hpr = 0;
for (let i = 0; i < N; i++) {
  const t = i / SR;
  const env = Math.min(1, t / fadeIn) * Math.min(1, Math.max(0, (DUR - t) / fadeOut));
  hpl = L[i] * (1 - hpA) + hpl * hpA;
  hpr = R[i] * (1 - hpA) + hpr * hpA;
  L[i] = soft((L[i] - hpl) * env * MASTER);
  R[i] = soft((R[i] - hpr) * env * MASTER);
}

/* ---------- 写 16bit PCM WAV ---------- */
const bytes = 44 + N * 4;
const buf = Buffer.alloc(bytes);
buf.write('RIFF', 0); buf.writeUInt32LE(bytes - 8, 4); buf.write('WAVE', 8);
buf.write('fmt ', 12); buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20); buf.writeUInt16LE(2, 22);
buf.writeUInt32LE(SR, 24); buf.writeUInt32LE(SR * 4, 28); buf.writeUInt16LE(4, 32); buf.writeUInt16LE(16, 34);
buf.write('data', 36); buf.writeUInt32LE(N * 4, 40);
for (let i = 0; i < N; i++) {
  const l = Math.max(-1, Math.min(1, L[i])), r = Math.max(-1, Math.min(1, R[i]));
  buf.writeInt16LE((l * 32767) | 0, 44 + i * 4);
  buf.writeInt16LE((r * 32767) | 0, 46 + i * 4);
}

const out = path.resolve(process.argv[2] ?? 'out/theme.wav');
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, buf);
console.log(`生成 ${out}：${DUR.toFixed(2)} 秒，${(bytes / 1048576).toFixed(1)} MB`);
