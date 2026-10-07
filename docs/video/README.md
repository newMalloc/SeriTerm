# SeriTerm 介绍动画（网页版）

一支 56 秒的产品介绍动画：**一张网页**（`index.html`），画面由 JS 按时间轴逐帧算出来，
用浏览器逐帧截图再编码成 mp4；背景音也是代码合成的（`music.mjs`），没用任何现成素材。
成品挂在 [Releases](https://github.com/newMalloc/SeriTerm/releases) 上（`SeriTerm-intro-1080p.mp4` / 720p）。

## 在浏览器里看

双击 `index.html` 即可播放（循环）。加参数：

- `index.html?t=23` —— 停在 23 秒那一帧
- `index.html?static=1` —— 只渲染第 0 帧（截图脚本用这个）

图片直接引用仓库里已有的文件，不另存一份：界面截图用 `docs/images/main.png`，
图标用 `src/SeriTerm.App/Assets/seriterm.png`。所以整个动画没有构建步骤、没有依赖。

## 画面结构

时间轴写在 `index.html` 顶部的 `SCENES`（每段的起止秒），八段依次是：

| 秒 | 内容 |
|---|---|
| 0–4.6 | 开场：图标、名字、一句话定位 |
| 4.4–13.0 | 主界面巡览：实拍界面 + 聚光打点（端口设置 / 接收设置 / 查找 / AI 接入） |
| 12.8–20.6 | 长跑不卡：日志滚动 + 20 万行 / 30 Hz / 异步写盘 |
| 20.4–27.4 | 断帧：字节流被切成完整帧，三种断帧方式 |
| 27.2–33.6 | 自动重连：断线、指数退避、恢复 |
| 33.4–40.8 | 查找与收藏：Ctrl+F 打字、字符级高亮、关键字收藏 |
| 40.6–49.8 | MCP：客户端 → 命名管道 → 工具清单 → 权限开关 → `[AI]` 审计行 |
| 49.6–56.0 | 收尾：两个下载产物、仓库地址 |

文案、配色（`:root` 里的变量）、动画节奏都在同一个文件里：各段画面是 `renderS1`…`renderS8`。

## 重新出片

需要 Node（脚本用 `playwright-core` 驱动本机 Edge，`@ffmpeg-installer/ffmpeg` 提供 ffmpeg）：

```powershell
cd docs/video
pnpm install                                  # node_modules/ 已被 .gitignore 忽略
node snap.mjs 7.0 16.0 37.5                   # 抽查这几秒，看图校对到 snap/
node capture.mjs 0 56 30 frames 95            # 逐帧 1920x1080@30fps，约 90 秒
pwsh -File build.ps1                          # 合成背景音 + 编码 out/*.mp4 + 抽封面
node inline.mjs                               # 可选：图片内联成单文件 HTML
```

`check-single.mjs` / `probe.mjs` 是排查用的：前者校验单文件 HTML，后者打印页面报错堆栈。

## 背景音

`music.mjs` 用代码合成 56 秒背景音（`out/theme.wav`），不引用任何现成素材，所以没有版权问题：
120 BPM、Am7–Fmaj7–Cmaj7–G6 四小节一循环共 7 遍，铺底 pad + 低音 + 铃声 + 弱底鼓 + 气声踩镲，
并在每个分段的起点（0 / 4.4 / 12.8 / 20.4 / 27.2 / 33.4 / 40.6 / 49.6 秒）叠一层噪声渐强做转场。
`build.ps1` 会先跑它，再把音轨混进两个 mp4。想调风格就改 `music.mjs`：`KEYS` 是和弦与音区，
`MASTER` 是整体电平（当前约 -17.6 LUFS，做背景音不抢戏），`BARS` 小节数。单独试听：

```powershell
node music.mjs out\theme.wav
ffplay out\theme.wav
```

