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
| M6 | 自动重连（指数退避 + 端口回归探测）、启动时自动打开串口 | ✅ 完成（真机拔插已验证） |
| M7 | 日志落盘：文本日志 + 原始字节日志（可重放）、分卷、异步写不阻塞 | ✅ 完成 |
| M5 | 终端模式：逐键即发、回车/退格/Ctrl+C 透传、本地回显、多行粘贴、ANSI 过滤 | ✅ 完成 |
| M8 | 配置预设（★）、应用图标、主题打磨、单文件绿色发布 | ✅ 完成 |

**v1 功能已全部落地并通过 COM5 回环验证**，单文件绿色版见下方「发布」。

### 已验证

- **单元测试 266 个全绿**：断帧边界（空闲/分隔符/跨块/空帧/上限）、HEX 与字节模式解析、ANSI 过滤、终端按键编码、GB2312/UTF-8 跨块解码、显示行存储与淘汰、搜索与淘汰联动、发送组装、定时发送、文件分块发送、日志落盘与重放读取、重连退避与重连流程、**故障归类（拔线 vs 端口被占用）**、配置预设增删改。
- **COM5 回环端到端**（UI 自动化真实点击，含**发布产物本身**）：
  - 打开串口 → 发送 → Rx/Tx 计数一致，日志按帧一行显示；
  - 自动滚动：初始 On → 向上滚动后自动 **Off** → 拉回底部自动 **On**，并出现"▸ N 条新数据"提示条；
  - 切"十六进制显示"后**已有行全部重刷为 HEX**（系统提示行保留）；Ctrl+F 命中计数与跳转正确；
  - **分隔符断帧**：发送 `41 0D 0A 42 0D 0A` 后 Rx 显示为独立的 `A`、`B` 两行（若断帧失效会合成一行 `A B`）；发布版实测 11 轮全部正确；
  - **定时发送**：0.2 秒间隔连续发送，每轮产生 1 条 Tx + 2 条 Rx，计数与字节数吻合；
  - **日志落盘**：日志文件按预期生成（文本 404 B / 原始 108 B = 4 条记录 × 27 B），文本日志含时间戳、方向与真实换行，中文不乱码；
  - **启动时自动打开串口**：启动即为"已打开"状态；
  - **终端模式**：在日志区直接敲 `AT` + 回车 → Rx/Tx 各 4 字节（`AT` + CRLF 双向往返），Tx 侧按整行回显一条；
  - **配置预设**：从下拉框选中预设后，波特率变为 921600、十六进制显示自动勾选；
  - **真机拔插自动重连**（`tools/ui-reconnect-smoke.ps1`，人工拔掉 USB-TTL 后由脚本判定）：
    拔线后提示为「**COM5 连接已中断：设备已被拔出或被禁用**」（而非误报"被其它程序抢占"）、
    自动重连开启时**不弹模态框**、设备插回后 **1 秒左右自动重连成功**，
    全过程界面文本：`链路中断…` → `已开启自动重连…` → `已重连 COM5（第 1 次尝试）。`;
  - 深色 / 浅色两套主题跑同一套自动化验证。

### 已知边界（不打算做）

- 拔线检测依赖系统端口枚举（注册表 `SERIALCOMM`）判定"设备是否还在"：
  仅当端口**仍在系统里但被其它进程独占**时才会提示"被其它程序占用"，这是 Windows 串口独占模型的固有歧义。




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
tools/ui-m5-smoke.ps1          # UI 自动化冒烟：终端模式（真实虚拟键注入 + 回环）
src/
  SeriTerm.Core/               # 纯逻辑层，零 WPF 依赖，可单测
    Serial/                    # 参数模型、传输实现、端口枚举、错误翻译、重连策略与监督者
    Framing/                   # 断帧策略（空闲间隔 / 分隔符）
    Text/                      # HEX 编解码、有状态文本解码、字节模式转义、ANSI 过滤
    Terminal/                  # 终端模式按键编码
    Send/                      # 发送组装、定时发送器、文件分块发送
    Logging/                   # 日志落盘（文本 + 原始字节）、后台写队列、原始日志读取
    Presets/                   # 配置预设模型（增删改与默认命名）
    Pipeline/                  # 接收处理器、显示行、接收选项
    Documents/                 # 日志文档（存储/淘汰/搜索）与批量通知集合
  SeriTerm.App/                # WPF 表现层
    Assets/                    # 应用图标（由 tools/make-icon.ps1 生成）
    Services/                  # 主题、配置存储、用户提示、设备友好名、文件对话框、输入法控制
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
6. **串口故障先问"端口还在不在"，再决定提示什么**：设备被拔出与"被别的程序占用"都是 Win32 `ERROR_ACCESS_DENIED`，只看异常类型必然误判。归类入口是 `SerialErrorTranslator.Classify(ex, portPresent)`，`portPresent` 必须由调用方在故障当下查询。
7. **"默认开启"的开关不能只靠属性变更通知去驱动其它组件**：`[ObservableProperty]` 在值未变化时不触发通知，"默认 true + 配置也是 true"会让功能静默失效（见开发规格 11.17，自动重连真踩过）。

## 发布

```powershell
# 单文件绿色版（推荐分发；目标机器无需安装 .NET 运行时）
pwsh -File tools/publish.ps1

# 精简版（需目标机安装 .NET 8/10 桌面运行时）
pwsh -File tools/publish.ps1 -FrameworkDependent
```

实测产物：`artifacts/publish/SeriTerm.exe`，**单文件 63.7 MB**（自包含 + 压缩）。
该产物已用回环自动化验证通过（打开串口、分隔符断帧、定时发送、终端模式）。

> ⚠️ 不要开启 `PublishTrimmed`：WPF 不支持裁剪。
> 压缩发布（`EnableCompressionInSingleFile`）会让首次启动慢一点点，换来体积减半。

## 配置文件

- 位置：`%AppData%\SeriTerm\settings.json`（不带 BOM 的 UTF-8）
- 内容：主题、上次串口参数、显示/发送/终端设置、自动重连开关、日志目录、配置预设

## 开发脚本

| 脚本 | 用途 |
|---|---|
| `tools/publish.ps1` | 发布单文件绿色版 |
| `tools/make-icon.ps1` | 生成应用图标（含 PNG 预览，可复现） |
| `tools/capture-window.ps1` | 启动应用并截图（含 DPI 感知处理） |
| `tools/ui-smoke-test.ps1` | 冒烟：回环收发、自动滚动、Ctrl+F 搜索 |
| `tools/ui-m4-smoke.ps1` | 冒烟：分隔符断帧、HEX 发送、定时发送 |
| `tools/ui-m5-smoke.ps1` | 冒烟：终端模式（真实虚拟键注入） |
| `tools/ui-m7-smoke.ps1` | 冒烟：日志落盘、启动自动打开串口 |
| `tools/ui-m8-smoke.ps1` | 冒烟：配置预设套用 |
| `tools/ui-reconnect-smoke.ps1` | 冒烟：真机拔插（故障诊断 + 无模态框 + 自动重连恢复） |

> 这些脚本必须用 Windows PowerShell 5.1 运行（UIAutomationClient 只在 .NET Framework 里提供），
> 且文件要保存成**带 BOM 的 UTF-8**（否则中文控件名会被当 ANSI 读成乱码）。

