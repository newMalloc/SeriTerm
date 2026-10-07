# SeriTerm

[![CI](https://github.com/newMalloc/SeriTerm/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/newMalloc/SeriTerm/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/newMalloc/SeriTerm?label=release)](https://github.com/newMalloc/SeriTerm/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[English](README.en.md) | **简体中文**

Windows 串口调试助手（C# / WPF / .NET 8），用于接收数据、发送命令与记录日志：
长时运行不卡顿，日志上限 20 万行，断线自动重连，单文件免安装；内置 MCP，AI 可直接读取设备输出。

https://github.com/user-attachments/assets/60625d5f-2d7c-470b-8355-47cf9d8c60c3

[网页版](https://newmalloc.github.io/SeriTerm/video/)

## 下载

从 [Releases](https://github.com/newMalloc/SeriTerm/releases) 下载。两个产物功能相同，均为 win-x64 单文件，无需安装：

| 产物 | 大小 | 运行时 | 适用场景 |
|---|---|---|---|
| `SeriTerm-<版本>-win-x64.exe` | 约 3.8 MB | 需 .NET 8 桌面运行时（缺失时自动从微软官方安装，约 56 MB） | 常规分发（推荐） |
| `SeriTerm-<版本>-win-x64-full.exe` | 约 67 MB | 随 exe 打包，不联网 | 离线 / 内网分发 |

## 功能

**连接**

- 端口列表显示设备友好名（`COM5 (USB-SERIAL CH340)`）
- 波特率 / 数据位 / 校验 / 停止位 / 流控；配置预设可命名保存并复用
- 启动时自动打开上次使用的端口；掉线后按指数退避自动重连

**接收**

- 文本 / HEX 双模式；UTF-8、GB2312 等多编码，跨数据块解码不产生乱码
- 断帧方式：空闲间隔 / 分隔符 / 不断帧，可设缓冲区上限；按帧输出，不粘连、不输出半包
- 日志视图虚拟化，显示上限 20 万行，超出按段淘汰；支持暂停、清空、保存
- 时间戳、自动换行、字号可调；自动滚动在上翻时暂停，并在底部提示新增行数

**发送**

- 文本 / HEX，行尾 CR / LF / CRLF
- 定时发送；文件分块发送（可取消、带进度）
- 终端模式：逐键发送，回车 / 退格 / `Ctrl+C` 透传，本地回显

**查找与复制**

- `Ctrl+F` 实时搜索：命中计数、上下跳转、字符级高亮
- 关键字收藏为浮层，叠在日志右上角；点击回填搜索框并跳到首个命中
- 日志按字符选择，支持行内与跨行；`Ctrl+C` 复制所选内容

**日志落盘**

- 文本日志与原始字节日志（可重放回日志区），按大小分卷
- 后台异步写队列，落盘不阻塞接收

**AI 接入（MCP）**

- 内置 [MCP](https://modelcontextprotocol.io) 服务端，Claude Desktop / Cursor / VS Code 可直接读取设备输出；
  在左栏「AI 接入」点「复制配置」接入
- `serial_wait_for_pattern` 阻塞等待指定内容出现（重启后的首行、`OK`、`panic`），无需轮询
- 读取结果为已按断帧设置切分的帧，带到达时间、方向与游标；按游标续读不重复、不遗漏
- 默认只读：写入类工具不出现在工具清单中；勾选「允许 AI 发送数据（完全权限）」后开放发送，状态栏显示当前档位
- 写入上限 4096 字节 / 次、速率 3 次/秒，每次发送在日志中留下 `[AI]` 审计行
- 仅通过本机命名管道通信（限当前用户），不开放网络端口；详见 [AI 接入文档](docs/mcp.md)

**界面**

![主界面](docs/images/main.png)

> 实拍（全屏运行）：MCP 已接入 1 个客户端；`Ctrl+F` 搜索 `error` 命中 4 处并字符级高亮；
> 日志经 COM5 回环实发实收（`Tx`/`Rx` 成对）；`[AI]` 行为 AI 经 MCP 发出的命令。

- 深 / 浅两套主题，运行中可切换；自绘标题栏；窗口背景模糊（可关闭）
- 标题栏「关于」或 <kbd>F1</kbd>：版本号、提交号、运行时与仓库入口

## 环境要求

Windows 10 1809 或更高版本（64 位）。完整版无需任何运行时；启动器需要 .NET 8 桌面运行时（缺失时自动安装）。
从源码构建需要 .NET SDK 8.0 或更高版本。

## 构建与测试

```powershell
dotnet build SeriTerm.sln
dotnet run   --project src/SeriTerm.App
dotnet test  SeriTerm.sln
```

**354 个自动化测试**（346 纯逻辑单测 + 8 回环集成），`dotnet test` 一次跑完。
回环测试需要把 USB-TTL 的 TX 与 RX 短接，默认使用 `COM5`；换端口不必改源码
（`$env:SERITERM_LOOPBACK_PORT = 'COM3'`）。机器上没有回环硬件时，该组测试自动跳过，不会失败。

MCP 链路跨三个进程（AI 客户端 → `--mcp-stdio` 桥 → 界面进程）。单测覆盖协议与护栏；
发版前用 `pwsh -File tools/mcp-smoke.ps1 -Exe <exe>` 做一次端到端验证（用法见 [AI 接入文档](docs/mcp.md)）。

## 发布

```powershell
pwsh -File tools/publish.ps1                     # 启动器，约 3.8 MB（默认，分发用）
pwsh -File tools/publish.ps1 -All                # 启动器 + 自包含完整版（约 67 MB）
pwsh -File tools/publish.ps1 -SelfContained      # 只出自包含完整版
pwsh -File tools/publish.ps1 -FrameworkDependent # 框架依赖单文件，约 1.7 MB（开发自用）
```

启动器由 [NativeAOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot/) 编译为原生 exe，
自身不含 .NET 运行时，在未安装运行时的机器上也能启动。程序本体（框架依赖单文件）经 Brotli 压缩后内嵌其中，
启动时解包到 `%LocalAppData%\SeriTerm\app\<版本>\`，升级后自动清理旧版本。

推送 `v*` 标签后，[release.yml](.github/workflows/release.yml) 以 `-All` 构建，并把两个 exe 附到同名 Release。
Release 正文取自 [CHANGELOG.md](CHANGELOG.md) 对应版本一节，缺失时流程报错中止。

## 配置

- 设置：`%AppData%\SeriTerm\settings.json`
- 诊断日志：`%AppData%\SeriTerm\ui-errors.log`（仅出错时写入）

## 已知边界

- **「被占用」与「已拔出」无法区分**：两者在 Win32 层都是 `ERROR_ACCESS_DENIED`。仅当端口仍在系统枚举中、
  却打不开时提示「被其它程序占用」，其余情况按设备拔出处理。
- **背景模糊使用桌面壁纸的模糊副本**，非实时桌面：窗口只覆盖桌面一部分时，背景中不会出现桌面图标与其它窗口。
- **查找浮层遮挡日志右上角一小块**：这是不占用日志行高的取舍。未搜索时浮层折叠，日志恢复全宽。
- 窗口较矮时左侧设置栏需滚动；「打开串口」始终位于首屏。
- **MCP 依赖界面进程**：读取数据时 SeriTerm 需保持运行，退出后工具返回 `[not_connected]`。
  「复制配置」给出的是当前运行 exe 的路径，升级后路径变化，需重新复制。
- 单文件版本首次启动需解压到临时目录，启动略慢于常规发布。

## 许可

[MIT](LICENSE)

设计取舍、验证数字与开发脚本说明见 [开发规格与里程碑](docs/development-plan.md) 与
[验证记录](docs/verification.md)；AI 接入（MCP）的用法与工具清单见 [docs/mcp.md](docs/mcp.md)。
