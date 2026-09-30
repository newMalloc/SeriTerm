# SeriTerm

Windows 串口调试助手（C# / WPF / .NET 8），复刻 [lingguang「串口调试助手」](https://lgblog.github.io/Help/zh-Hans/) 的核心串口能力。

> 开发规格与模块清单见 [docs/development-plan.md](docs/development-plan.md)。

## 当前进度

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M0 | 解决方案骨架、依赖注入、深/浅主题（含系统标题栏跟随）、配置持久化 | ✅ 完成 |
| M1 | 端口枚举（含 WMI 设备友好名）、参数配置、打开/关闭、收发字节计数 | ✅ 完成 |
| M2 | 接收管线与日志视图：虚拟化列表、HEX/文本切换、多编码、自动断帧、暂停/清空/保存 | ✅ 完成 |
| M9 | Ctrl+F 实时搜索（计数 + 上下跳转 + 命中高亮）、自动滚动智能开关 | ✅ 完成 |
| M3 | 断帧策略：空闲间隔 / 分隔符（CRLF 等）/ 不断帧，含缓冲区上限保护 | ✅ 完成 |
| M4 | 发送：HEX/文本、行尾可选、定时发送、文件分块发送（可取消 + 进度） | ✅ 完成 |
| M6 | 自动重连（指数退避 + 端口回归探测）、启动时自动打开串口 | ✅ 完成（真机拔插待验） |
| M7 | 日志落盘：文本日志 + 原始字节日志（可重放）、分卷、异步写不阻塞 | ✅ 完成 |
| M5 / M8 | 终端模式；配置预设、主题打磨、单文件绿色发布 | 计划中 |

### 已验证

- **单元测试 195 个全绿**：断帧边界（空闲/分隔符/跨块/空帧/上限）、HEX 与字节模式解析、GB2312/UTF-8 跨块解码、显示行存储与淘汰、搜索与淘汰联动、发送组装、定时发送、文件分块发送、**日志落盘与重放读取、重连退避与重连流程**。
- **COM5 回环端到端**（UI 自动化真实点击）：
  - 打开串口 → 发送 → Rx/Tx 计数一致，日志按帧一行显示；
  - 自动滚动：初始 On → 向上滚动后自动 **Off** → 拉回底部自动 **On**，并出现"▸ N 条新数据"提示条；
  - 切"十六进制显示"后**已有行全部重刷为 HEX**（系统提示行保留）；Ctrl+F 命中计数与跳转正确；
  - **分隔符断帧**：发送 `41 0D 0A 42 0D 0A` 后 Rx 显示为独立的 `A`、`B` 两行（若断帧失效会合成一行 `A B`）；
  - **定时发送**：0.2 秒间隔连续发送，每轮产生 1 条 Tx + 2 条 Rx，计数与字节数吻合；
  - **日志落盘**：日志文件按预期生成（文本 404 B / 原始 108 B = 4 条记录 × 27 B），文本日志含时间戳、方向与真实换行，中文不乱码；
  - **启动时自动打开串口**：启动即为"已打开"状态；
  - 深色 / 浅色两套主题跑同一套自动化验证。

### 待人工确认

- **自动重连的真机拔插**：重连逻辑有 6 个单测覆盖（用可控假传输层模拟"故障 → 设备回来 → 重连成功/放弃"），但真实"拔掉 USB-TTL 再插回"的端到端场景需要人工配合验证。




## 环境要求

- Windows 10 1809 或更高
- .NET SDK 8.0 或更高（本机使用 9.0.304）
- 目标框架：`net8.0-windows`

## 构建与运行

```powershell
dotnet build SeriTerm.sln
dotnet run --project src/SeriTerm.App
```

## 测试

```powershell
dotnet test SeriTerm.sln
```

- 回环测试需要把 USB-TTL 的 **TX 与 RX 短接**（本机为 `COM5`）。
- 端口不存在时该组测试会被标记为 **skipped** 而不是 failed，不会污染结果。
- 串口是独占硬件资源，测试程序集已禁用并行执行。

## 目录结构

```
SeriTerm.sln
Directory.Build.props          # 全解决方案编译约定
docs/development-plan.md       # 开发规格（范围、模块、里程碑、风险、实现记录）
tools/capture-window.ps1       # 启动应用并截图（含 DPI 感知处理）
tools/ui-smoke-test.ps1        # UI 自动化冒烟：真实点击打开/发送，验证回环、自动滚动、搜索
tools/ui-m4-smoke.ps1          # UI 自动化冒烟：分隔符断帧、HEX 发送、定时发送
tools/ui-m7-smoke.ps1          # UI 自动化冒烟：日志落盘、启动自动打开串口
src/
  SeriTerm.Core/               # 纯逻辑层，零 WPF 依赖，可单测
    Serial/                    # 参数模型、传输实现、端口枚举、错误翻译、重连策略与监督者
    Framing/                   # 断帧策略（空闲间隔 / 分隔符）
    Text/                      # HEX 编解码、有状态文本解码、字节模式转义
    Send/                      # 发送组装、定时发送器、文件分块发送
    Logging/                   # 日志落盘（文本 + 原始字节）、后台写队列、原始日志读取
    Pipeline/                  # 接收处理器、显示行、接收选项
    Documents/                 # 日志文档（存储/淘汰/搜索）与批量通知集合
  SeriTerm.App/                # WPF 表现层
    Services/                  # 主题、配置存储、用户提示、设备友好名、文件对话框
    ViewModels/                # MainViewModel 等
    Controls/                  # LogView（虚拟化日志 + 自动滚动 + 搜索条）
    Themes/                    # Shared.xaml + Dark.xaml + Light.xaml
  SeriTerm.Tests/              # xUnit（含 COM5 回环集成测试）
```

## 关键设计约束

这几条是这类工具不丢数据、不卡死的前提，改动时请勿违反：

1. **不使用 `SerialPort.DataReceived` 事件**：它在线程池上触发、会丢事件，高波特率必丢数据。传输层用专用后台线程做阻塞读。
2. **读取使用有限超时（100 ms）+ 取消标志轮询**：`SerialPort` 的取消令牌无法可靠中断已阻塞的读，`Close()` 可能永久挂住。
3. **读取线程绝不碰 UI**：只做计数/入队，界面侧约 30 fps 批量刷新。
4. **一批日志只发一次集合通知**：逐行通知会让 WPF 每行跑一次布局；淘汰旧行用整段重建，不用 `RemoveAt(0)` 循环（20 万行规模下是 O(n²)）。
5. **主题全部走 `DynamicResource`**：切换深/浅色无需重启。

## 发布

```powershell
# 单文件绿色版（自包含，无需安装运行时）
dotnet publish src/SeriTerm.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none
```

> ⚠️ 不要开启 `PublishTrimmed`：WPF 不支持裁剪。

## 配置文件

- 位置：`%AppData%\SeriTerm\settings.json`
- 内容：主题、上次串口参数、窗口尺寸、以及后续功能的开关
