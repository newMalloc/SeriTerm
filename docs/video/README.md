# SeriTerm v1.2.3 界面演示

视频使用 Windows 上的 SeriTerm 1.2.3 界面和模拟串口数据。开场概览串口收发、实时日志、自动重连与 AI 接入，随后展示工作台、异常搜索、暂停恢复与磨砂设置。MCP 一节展示复制客户端配置、等待 TIMEOUT 和读取命中帧及上下文。镜头运动、指针和转场为后期演示效果。

- [GitHub 首页播放器](https://github.com/newMalloc/SeriTerm#演示)
- [网页版](https://newmalloc.github.io/SeriTerm/video/) 支持暂停、拖动进度和全屏，无背景音。
- [1080p MP4](https://github.com/newMalloc/SeriTerm/releases/download/v1.2.3/SeriTerm-1.2.3-intro-1080p.mp4) 包含用 `music.mjs` 合成的背景音。

首页播放器引用 [issue #1](https://github.com/newMalloc/SeriTerm/issues/1) 的附件。更新视频时可用 `gh issue comment 1 --attach <MP4>` 上传，并将评论中生成的附件地址替换到中英文 README。

## 重新出片

`index.html` 中的 `scenes` 定义画面与时间轴。`shots/` 保存交互过程的界面截图，`../images/` 提供工作台截图和图标。双击 `index.html` 即可播放，也可用 `?t=23` 查看第 23 秒或用 `?static=1` 固定画面供脚本渲染。

在 `docs/video` 下执行。逐帧截图需要 Node.js、pnpm 和本机 Edge，编码需要 PowerShell 7.4 或以上版本以及 ffmpeg。

```powershell
pnpm install
node snap.mjs 2 8 14 26 33 37 41 45
node capture.mjs 0 48 30 frames 95
pwsh -File build.ps1
```

成片和封面输出到 `out/`。可运行 `node check-pages.mjs` 检查线上页面资源，或用 `node inline.mjs` 生成可离线播放的单文件 HTML。
