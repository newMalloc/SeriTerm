# SeriTerm

[![CI](https://github.com/newMalloc/SeriTerm/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/newMalloc/SeriTerm/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/newMalloc/SeriTerm?label=release)](https://github.com/newMalloc/SeriTerm/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[English](README.en.md) | **简体中文**

Windows 串口调试助手（C# / WPF / .NET 8）。看数据、发命令、抓日志三件事：
长跑不卡、20 万行不丢、断线自己接回来、单文件绿色版免安装；内置 MCP，AI 可直接读你的设备。

![主界面](docs/images/main.png)

> 上图是实拍（全屏、真实运行）：左栏「AI 接入」里 MCP 已接入 1 个客户端；右上是 `Ctrl+F` 实时查找
> （`error` 命中 4 处、字符级高亮）与「查找收藏」；日志里的设备输出经 COM5 回环实发实收（`Tx`/`Rx` 成对），
> `[AI] 发送 8 字节：STATUS` 是 AI 通过 MCP 发出的命令。图为完全权限档。

## 下载

到 [Releases](https://github.com/newMalloc/SeriTerm/releases) 选一个，两个功能完全一样：

| 产物 | 大小 | 说明 |
|---|---|---|
| `SeriTerm-<版本>-win-x64.exe` | 约 3.8 MB | **推荐**。本身不含 .NET 运行时；已装 .NET 8/9/10 桌面运行时的机器只下这几 MB，没装的会在启动时先问一句，同意后自动从微软官方站点装好（约 56 MB，装一次，之后所有 .NET 程序共用）再继续启动。 |
| `SeriTerm-<版本>-win-x64-full.exe` | 约 67 MB | 运行时整套打包在 exe 内，不联网、不装任何东西，离线 / 内网分发用。 |

两个都是 win-x64 单文件，双击即用，不需要安装程序。

## 功能

**连接**

- 端口枚举带设备友好名（`COM5 (USB-SERIAL CH340)`），不用猜哪个口；
- 波特率 / 数据位 / 校验 / 停止位 / 流控，配置预设可命名保存、一键套用；
- 启动时自动打开上次的端口；**掉线自动重连**（指数退避），拔插不再误报"被其它程序占用"。

**接收**

- 文本 / HEX 双模式，多编码（UTF-8、GB2312 等）跨块解码不乱码；
- 断帧：空闲间隔 / 分隔符 / 不断帧，带缓冲区上限，不粘包也不显示半包；
- 虚拟化日志视图，显示上限 20 万行，超出按段淘汰；暂停显示、清空、保存；
- 时间戳、自动换行、字号可调；**自动滚动智能开关**——向上翻阅自动暂停，底部出现「▸ N 条新数据」。

**发送**

- 文本 / HEX，行尾 CR / LF / CRLF；
- 定时发送；文件分块发送（可取消、带进度）；
- 终端模式：逐键即发，回车 / 退格 / `Ctrl+C` 透传，本地回显。

**查找与复制**

- `Ctrl+F` 实时搜索：命中计数、上下跳转、**字符级高亮**；
- 关键字可收藏，浮层叠在日志右上角，点一下回填并跳到第一处命中；
- 日志可**按字符自由选择**：行内、跨行都行，`Ctrl+C` 拿到的就是屏幕上高亮的那一段。

**日志落盘**

- 文本日志 + **原始字节日志**（可重放回日志区），按大小分卷；
- 后台异步写队列，落盘不阻塞接收。

**AI 接入（MCP）**

- 内置 [MCP](https://modelcontextprotocol.io) 服务端：Claude Desktop / Cursor / VS Code 里的模型
  **直接读到你设备的输出**，不用再把日志复制粘贴过去。左栏「AI 接入」→「复制配置」即可接入；
- **能等**：`serial_wait_for_pattern` 阻塞等到设备打印出指定内容（重启后的第一行、`OK`、`panic`）；
- **是帧不是字节**：读到的数据已按你的断帧设置切好，带到达时间、方向与游标，续读不重不漏；
- **默认只读**：写入类工具不出现在工具清单里；需要发送时在界面上勾「允许 AI 发送数据（完全权限）」，
  状态栏显示当前档位。发送限长 4096 字节、限速 3 次/秒，每次都在日志里留 `[AI]` 审计行；
- 只走本机命名管道（仅当前用户可连），不开任何网络端口。详见 [AI 接入文档](docs/mcp.md)。

**界面**

- 深 / 浅两套主题，运行中即时切换；自绘标题栏；窗口背景模糊（可关闭）；
- 标题栏「关于」或 <kbd>F1</kbd>：版本号、提交号、运行时与仓库入口。

## 环境要求

Windows 10 1809 或更高（64 位）。完整版免装任何运行时；启动器需要 .NET 8 桌面运行时（缺失时会自动装好）。
从源码构建需要 .NET SDK 8.0 或更高。

## 构建与测试

```powershell
dotnet build SeriTerm.sln
dotnet run   --project src/SeriTerm.App
dotnet test  SeriTerm.sln
```

**354 个自动化测试**（346 纯逻辑单测 + 8 回环集成），`dotnet test` 常态全绿。
回环集成测试需要一根 USB-TTL 的 **TX 与 RX 短接**，默认用 `COM5`，换端口不必改源码
（`$env:SERITERM_LOOPBACK_PORT = 'COM3'`）；机器上没有回环硬件时这组自动跳过而不是失败。

MCP 这条链路跨三个进程（AI 客户端 → `--mcp-stdio` 桥 → 界面进程），单测覆盖协议与护栏，
发版前再用 `pwsh -File tools/mcp-smoke.ps1 -Exe <exe>` 端到端走一遍（用法见 [AI 接入文档](docs/mcp.md)）。

## 发布

```powershell
pwsh -File tools/publish.ps1                     # 启动器，约 3.8 MB（默认，分发用）
pwsh -File tools/publish.ps1 -All                # 启动器 + 自包含完整版（约 67 MB）
pwsh -File tools/publish.ps1 -SelfContained      # 只出自包含完整版
pwsh -File tools/publish.ps1 -FrameworkDependent # 框架依赖单文件，约 1.7 MB（开发自用）
```

启动器是 [NativeAOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot/) 编出来的**原生 exe**：
它自己不含 .NET 运行时，所以"机器上什么都没装"时也能双击运行；真正的程序体（框架依赖单文件）
经 Brotli 压缩后内嵌在它里面，启动时解包到 `%LocalAppData%\SeriTerm\app\<版本>\` 再跑（升级后自动清掉旧版本）。

推一个 `v*` 标签，[release.yml](.github/workflows/release.yml) 会用 `-All` 构建并把两个 exe 都挂到同名 Release 上。
Release 说明正文取自 [CHANGELOG.md](CHANGELOG.md) 里对应版本那一节，**发版前先补好**，缺了就报错不发。

## 配置

- 设置：`%AppData%\SeriTerm\settings.json`
- 诊断日志：`%AppData%\SeriTerm\ui-errors.log`（只在出错时写）

## 已知边界

- **「被占用」与「已拔出」在 Win32 层无法区分**（都是 `ERROR_ACCESS_DENIED`）：只有端口仍在系统枚举里、
  却打不开时才提示"被其它程序占用"，其余按设备拔出处理。
- **背景模糊用的是桌面壁纸的模糊副本**，不是实时桌面：窗口只盖住桌面一部分时，桌面图标与其它窗口不会出现在背景里。
- **查找浮层会盖住日志右上角一小块**，这是"不占日志行高"的代价；不搜索时它整块折叠，日志全宽可用。
- 左侧设置栏在窗口较矮时需要滚动才能看全，主操作「打开串口」始终在首屏。
- **MCP 只在界面进程运行时可用**：AI 要读数据，SeriTerm 窗口得开着（关掉程序后工具会返回 `[not_connected]`）；
  「复制配置」给的是当前正在运行的那个 exe 的路径，**升级换版本后路径会变，重新复制一次即可**。
- 单文件绿色版首次启动要自解压到临时目录，比常规发布稍慢一点。

## 许可

[MIT](LICENSE)

设计取舍、验证数字与开发脚本说明见 [开发规格与里程碑](docs/development-plan.md) 与
[验证记录](docs/verification.md)；AI 接入（MCP）的用法与工具清单见 [docs/mcp.md](docs/mcp.md)。
