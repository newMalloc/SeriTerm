# SeriTerm 开发内容（v1 规格）

> 复刻目标：lingguang「串口调试助手」(Serial Debug Assistant) 的**核心串口能力**。
> 已确认边界：**WPF + .NET 8、仅 Windows、单文件绿色版、不做脚本**。
> 本版范围 = 串口核心（参数/开关/收发/HEX/编码/自动断帧/定时发送）+ 终端模式 + 自动重连 + 日志落盘 + **日志视图交互（Ctrl+F 实时搜索、自动滚动智能开关）**。
> 其他已确认项：界面**中文**；**深/浅主题运行中可切换**；测试环境 **COM5（TX–RX 已短接）回环**；全程 **git** 版本控制。

---

## 0. 范围界定

### v1 做什么（In Scope）

| # | 模块 | 关键能力 |
|---|---|---|
| M1 | 串口参数与开关 | 端口枚举（含设备友好名）、波特率/数据位/校验/停止位、DTR/RTS、打开/关闭、错误提示 |
| M2 | 接收管线与显示 | 高吞吐读取、HEX/文本显示、编码切换、自动换行、暂停/清空/保存 |
| M3 | 自动断帧 | 空闲间隔断帧（默认 20 ms，可调）、分隔符断帧、每帧带到达时间戳 |
| M4 | 发送 | 文本/HEX 发送、行尾附加、定时发送、文件发送、发送回显（橙色区分） |
| M5 | 终端模式 | 逐键即发、本地回显、退格/Ctrl+C、粘贴多行、历史命令 |
| M6 | 自动重连 | 掉线检测 + 退避重连、端口回归探测、启动自动打开 |
| M7 | 日志落盘 | 原始字节落盘、文本日志落盘、分卷、异步写不阻塞接收 |
| M8 | 界面与交付 | 深/浅主题切换、配置预设（★收藏）、配置持久化、快捷键、窗口置顶、单文件发布 |
| M9 | 日志视图交互 | **Ctrl+F 实时搜索**命中高亮/计数/跳转；**自动滚动智能开关**（手动切换、滚到最新自动开、滚离底部自动停） |

### v1 明确不做（Out of Scope）

JavaScript 脚本引擎、波形绘制、TCP/UDP 网络调试、MQTT、扩展命令（AT 命令库）、多串口并行。

**但架构必须留口子**，让这些后续能以"加模块"而不是"改架构"的方式接进来：

- `ISerialTransport`（收发字节流）→ 将来 `TcpTransport` 实现同一接口，网络调试零重构。
- `IFrameSplitter` → 将来按协议断帧。
- `IReceiveSink` 管道上预留 `ITransformHook { byte[] OnSend(byte[]); void OnReceive(byte[]); }` 空实现 → 将来挂脚本。

---

## 1. 技术选型

| 项 | 选型 | 说明 |
|---|---|---|
| 目标框架 | `net8.0-windows` | 本机已装 SDK 9.0.304 + WindowsDesktop ref pack 8.0.19，可直接编译 |
| UI | WPF（`UseWPF=true`） | 与原工具同技术栈，暗色主题/虚拟化列表成熟 |
| MVVM | `CommunityToolkit.Mvvm` 8.x | 源生成器 `[ObservableProperty]` / `[RelayCommand]`，无运行时反射 |
| 串口 | `System.IO.Ports` 9.0.x | .NET Core 后不在 BCL 内，需 NuGet |
| 编码 | `System.Text.Encoding.CodePages` | 注册后才能用 GB2312 / BIG5 / Shift-JIS |
| 设备友好名 | `System.Management`（WMI `Win32_PnPEntity`） | 显示 `COM3 (CH340)`；失败时降级为纯 `COM3` |
| 配置持久化 | `System.Text.Json` → `%AppData%\SeriTerm\settings.json` | 原子写（先写 .tmp 再替换） |
| 日志 | `Microsoft.Extensions.Logging` + 自写文件 sink | 量小，不引入 Serilog |
| 依赖注入 | `Microsoft.Extensions.DependencyInjection` | 组合根在 `App.xaml.cs` |
| 测试 | xUnit + `SeriTerm.Core.Tests` | 断帧/HEX/解码/环形缓冲/退避纯逻辑可测 |
| 打包 | `dotnet publish` 单文件自包含 | 见 §7 |

**明确不用的做法**（都是这类工具翻车的经典原因）：

1. ❌ `SerialPort.DataReceived` 事件 —— 在线程池上触发、会丢事件、高波特率必丢数据。
2. ❌ 每收到一个字节就走一次 `Dispatcher.Invoke` —— 1 Mbps ≈ 10 万次/秒，UI 必卡死。
3. ❌ `RichTextBox` / `TextBox` 追加显示日志 —— O(n²) 重排，几万行就卡。
4. ❌ 在 UI 线程上 `SerialPort.Open()/Close()` —— 会阻塞到超时。

### TFM 支持期提醒

`.NET 8` 的 LTS 支持期到 **2026-11-10**（本机当前日期 2026-09-30，约 6 周后结束）。
**结论（v1 按此推进）**：先用 `net8.0-windows` 落地——本机已装 SDK 9.0.304 与 8.0.19 的 WindowsDesktop ref pack，可离线编译、立刻开工。是否升到 **`net10.0-windows`（LTS 到 2028）**推迟到 M8 打包前决定；升级动作 = 改 `Directory.Build.props` 里的 `TargetFramework`（App/Core 两处）+ 全量复测，WPF API 两版之间几乎无差异。

---

## 2. 解决方案结构

```
SeriTerm/
├─ SeriTerm.sln
├─ Directory.Build.props              # 统一 LangVersion / Nullable / TreatWarningsAsErrors
├─ src/
│  ├─ SeriTerm.Core/                  # 纯逻辑，零 WPF 依赖，可单测
│  │  ├─ Serial/
│  │  │  ├─ SerialSettings.cs         # 参数模型 + 校验
│  │  │  ├─ ISerialTransport.cs       # 传输抽象（将来 TCP 复用）
│  │  │  ├─ SerialPortTransport.cs    # System.IO.Ports 实现
│  │  │  ├─ PortEnumerator.cs         # 端口枚举 + WMI 友好名
│  │  │  └─ ReconnectPolicy.cs        # 退避策略（纯函数，可单测）
│  │  ├─ Framing/
│  │  │  ├─ IFrameSplitter.cs
│  │  │  ├─ GapFrameSplitter.cs       # 空闲间隔断帧
│  │  │  └─ DelimiterFrameSplitter.cs
│  │  ├─ Pipeline/
│  │  │  ├─ ReceivePipeline.cs        # 读线程 → 断帧 → 解码 → 展示行
│  │  │  ├─ RingBuffer.cs             # 定容显示缓冲
│  │  │  └─ IReceiveSink.cs
│  │  ├─ Text/
│  │  │  ├─ HexCodec.cs               # HEX 解析/格式化（宽容输入）
│  │  │  └─ StatefulTextDecoder.cs    # 跨块有状态解码，防中文乱码
│  │  └─ Logging/
│  │     ├─ RawLogWriter.cs           # 原始字节落盘（可重放）
│  │     └─ TextLogWriter.cs          # 带时间戳/方向的文本日志
│  ├─ SeriTerm.App/                   # WPF 表现层
│  │  ├─ App.xaml(.cs)                # DI 组合根
│  │  ├─ Views/MainWindow.xaml
│  │  ├─ ViewModels/                  # MainVm / SerialSettingsVm / ReceiveVm / SendVm / TerminalVm
│  │  ├─ Controls/LogView.cs          # 高性能虚拟化日志控件
│  │  ├─ Themes/Dark.xaml             # 深色主题资源字典
│  │  └─ Converters/
│  └─ SeriTerm.Tests/                 # xUnit
└─ docs/
```

3 个项目足够；将来网络调试独立成 `SeriTerm.Net`，`SeriTerm.Core` 不需要动。

---

## 3. 核心接口（骨架）

```csharp
public sealed record SerialSettings
{
    public string PortName { get; init; } = "";
    public int BaudRate { get; init; } = 115200;
    public int DataBits { get; init; } = 8;
    public Parity Parity { get; init; } = Parity.None;
    public StopBits StopBits { get; init; } = StopBits.One;
    public Handshake Handshake { get; init; } = Handshake.None;
    public bool DtrEnable { get; init; }
    public bool RtsEnable { get; init; }
}

/// 收发字节流的抽象；SerialPortTransport 与将来的 TcpTransport 都实现它
public interface ISerialTransport : IAsyncDisposable
{
    TransportState State { get; }
    event EventHandler<TransportState>? StateChanged;
    Task OpenAsync(SerialSettings settings, CancellationToken ct);
    Task CloseAsync();
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    /// 每读到一块数据回调一次，带单调时钟时间戳（Stopwatch ticks）
    event Action<ReadOnlyMemory<byte>, long>? BytesReceived;
}

public interface IFrameSplitter
{
    /// 输入字节块 + 到达时间戳，产出 0..n 个完整帧
    void Append(ReadOnlySpan<byte> data, long timestamp, List<RawFrame> output);
    /// 定时调用：把因空闲而结束的最后一帧吐出来
    void FlushIdle(long now, List<RawFrame> output);
}

public readonly record struct RawFrame(long StartTimestamp, byte[] Data);

/// 显示行：一帧在界面上的呈现单位
public sealed record DisplayLine(
    DateTime Time, LineDirection Direction, byte[] Raw, string Text, int Length);

public enum LineDirection { Rx, Tx, System }
```

### 读取模型（关键实现约定）

```csharp
// 专用后台线程；不使用 DataReceived
_stream.ReadTimeout = 100;                    // ms，必须有限，否则关不掉
var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
while (!ct.IsCancellationRequested)
{
    try
    {
        int n = _stream.Read(buffer, 0, buffer.Length);   // 同步阻塞读
        if (n > 0) RaiseBytes(buffer.AsSpan(0, n), Stopwatch.GetTimestamp());
    }
    catch (TimeoutException) { /* 100ms 空转，回到循环检查取消标志 */ }
    catch (IOException)      { RaiseFaulted(); break; }   // 设备拔出 → 触发重连
}
```

> **为什么用"有限超时 + 轮询取消"而不是 `ReadAsync(ct)`**：`SerialPort` 的取消令牌无法可靠中断已阻塞的读，`Close()` 也可能永久挂住。100 ms 超时读是最稳的实践做法，开销可忽略。

---

## 4. 各模块开发内容

### M1 串口参数与开关

**功能点**
- 端口下拉：`SerialPort.GetPortNames()` + WMI 友好名，异步刷新（工具栏 `↻`），插拔即刷新。
- 波特率下拉**可编辑**：预置 9600…921600，以及 1000000 等非标准值；非法输入即时校验。
- 数据位 5–8、校验 None/Odd/Even/Mark/Space、停止位 1/1.5/2、流控 None/XOnXOff/RequestToSend。
- DTR / RTS 可勾选；CTS / DSR / RI 只读显示（`PinChanged` 事件驱动状态灯）。
- 打开/关闭按钮状态机；打开中禁用参数控件。

**实现要点**
- `Open()` 放后台线程，`Task.Run` 包装，避免 UI 阻塞。
- 异常映射成人话：`UnauthorizedAccessException` → "端口被其它程序占用"；`IOException` → "设备已移除或参数不被驱动支持"；`ArgumentException` → "端口名不存在"。
- 开关串行化：`SemaphoreSlim(1,1)` 保护 `Open`/`Close`。

**验收**：能打开/关闭真实或虚拟串口；拔掉设备后按钮状态与提示正确；1 Mbps 打开失败时给出明确原因。

---

### M2 接收管线与显示

**功能点**
- 显示模式：文本 / HEX（HEX 支持"每行字节数"、"附带 ASCII 侧栏"）。
- 编码切换：ASCII / UTF-8 / GB2312 / BIG5 / Shift-JIS / Unicode（运行中可切，不影响已存原始字节）。
- 自动换行开关、暂停/继续接收、清空、保存显示内容到文件、**将接收实时保存到文件**。
- 状态栏：Rx/Tx 字节数、当前帧率、缓冲区占用、丢弃字节数。

**实现要点**
- **读线程 → 无锁环形缓冲 → UI 批量刷新**三段解耦：读线程只写缓冲并发信号；UI 用 30 Hz 定时器批量取新增行，一次性刷新。
- 显示缓冲定容（默认 20 万行，可配），超出滚动淘汰最旧。
- **有状态解码**：`Encoding.GetDecoder()` 跨块续解，帧尾 `flush: true`，彻底解决中文被切成两半的乱码。
- 背压：UI 消费不过来时**丢显示、不丢数据**（数据仍可落盘），并在状态栏提示"已丢弃 N 行显示"。
- 日志控件自实现：`RingBuffer<DisplayLine>` + `VirtualizingStackPanel`，只渲染可视区；不使用 `RichTextBox`。

**验收**：1 Mbps 连续灌数据 10 分钟，Rx 计数与发送方完全一致、UI 可流畅滚动、内存无持续增长。

---

### M3 自动断帧

**功能点**
- **空闲间隔断帧**：距上一字节超过 N ms（默认 20，可调 1–1000）即认为一帧结束，换行显示。
- **分隔符断帧**：按 `\n` / `\r\n` / 自定义字节串切分（混用场景更准）。
- 每帧记录**起始时间戳**，显示时可选择是否带时间戳前缀。

**实现要点**
- 时间基准统一用 `Stopwatch.GetTimestamp()`（单调时钟），不要用 `DateTime.Now`（会被系统对时抖动）。
- 需要一个**低频 flush 定时器**（`gap/2`，下限 5 ms）：否则"最后一包"要等到下次收到数据才显示。
- 计时器塌缩式实现（每收一块就重置计时）在高速流下会漏切分，改为**时间戳扫描式**：记录每块的到达时刻，在块内按累计时间切分。
- 边界用例纳入单测：大包被驱动拆成多块、多包粘成一块、正好等于阈值的间隔、零长块。

**验收**：单测覆盖上述 4 类边界；实测 `01 02 03`(停 50 ms) `04 05` 显示为两行。

---

### M4 发送

**功能点**
- 文本发送（按当前编码） / HEX 发送（宽容解析：`01 02 FF`、`0x01,0x02`、`0102FF` 都接受，非法字符高亮报错）。
- 行尾附加：无 / `\r` / `\n` / `\r\n`（AT 指令必备）。
- **定时发送**：周期 T（0.1 s 起），可配"仅当链路空闲时才发"；发送中显示倒计时。
- **文件发送**：分块发送、进度与取消。
- 发送历史：`↑`/`↓` 翻历史，`Ctrl+Enter` 快速发送。
- 发送内容显示在接收区（橙色，对应原工具的"显示发送字符串"），可开关。

**实现要点**
- 写操作全部经 `SemaphoreSlim` 串行化，定时器与手动发送不重入。
- `WriteTimeout` 设为有限值；写入失败按 M6 规则转入重连。

**验收**：AT 指令回环可读；1 KB 文件按块发送无粘连；定时发送 1.0 s 精度误差 < 50 ms。

---

### M5 终端模式

**功能点**
- 逐键即发：可打印字符立即写串口；`Enter` 按行尾设置发送；`Backspace` 发 `0x08`/`0x7F`（可配）并本地删除；`Ctrl+C` 发 `0x03`。
- 本地回显开关（默认关，真实终端靠设备回显）。
- 粘贴多行 → 逐行发送 + 行尾处理。
- 与普通模式互斥切换，切换时保留日志内容。

**实现要点**
- 用 `PreviewKeyDown` 拦截，避免控件自身的文本编辑行为干扰。
- 最小 ANSI 处理：至少保证 `ESC[...m` 之类的序列不破坏显示（v1 可仅过滤，不做着色）。
- 终端模式下屏蔽 M3 的换行插入，改为原样流式显示。

**验收**：能直接与串口 shell / 设备 CLI 交互，方向键历史与退格行为符合预期。

---

### M6 自动重连

**功能点**
- "自动重连"（掉线恢复）与"自动开关串口"（启动即打开上次端口）两个独立开关。
- 掉线检测：`IOException` / 设备移除事件 / 端口从 `GetPortNames()` 消失。
- 退避策略：1 s → 2 s → 4 s → 8 s（上限 10 s，可配固定间隔），重连成功后在状态栏提示。
- 连续失败 N 次（默认无限，可配）后停止并提示。

**实现要点**
- 状态机：`Closed → Opening → Open → Faulted → Waiting → Opening`，所有迁移在单线程（UI Dispatcher 或专用 actor）内完成，禁止并发迁移。
- 端口回归探测：1 s 轮询 `GetPortNames()`（WMI 友好名不必每轮查，开销大）。
- 退避计算抽成 `ReconnectPolicy` 纯函数，单测覆盖序列。

**验收**：运行中拔掉 USB 转串口 → 状态转 Faulted 并提示；插回后自动恢复到 Open 且继续接收。

---

### M7 日志落盘

**功能点**
- **原始日志**：纯字节流（可被本工具或第三方直接重放），可选带长度前缀/时间戳封装。
- **文本日志**：`时间戳 [Rx] 数据`，沿用当前编码与 HEX 设置。
- 文件分卷：按大小（默认 100 MB）或按天；命名 `SeriTerm_yyyyMMdd_HHmmss.log`。
- 目录默认 `%USERPROFILE%\Documents\SeriTerm\Logs`，可改；提供"打开日志目录"。
- 写入全程后台，接收线程零阻塞；崩溃/断电时最多丢最后 1 个缓冲块。

**实现要点**
- `Channel<byte[]>` 无界/有界队列 + 单消费者写 `FileStream`（64 KB 缓冲，定期 `Flush`）。
- 队列积压超阈值时告警并落盘降级（先保证不丢、再保证不卡）。

**验收**：1 Mbps 落盘 10 分钟，文件大小与收发字节数吻合；关闭程序后文件可直接重放。

---

### M8 界面与交付

**功能点**
- 布局：左侧设置面板（端口/接收/发送三组，可折叠）+ 右侧日志区 + 底部状态栏，中间用 `GridSplitter` 可拖拽。
- **深/浅主题**：两套资源字典（`Themes/Dark.xaml`、`Themes/Light.xaml`），运行中即时切换、无需重启，选择持久化；可选"跟随系统"。字号/字体可调（工具栏 `AA`）、自动换行（`¶`）、窗口置顶（`Ctrl+Shift+T`）。
- **配置预设（★）**：命名保存"端口+参数+编码+断帧+行尾"整套配置，下拉一键套用。
- 配置持久化：窗口位置/大小、上次配置、主题、显示上限。
- 快捷键：`Ctrl+Enter` 打开/关闭、`Ctrl+K` 清空、`Ctrl+S` 保存显示、`Ctrl+Shift+T` 置顶、`Ctrl+F` 搜索（可延后）。

**验收**：重启后所有配置与窗口位置恢复；预设切换后立即按预设参数打开端口。

---

### M9 日志视图交互（与 M2 的日志控件同期落地）

**功能点**

**1) Ctrl+F 实时搜索**
- 呼出搜索条，**输入即搜**（增量，不阻塞 UI 线程）；`Esc` 关闭、`Enter`/`F3` 下一个、`Shift+Enter` 上一个。
- 命中计数 `第 n / 共 m 条`；全部命中高亮，当前命中用强调色并自动滚入可视区。
- 搜索目标可选：当前显示文本 / 原始 HEX（十六进制显示时按字节串搜）。
- 选项：区分大小写、全字匹配（可选）。
- **实时**：搜索期间新到达的数据行也参与匹配，命中计数持续更新。
- 性能：在显示环形缓冲上做**增量匹配**（新行增量扫、旧行按需重扫），10 万行内输入响应 < 50 ms。

**2) 自动滚动智能开关**
- 日志区提供"自动滚动"开关，可手动开/关，状态写入配置持久化。
- 用户拖动滚动条或滚轮**离开底部** → 自动关闭滚动，角标提示"已暂停滚动 · 点击回到最新"。
- 用户滚动/拖回**接近底部**（阈值约 8 px 或最后一行完全可见）→ **自动重新开启**滚动。
- 开关开启时新数据平滑跟随到底部；关闭时视口冻结，底部显示"**N 条新数据**"提示条，点击即回到底部并恢复滚动。
- 搜索跳转、清空、切换显示模式等**程序发起的滚动**不得误触发开关状态翻转。

**实现要点**
- "是否在底部"判定：`ScrollViewer.VerticalOffset >= ScrollableHeight - epsilon`。
- **区分滚动来源是本功能的关键**（也是最容易出 bug 的地方）：仅当 `ScrollChangedEventArgs.ExtentHeightChange == 0 && VerticalChange != 0` 时才认定为用户滚动；程序追加数据导致的 `ExtentHeightChange != 0` 必须忽略。
- 关闭滚动时只冻结视口，数据仍照常写入缓冲，保证不丢、可回看。

**验收**
- 关掉自动滚动后持续灌数据：视口静止、可向上翻阅历史、底部提示新数据条数；拖到底部后开关自动打开并跟随。
- `Ctrl+F` 在 1 Mbps 持续数据下输入 `AT`：实时定位、计数正确、命中高亮无闪烁。

---

## 5. 里程碑与工作量（单人估算）

| 里程碑 | 内容 | 估算 | 出口标准 |
|---|---|---|---|
| M0 | 解决方案骨架、DI、暗色主题、空窗口 | 0.5 d | `dotnet run` 起窗口 |
| M1 | 端口枚举 + 参数 + 开关 | 1 d | 能稳定开关真实/虚拟串口 |
| M2 | 接收管线 + 显示 + 编码 | 2 d | 1 Mbps 压测不丢不卡 |
| M3 | 断帧 + HEX 显示 | 1.5 d | 单测 + 实测断帧正确 |
| M4 | 发送 + 定时 + 文件 | 1 d | 定时精度达标、文件发送无粘连 |
| M5 | 终端模式 | 1 d | 可与设备 CLI 交互 |
| M6 | 自动重连 | 1 d | 拔插自动恢复 |
| M7 | 日志落盘 | 0.5 d | 长跑落盘可重放 |
| M8 | 预设/主题/快捷键/单文件发布 | 1 d | 绿色包拷走即用 |
| M9 | 日志视图交互（Ctrl+F + 自动滚动） | 1 d | 搜索实时、滚动开关判定无误 |
| — | **合计** | **≈ 10.5 人天** | |

建议顺序：**M0 → M1 → M2 → M9 → M3**（先把"高速不丢不卡 + 日志视图交互"这两块核心体验打牢）→ M4 → M6 → M7 → M5 → M8。

---

## 6. 关键风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| 高波特率下丢数据 / UI 卡死 | 工具不可用 | 读线程与 UI 彻底解耦、环形缓冲、30 Hz 批量刷新、虚拟化；M2 就压测，不达标不往下走 |
| `SerialPort` 关闭时读线程不退出 | 卡死/崩溃 | 有限 `ReadTimeout` + 取消标志 + 开关串行化；不用 `ReadAsync(ct)` |
| 中文被切包乱码 | 数据误读 | 有状态 `Decoder` 跨块续解，帧尾 flush |
| 非标准波特率（1000000）驱动不支持 | 打不开口 | 打开失败给出明确原因，可一键回退到标准波特率 |
| WMI 友好名获取慢或被策略禁用 | 列表卡顿 | 异步获取 + 超时 + 降级为纯 COM 名 |
| 单文件自包含体积（≈150 MB） | 分发体积大 | 开压缩发布；或提供 framework-dependent 版本（需装桌面运行时） |
| 断帧阈值不适用于高速流 | 帧切分错误 | 提供"间隔/分隔符"两种策略并可调，20 ms 不是硬编码 |

---

## 7. 构建与发布

```powershell
# 开发
dotnet run --project src/SeriTerm.App

# 单文件绿色版（self-contained，无需装运行时）
dotnet publish src/SeriTerm.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none

# 轻量版（需目标机装 .NET 8/10 桌面运行时，约 5 MB）
dotnet publish src/SeriTerm.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

> ⚠️ 不要开 `PublishTrimmed`：WPF 不支持裁剪，会产生运行时找不到类型的错误。

---

## 8. 测试方案

**单元测试（`SeriTerm.Tests`）**
- `GapFrameSplitter`：多块粘包 / 大包拆分 / 等值间隔 / 零长块 / flush 边界。
- `HexCodec`：合法与非法输入的解析与格式化。
- `StatefulTextDecoder`：GB2312 中文跨块续解、切换编码。
- `RingBuffer`：容量淘汰、并发读写正确性。
- `ReconnectPolicy`：退避序列与上限。

**集成/压测（需要环境）**
- 虚拟串口对：[com0com](https://sourceforge.net/projects/com0com/) 建 COM10↔COM11，一端发一端收，校验字节数一致。
- **本机 COM5（TX–RX 已短接）回环**：`SeriTerm.Tests` 的回环集成测试直接打开 COM5、写入 N 字节、读回逐字节比对；UI 侧自发自收也走 COM5。做自动重连验证时由你把 USB-TTL 拔掉再插回。
- 压测指标：1,000,000 bps 连续 10 分钟 → 丢包 0、滚动帧率 > 30 fps、内存平稳、日志文件大小吻合。

---

## 9. 已确认事项与待定项

### 已确认（据此开发）

| # | 事项 | 结论 |
|---|---|---|
| 1 | 测试环境 | USB-TTL，**COM5 的 TX–RX 已短接**，用于回环验证；需要拔插验证自动重连时由你配合操作 |
| 2 | 界面语言 | **中文**（默认，与截图一致），不要求中英双语 |
| 3 | 主题 | **深色 + 浅色，运行中可切换**，选择持久化 |
| 4 | 日志视图 | 必须支持 **Ctrl+F 实时搜索** 与 **自动滚动智能开关**（详见 M9） |
| 5 | 版本控制 | **git**（约定见 §10） |

### 待定（不阻塞开工，按默认值推进）

1. **TFM**：默认 `net8.0-windows` 落地；M8 打包前决定是否升 `net10.0-windows`。
2. **扩展命令**（AT 命令库、分组批量发送）：暂列 v1.1（约 0.5 天）；若要，插在 M4 之后。

---

## 10. 版本控制约定（git）

- 仓库根 = `D:\Project\CSharp\SeriTerm`，主分支 `main`；**一个里程碑至少一个提交**，功能点可再细分。
- 提交信息：`<类型>(<范围>): <中文简述>`，类型取 `feat / fix / refactor / test / docs / chore / build`。示例：
  - `feat(serial): 端口枚举与打开关闭`
  - `feat(logview): Ctrl+F 实时搜索与自动滚动智能开关`
  - `test(framing): 补充空闲断帧边界用例`
- `.gitignore` 覆盖 `bin/`、`obj/`、`.vs/`、`*.user`、`artifacts/`、`publish/`。
- **不提交**：发布产物、日志样本、含设备敏感信息的抓包、本机端口配置（配置放 `%AppData%\SeriTerm\`）。
- 每个里程碑提交前必须：`dotnet build` 通过 + 单测通过 + 手工验收项走一遍。

---

## 11. 实现记录（踩坑与决策）

开发中真实踩到、值得写下来避免重复踩的点：

### 11.1 有状态解码器不能用 `GetCharCount` + `GetChars`

对 `Decoder` 先调 `GetCharCount` 再调 `GetChars`，会让内部挂起状态被消费两次，
结果是**半个汉字变成替换字符**（正是我们要消灭的乱码）。
正确做法：一次 `GetChars` 写进足够大的缓冲（`bytes.Length + 2`），用返回值确定字符数。

### 11.2 自动滚动的"滚动来源"判定

- 程序自身滚动（`ScrollIntoView`）必须用抑制标志排除，否则会被当成用户操作而关掉跟随；
  抑制标志用 `DispatcherPriority.Background` 延后解除，因为 `ScrollIntoView` 引发的
  `ScrollChanged` 可能在布局阶段才派发。
- 用户滚动判定：只有 `ExtentHeightChange == 0 && VerticalChange != 0` 才是拖动/滚轮；
  追加或淘汰数据导致的偏移变化必须忽略。
- 判定"在底部"留 8 px 容差，避免浮点误差导致永远判不到底部。

### 11.3 日志区的性能三条

1. 一批追加合并成**一次 Reset**（`BulkObservableCollection`），不是逐行 `Add`。
2. 淘汰旧行用**整段重建**；`RemoveAt(0)` 循环在 20 万行规模下是 O(n²)，会卡死数秒。
3. 淘汰要同时受**行数与字节数**双重预算约束：1 Mbps + 20 ms 断帧时每帧约 2.5 KB，
   只按 20 万行限制会吃 500 MB 内存。

### 11.4 重刷显示时要跳过系统提示行

切换 HEX/编码会重刷所有行；系统提示行（"串口已打开…"）没有原始字节，
若不跳过会被刷成空字符串。

### 11.5 视图模型的构造顺序

`_processor` 必须在 `ApplySettingsToUi` **之前**创建：读配置会设置 `HexDisplay` 等属性，
其变更回调会走到 `ApplyReceiveOptions`，此时若 `_processor` 还是 null 就会崩在构造函数里。
（初始值恰好与默认值相同时不会触发，属于"换个配置就启动失败"的隐藏 bug。）

### 11.6 硬件相关测试的稳定性

- 串口是**独占**资源：测试程序集必须 `DisableTestParallelization`。
- 上一个用例关闭端口后，系统释放句柄有延迟：打开要**带重试**，否则偶发"被占用"。
- 每个用例开始前先**排空驱动缓冲里的残留字节**，否则会读到上一次的数据。
- 端口不存在时用 `Skip`（自定义 `LoopbackFact`）而不是让测试失败。

### 11.7 自动化脚本的两个坑

- Windows PowerShell 5.1 会把**无 BOM 的 UTF-8** 脚本当 ANSI 读，中文控件名会乱码
  → `tools/*.ps1` 必须存成"带 BOM 的 UTF-8"。
- `SendKeys` 只有当窗口**确实在前台**时才会打到它身上；UIA 的 `ValuePattern.SetValue`
  不依赖焦点，更适合自动化。查找控件优先用 `AutomationId`（即 XAML 的 `x:Name`），比中文 `Name` 稳。
- 异常退出的自动化会留下占用串口与 exe 的僵尸进程，必须清理后再构建/测试。

### 11.8 UIA 自动化：不要缓存"刚出现"的窗口元素

窗口句柄（HWND）比 WPF 内容出现得早。如果在这一刻抓取窗口的 UIA 元素并长期持有，
拿到的往往是**旧式 HWND 代理**，它的子树永远是空的（`FindAll` 返回 0 个元素），
表现为"等待控件超时"却百思不得其解。
正确做法：每次从 `RootElement` 按进程号重新枚举，而不是缓存窗口元素。

另外 `FindFirst(PropertyCondition)` 在 WPF 上也不可靠（某些 peer 的 Name/AutomationId
是延迟计算的，条件匹配可能查不到，稍后枚举又能看到）。统一用"枚举 + 手动比较"。

### 11.9 其它两个小坑

- PowerShell 5.1 的 `Set-Content -Encoding UTF8` 会写 BOM；给程序读的 JSON 配置必须用
  `UTF8Encoding($false)` 写**不带 BOM**，否则 `JsonSerializer` 会因 BOM 解析失败而静默退回默认配置。
- PowerShell 里 `[char]0x53D1 + [char]0x9001` 是**整数加法**，不是字符串拼接；
  带 BOM 的脚本直接用中文字面量即可。

### 11.10 界面易用性：不要把输入框绑成"未打开就禁用"

发送内容输入框最初绑定了 `IsEnabled="{Binding IsOpen}"`，结果用户（和自动化）在打开串口前
无法先准备好要发的内容，只能"先连上再打字"。正确做法是：输入框始终可编辑，只禁用"发送"动作。

### 11.11 定时发送的线程边界

`TimedSender` 跑在线程池线程上，而写日志会碰绑定到界面的集合。因此
`RecordTransmitted` / `AddSystemLine` 都先检查 `Dispatcher.CheckAccess()`，不在界面线程时
`InvokeAsync` 切回去，否则定时发送一开就抛跨线程访问异常。

### 11.12 自动重连：不要把"自己造成的 Closed"当成用户关闭

重连尝试失败时，传输层会把状态置为 `Closed`（这是对的）。但监督者如果不加区分地
在 `Closed` 时取消重连循环，就会**取消掉自己**：表现为第一次重连失败后再也不试了。
解法：用 `_connecting` 标志标记"这次 Closed 是我自己造成的"，只有非自身原因
（用户点了关闭）才取消循环。这个 bug 是被"达到最大尝试次数应放弃"这条单测抓出来的。

### 11.13 日志落盘：直接杀进程会丢缓冲

`BufferedLogWriter` 只在写满 32 KB 时主动 Flush，退出时才做最终 Flush。
因此自动化验证不能"写完就 Kill 进程"——那样文件里什么都没有。
正确做法是通过界面把"将接收保存到文件"取消勾选（触发 `DisposeAsync` → Flush），
或者在测试里显式 `await DisposeAsync()`。这一点也提醒了产品行为：异常退出会丢最后一块缓冲。

### 11.14 终端模式必须先关掉输入法（中文用户必踩）

装了中文输入法时，终端模式的按键会先进**系统输入法的组合过程**：
WPF 把按键报成 `Key.ImeProcessed`（`ImeProcessedKey` 也是 `ImeProcessed`，拿不到真实键），
**回车会被输入法当作"上屏"吃掉**，结果是"敲了 AT 却发不出去、回车没反应"。

试过的做法与结论：
- `ImmAssociateContext(hwnd, NULL)` 摘输入法上下文：对现代 TSF 输入法**无效**（旧 API）；
- 真正有效的是 WPF 层的 `InputMethod.SetPreferredImeState(..., InputMethodState.Off)`
  配合 `InputMethod.Current.ImeState = InputMethodState.Off`。
  设置之后按键恢复成 `A`/`T`/`Return`，回车正常。

### 11.15 用 SendKeys 做自动化时的一个陷阱

`SendKeys` 注入字符用的是 `VK_PACKET`（KEYEVENTF_UNICODE），WPF 同样会把它报成
`Key.ImeProcessed`——**看起来像"输入法问题"，其实是注入方式问题**。
自动化里要用真实的虚拟键（`keybd_event` 发 VK_RETURN/VK_BACK 等）才能模拟真人按键。
排查时正是靠"关掉输入法后按键变成 A/T/Return"这一现象，才把两者区分开。





