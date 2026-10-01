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

### 11.16 拔线 = UnauthorizedAccessException，与"被其它程序占用"同码不同因

真机反馈：USB-TTL 手动拔掉后，界面报**"COM5 访问被拒绝：设备可能被其它程序抢占"**
并弹模态框——把一个"你拔了线"的普通事实，说成了"有别的程序在抢你的串口"，
让人去找一个根本不存在的元凶。

原因：设备被拔出时，`SerialPort` 底层拿到的是 Win32 的 `ERROR_ACCESS_DENIED(5)`，
于是抛 `UnauthorizedAccessException: Access to the path 'COM5' is denied.`——
**和"端口被别的程序独占"是同一个异常类型、同一个错误码**。只看异常类型必然误判。

结论与做法：
- 唯一可靠的判据是**问系统"这个端口现在还枚举得到吗"**（`PortEnumerator.IsPortPresent`，
  即注册表 `HARDWARE\DEVICEMAP\SERIALCOMM`）。于是新增 `SerialFaultKind`
  （`DeviceRemoved` / `PortBusy` / `DriverError` / `InvalidPort`），
  `Classify(ex, portPresent)` 一律要求调用方提供**故障当下**查到的端口存在性；
- 查询时留 **120 ms 二次确认窗口**：查到"还在"时稍等再查一次，吸收拔线瞬间的注册表滞后，
  否则仍会误判成"被占用"。实测该窗口并非必需（拔线与"端口消失"同秒生效），但作为保险保留；
- 错误码判据只认 HResult，不再按消息文本猜（原来的 `ex.Message.Contains("设备")` 之流
  既不可靠又会误伤，已删除）。

### 11.17 自动重连"勾着却没生效"：属性默认值把变更通知吃掉了

**这是本轮最严重的 bug，只有真机拔插才能暴露。**

```csharp
private bool _autoReconnect = true;                    // 字段默认 true
_reconnect = new ReconnectSupervisor(_transport);      // Enabled 默认 false
// ApplySettingsToUi: AutoReconnect = settings.AutoReconnect;  // 载入值也是 true
partial void OnAutoReconnectChanged(bool v) => _reconnect.Enabled = v;  // 值没变化 → 从不执行
```

`[ObservableProperty]` 生成的 setter 在**值未变化时直接返回**，于是源生成的部分方法
从不被调用，监督者永远是关的。用户界面上"自动重连"是勾选状态（因为默认就是勾的），
实际拔线后一动不动——比不提供该功能更糟。

教训：**"带默认值的可观察属性"作为另一个组件的开关时，不能只依赖变更通知**。
修法是构造时显式给初值（`new ReconnectSupervisor(_transport) { Enabled = AutoReconnect }`），
载入配置后再同步一次兜底。凡"默认开"的功能，冒烟测试必须真的走一遍触发路径——
单测里 `{ Enabled = true }` 是手工设的，永远碰不到这个坑。

### 11.18 链路故障不要弹模态框

非用户操作直接引发的故障（拔线、驱动报错）弹模态框有三个坏处：
挡住界面、阻塞键盘、并且暗示"你必须做点什么"——而自动重连开着时用户**什么都不用做**。
现在只有两种情况弹框：用户主动点"打开"失败（那是他的直接操作），以及**放弃重连**（真的没救了）。
拔线改用一行日志 + 状态栏文字。

另外两条随之修正的小问题：
- 重连等待设备时**不计入重试次数**。否则拔线放着不管一会儿就会"连续 N 次失败，已停止自动重连"——
  而设备没插回来并不是失败；
- 设备不在时用**固定 0.5 秒轮询**而不是退避：原来每次都要等完退避（上限 10 秒）才检查端口，
  插回后最坏要等 10 秒才恢复。现在插回后约 1 秒重连。

### 11.19 Windows 10 的标题栏不吃 `DWMWA_USE_IMMERSIVE_DARK_MODE`

症状：运行中点"切换到浅色/深色"，客户区全变了，**最上面那条标题栏还是白的**。

实测（Windows 10 19045）：

```
GET|attr20 hr=0x00000000 value=0
SET|attr20 hr=0x00000000            ← 返回 S_OK
GET2|attr20 hr=0x00000000 value=1   ← 读回来也是 1
CAPTION|a-start   |1-44:#FFFFFF
CAPTION|b-attr20  |1-44:#FFFFFF     ← 像素一个都没变
CAPTION|d-framechanged |1-44:#FFFFFF ← 强制重算非客户区也没用
```

也就是说这个属性在 Win10 上**会被接受、能读回、但 Win32 窗口的标题栏完全不理会**
（UWP/WinUI 的标题栏才会跟随；这也解释了为什么"微软商店里的那个串口助手"看起来能跟随主题）。
结论：想让顶部跟随主题，只能**自绘标题栏**：`WindowStyle=None` + `WindowChrome`，
标题栏用 `DynamicResource` 取色，窗口按钮自己画。`TitleBarTheme` 的调用保留（Win11 的边框/系统菜单仍可用），
但不能再指望它。

### 11.20 第三方窗口拿不到 DWM 背景模糊：改成自己模糊壁纸

目标是"背景模糊透出桌面"。Windows 11 有 Mica（本机是 Win10 19045，没有），
于是先按老办法试 `SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)`。实测：

```
APPLY|s4-f2-w99 |ret=1   ← 成功
SAMPLE|s4-f2-w99|#DCDCDC #DCDCDC … ← 全屏同一颜色，没有任何模糊
（AccentState 1/2/3/4/5/6 × AccentFlags 0/2 × 深浅两种底色，
  外加 DwmExtendFrameIntoClientArea(-1) 把玻璃区扩到整个客户区：九种组合全是这样）
```

而同一时刻**系统任务栏的亚克力是正常的**（任务栏区域能看出被压暗的壁纸色块），
即 DWM 自身支持模糊，只是拒绝为第三方窗口提供这套旧接口（远程/虚拟显示会话里尤其如此）。
可验证的旁证：把表面画刷换成半透明后，像素值精确等于"半透明画刷叠在纯黑上"（`#DCDCDC` = `#F4F4F4@0xA8`
再叠 `#FFFFFF@0xA0`），说明窗口背后什么都没有。

于是改成 **WPF 内自己模糊壁纸**（`DesktopBackdrop`）：读 `HKCU\Control Panel\Desktop\WallPaper`
（取不到就用 `%AppData%\Microsoft\Windows\Themes\TranscodedWallpaper`，覆盖幻灯片/聚焦），
降采样到 320 px 宽后做 3 次可分离盒式模糊（滑动窗口累加，几十毫秒），
得到一张很小的图交给 GPU 拉伸铺满窗口。好处：不依赖 DWM、任何会话都能用、静态图不占每帧开销。

配套两点：
- 半透明表面**只在模糊真的生效时**才启用（`SurfaceTranslucency` 往 `Application.Resources` 末尾
  合并一个覆盖字典，WPF 的合并字典是后加入者优先，所有引用处本来就是 `DynamicResource`）。
  取原始颜色必须从**当前主题字典**取，不能从 `Application.Resources` 查——那样会读到自己的半透明覆盖，
  刷新几次 alpha 就越乘越深；
- 深/浅主题各有一层蒙版（`BackdropTintBrush`）：壁纸通常是亮的，深色主题不加蒙版会让浅色文字糊掉。

### 11.21 自绘标题栏的三个坑

1. **最大化会盖住任务栏**。`WindowStyle=None` + `GlassFrameThickness=0` 时客户区就是整个窗口，
   而 WPF 按"无边框全屏"处理最大化，实测窗口矩形 `-10,-10,2570,1610` 而工作区是 `0,0,2560,1540`
   （多出来的正是 `ResizeBorderThickness`）。修法是在窗口消息钩子里处理 `WM_GETMINMAXINFO`，
   用 `GetMonitorInfo` 的 **rcWork** 设 `ptMaxPosition/ptMaxSize/ptMaxTrackSize`。修好后两者完全一致。
2. **`DockPanel` 的最后一个子元素默认是"填充"而不是"停靠"**。标题栏里左侧标题 + 右侧按钮组，
   按钮组写在最后 → 被当成填充元素并从左往右排，结果三个窗口按钮跑到标题文字后面
   （UIA 实测 `rect=574..772`，而窗口右边缘是 2240）。加 `LastChildFill="False"` 后回到 `2040..2238`。
   这个坑很隐蔽：截图里"标题后面跟着三个按钮"看着也不算太怪，是 UIA 矩形把它揪出来的。
3. **背景对齐不能用 `Window.Left` / `ActualWidth`**。最大化过程中这两个值会有一段时间是旧值，
   算出来的 `ImageBrush.Viewbox` 会超出图像范围，右侧露出一条 318 px 宽、没有背景的竖带
   （像素正好等于"日志底色叠在纯色窗口背景上"）。改成用 `GetWindowRect`（物理像素，按当前 DPI 换算回 DIP）
   并 clamp 到 `[0,1]`，同时在 `LocationChanged/SizeChanged/StateChanged` 里合并成一次 Background 优先级更新。

顺带记录一条排查手段：`WM_NCHITTEST` 的 `lParam` 是**屏幕坐标**，一开始按客户区坐标发，
得到的全是没有意义的 `HTCLIENT`；换成屏幕坐标后一次就对上了。

### 11.22 退出路径：每次关窗都弹崩溃框、进程还退不掉

现象（修复前，**每个版本都存在**）：点关闭 → 弹「SeriTerm 发生严重错误」→ 进程不退；点掉弹窗后进程以
`0xE0434352`（CLR 未处理异常）结束。事件日志里能看到真正的第一条：

```
System.InvalidOperationException: 'SeriTerm.App.ViewModels.MainViewModel' type only implements
IAsyncDisposable. Use DisposeAsync to dispose the container.
   at Microsoft.Extensions.DependencyInjection.ServiceProvider.Dispose()
   at SeriTerm.App.App.OnExit(...) App.xaml.cs:line 84
```

两个缺陷叠在一起：

1. `App.OnExit` 里调 `_services.Dispose()`，而容器里有只实现 `IAsyncDisposable` 的单例
   （`MainViewModel`），DI **同步**释放会直接抛异常。改成 `_services.DisposeAsync().AsTask().Wait(...)`，
   顺带把 `MainViewModel` 的手工释放也交给容器（它本来就会 flush 日志、关串口）；
2. `OnDispatcherUnhandledException` 里再 `_services.GetService<IUserNotifier>()`，
   而此时容器已经/正在释放 → 抛 `ObjectDisposedException` → "本来能被处理的界面异常"升级成
   **AppDomain 未处理异常 + 模态框 + 进程退不掉**。修法：启动时就把 `IUserNotifier` 解析成字段，
   并且整个处理器包在 try/catch 里——异常处理里再抛异常，等于把诊断信息也一起埋了。

顺带加了一个只在出错时写的 `%AppData%\SeriTerm\ui-errors.log`：发布版是单文件 exe、没有控制台、
日志提供程序只有 Debug（Release 下看不到），上面第 1 条就是靠它才拿到的。
另外注意：`Dispatcher.BeginInvoke` 排队的回调在关窗过程中**仍会执行**，
所以回调里必须先判断 `IsLoaded && !Dispatcher.HasShutdownStarted` 再碰窗口/HWND。

现在的实测结果：点关闭 **1 秒内退出、退出码 0、`settings.json` 已落盘**。

### 11.23 主界面重做：先量，再改

M11 的目标是把 M1–M10 一路堆出来的界面收敛一遍。先按"多余 / 布局不合理 / 不美观 / 不稳重"四类
逐条列出怀疑项，再用 UIA 量出真实矩形（`tools/probe-layout.ps1`）确认，最后才动 XAML。

**改前的实测清单**（都有截图与矩形数字支撑）：

| 问题 | 实测依据 | 处理 |
|---|---|---|
| `⚙` 设置、`?` 帮助两个按钮没有任何命令，点了没反应 | UIA 枚举按钮：两者 `IsEnabled=True` 但 `InvokePattern` 无任何副作用（对应功能从未实现） | 删除（不实现就不放） |
| "自动滚动"在工具栏和侧栏各有一个 | 枚举：1 个 `CheckBox 自动滚动` + 1 个 `ToggleButton AutoScrollToggle`，绑的是同一个属性 | 只留工具栏那个 |
| 工具栏左端 `SeriTerm` 字样与标题栏"SeriTerm 串口调试助手"重复 | 同屏两处品牌文字，且是工具栏里唯一的强调色文字 | 删除 |
| `A-` / `A+` 两个按钮看不出是"日志字号"，也看不出当前字号 | 只有一个模糊的按钮文案，字号值无处可见 | 换成 `字号 [13▾]` 下拉框 |
| "发送"在左栏最底部，800 px 高的窗口要滚动才看得到 | 改前 `probe-layout`：左栏内容高度 > 视口高度，且 `打开` 按钮在最底部 | 发送区移到右栏、固定在日志下方 |
| 搜索命中数显示两份 | `SearchStatusText` 同时绑在搜索条与状态栏 | 只留搜索条里那份 |
| 预设下拉框启动时是空白的，旁边 ★ / ✕ 语义不明 | 截图：下拉框一片空白，像控件坏了；`✕` 与搜索条的关闭按钮同名同形 | 加"未选择预设"占位；★/✕ 改成文字按钮（后移到标题行，见下） |
| 三处长说明文字（接收 3 行、发送 3 行）挤在面板里 | 截图：正文被说明文字切成碎块 | 收进各自控件的 ToolTip，交互说明留在界面上 |
| 日志目录路径折成两行、右边挤着"选择/打开" | `HintText` 样式带 `TextWrapping=Wrap`，把 `TextTrimming` 顶掉了 | 目录行只显示一行省略；按钮移到"日志保存"标题行，路径整行完整显示 |

**结构上做的三件事**：

1. **顶部工具栏整行取消**。把它的内容按归属拆开：日志操作（查找/暂停/换行/滚动/字号/保存/清空）
   进新增的"日志工具条"，紧贴日志区上沿；外观开关（模糊背景、主题）进自绘标题栏，
   做成两个图标按钮（半实心圆 / 月亮·太阳，都是 `Path` 几何，不用字体符号——MDL2 字形编号记错就是豆腐块）。
   少一行 40 px 的常驻横条，日志区更高。
2. **右栏变成"日志 + 发送"**：`Grid` 三行（工具条 / 日志 / 发送区），发送区永远在。
   发送选项行用 `WrapPanel`，窄窗口自动折行而不是把按钮挤出画面（实测 920 px 宽时 `发送文件` 折到第二行）。
3. **每个表单行只放"标签 + 字段"**。段级动作（刷新端口、保存/删除预设、选择/打开目录）放该段标题行右侧。
   这一条是被数字逼出来的：预设行的下拉框原来只剩约 90 px，预设名"COM5 1000000 8N1"只能显示成 `COM5 100…`；
   把两个文字按钮（各约 59 px）移到标题行后，下拉框拿回整行宽度。

顺带把主按钮文案从"打开/关闭"改成"打开串口/关闭串口"——原来的"打开"看不出打开的是什么。

**一次自伤的假故障（记下来免得重犯）**：改完后发现"点开串口 1.8 秒后进程自己退出"，
退出码还是 0，看起来像产品的严重 bug。分模式复现（`tools/probe-exit.ps1`，`none` 存活 /
`open` 退出）才确认：**是我自己的截图脚本点错了按钮**——`Find-Button -Pattern '^(打开|关闭)$'`
在自动化树里先匹配到的是**标题栏的"关闭"按钮**（44 px 宽，排在树的前面），于是脚本把窗口关了。
换个名字（"关闭串口"）并加最小宽度过滤后恢复正常。教训：UIA 按名字找按钮必须防重名，
标题栏按钮尤其容易撞——这次改名同时把这个隐患消掉了。

**顺带暴露的测试耦合**：`ui-m7-smoke.ps1` 的第一步是等"打开"出现（当作界面就绪信号），
但同名条件同时能匹配侧栏的"打开目录"按钮，所以它**从来没有真正验证过界面就绪**——
开着"启动时自动打开串口"时，主按钮早就变成"关闭"了，是"打开目录"满足了这个等待。
改名后这一点暴露成超时，改成等 `PresetCombo`（必然存在的控件）才是真的等界面。
M4/M5/M8 的同名等待也一并改成"打开串口"。

**改后实测**（`tools/ui-layout-check.ps1`，发布产物，失败项 0）：

```
REDUNDANT|死按钮与冗余文案=0
REDUNDANT|侧栏自动滚动复选框=0|工具栏自动滚动开关=True
REDUNDANT|未搜索时的搜索状态文本=0
FONT|行数=3|13号字行高=26 → FONT|20号字行高=39
PRESET|占位提示=True → 已套用=COM5 1000000 8N1 → 套用后占位提示=False → 套用后波特率=1000000
TERMINAL|本地回显=True|退格发0x7F=True
BOUNDS|发送按钮底边=1279 窗口底边=1370 在窗口内=True
PAUSE|切换后出现继续显示=True
CLEAR|清空前=5 清空后=1
SEARCH|命中数文本出现次数=1（期望 1）
NARROW|发送文件右=606 窗口右=1390 在窗口内=True
SUMMARY|失败项=0
```

四个既有冒烟基线一字未变（M4/M5/M7/M8），单测 266 全绿。

**给后续的约定**：`Shared.xaml` 里新增 `TitleBarToggle`（标题栏开关）与 `SmallButton`（段标题行里的小按钮）
两个样式，同类按钮不要再各自写 `Padding/FontSize`；主题色一律 `DynamicResource`，
标题栏图标跟随 `Foreground`（`RelativeSource AncestorType=ButtonBase`）以便选中态变强调色。

### 11.24 日志显示设置再收进左栏（M11 收尾）

11.23 把日志操作整条搬到"日志工具条"（紧贴日志上沿），实测一轮后用户给出的意见很具体：
**查找 / 暂停显示 / 自动换行 / 自动滚动 / 字号 这几个按钮还是应该放左侧**，
右栏日志上方只该留"针对当前显示内容"的动作用。

改法（只动 `MainWindow.xaml` 与 `Shared.xaml`）：

1. 左栏新增**「日志显示」**一节，放在「接收设置」与「日志保存」之间：
   - 标题行右侧放 `查找` / `暂停显示`（沿用 11.23 定下的"段级动作进标题行"约定，用 `SmallButton`）；
   - 正文只留 `自动换行` / `自动滚动` 两个复选框（侧栏里所有布尔项都是复选框，样式才统一）
     和 `字号:` 表单行，`FontSizeCombo` 与 `AutoScrollToggle` 的 `x:Name` 原样保留，冒烟脚本不受影响。
2. 右栏那一行只剩 `保存` / `清空`，左侧补一个"接收日志"小标题让这一行不至于半空；
   日志区因此多回一行高度。
3. `Shared.xaml` 里给工具栏胶囊开关用的 `ToolbarToggle` 样式**已无引用，直接删掉**
   （`ToolbarButton` 保留，现在只服务日志行的两个按钮）。

**实测证据**（`tools/ui-layout-check.ps1` 新增"控件归属"断言，发布产物，失败项 0）：

```
REDUNDANT|死按钮与冗余文案=0
REDUNDANT|自动滚动复选框=1|自动滚动按钮=0|AutoScrollToggle=True
OWNER|查找|右边界=624 日志区左边界=787 在左栏=True
OWNER|暂停显示|右边界=726 日志区左边界=787 在左栏=True
OWNER|自动换行|右边界=726 日志区左边界=787 在左栏=True
OWNER|自动滚动|右边界=726 日志区左边界=787 在左栏=True
OWNER|字号:|右边界=435 日志区左边界=787 在左栏=True
SUMMARY|失败项=0
```

"在左栏"不是靠截图目测，而是拿这 5 个控件的 `BoundingRectangle.Right` 与 `LogViewControl.Left` 比大小——
以后谁再把这些控件挪回右栏，这条断言会立刻变红。四个既有冒烟基线（M4/M5/M7/M8）与单测 266 条同样一字未变。

**两个自伤记录（都不是产品 bug，但都会浪费一轮排查）**：

- **把"窗口刚创建"当成了最终界面**：改完第一次跑 `ui-layout-check`，`REDUNDANT|未搜索时的搜索状态文本=1`
  报了一个失败项，而它在改动前后各跑一遍都是 0。单文件包**首次运行要先解包**，窗口比平时晚几秒出现；
  脚本原来固定 `Start-Sleep -Seconds 6`，正好在窗口刚建好、绑定与首次布局还没跑完的瞬间开始断言——
  那一刻搜索条的 `Visibility` 绑定还没生效（默认 `Visible`），于是占位文本被抓进了自动化树。
  验证方式：写一次性探针连跑 3 次枚举树，搜索条都是 `Collapsed`（不存在）。
  修法是"等真实信号"而不是睡固定秒数：轮询到 `MainWindowHandle` 非零 **且** 自动化树里出现
  `LogViewControl`，再留 1.5 秒稳定期。这与 11.23 里 M7 那个"假等待"是同一类错误。
- **脚本被改成了不带 BOM 的 UTF-8**：`powershell.exe`（5.1）对无 BOM 的 `.ps1` 按 ANSI 解码，
  中文全部变成乱码，报的是 `Unexpected token`/`Missing closing '}'` 这类语法错，看着像脚本本身写坏了。
  `tools/*.ps1` 必须保持 **UTF-8 with BOM**（`settings.json` 反过来必须不带 BOM），改完脚本用
  `[System.IO.File]::ReadAllBytes` 检查前三个字节是不是 `EF BB BF`。

### 11.25 日志选中/复制、查找收藏，日志操作全部收进左栏（M12）

三条需求：日志文字要能用鼠标选中并复制；「查找」的关键字要能收藏、列表项单击 × 删除；
"保存 / 清空"也一并挪进左栏。另外明确要求：**不要跑批量 UI 自检脚本**，由使用者自测。

**1) 选中与复制：为什么是"按行"而不是"按字符"**

日志区是 `ListBox` + `VirtualizingPanel`（20 万行上限、30 fps 批量刷新、回收式容器）。
WPF 里能按字符选择的宿主（`RichTextBox`/`FlowDocument`）都不支持虚拟化，换过去等于把性能保障丢掉；
`TextBlock` 干脆没有选择能力，逐行换成只读 `TextBox` 又只能在一行内选择，跨行还是选不了。
所以这一版的做法是：

- 选中粒度 = 行：`SelectionMode="Extended"` 本身就支持"按下拖动连选多行"，缺的只是视觉反馈；
  在 `ItemContainerStyle` 的模板里加了 `IsSelected` 触发器——**行首一道强调色竖条（宽度恒定，选中与否文字都不位移）+ 中性灰底色**。
  底色用新加的 `RowSelectionBrush` 而不是 `SelectionBrush`：后者已经是"搜索命中"的颜色，两种状态叠在一行上必须看得出来。
- **`ListBox` 自己不做拖动连选（这一条是实测出来的，靠想当然会翻车）**：
  第一版只加了选中触发器，交付后使用者反馈"无法自由选中，只能右键复制当前行"。
  写 `tools/probe-log-drag.ps1` 用真实鼠标事件量了一遍：

  ```
  ITEMS|37
  CLICK|选中行数=1      ← 单击选中是好的
  DRAG|选中行数=1       ← 从第 1 行拖到第 6 行，仍然只有 1 行
  ```

  也就是说 `SelectionMode="Extended"` 只提供 Ctrl/Shift 加减选，**按住拖动连选 WPF 根本没实现**。
  于是自己实现：按下时记锚点行（按住 Ctrl/Shift 则让位给 WPF），`MouseMove` 里把锚点到光标所在行整段设为选中；
  光标移出列表上方/下方时按方向各走一行并 `ScrollIntoView`，拖到边缘也能一路选下去。
  修完同一支探针的输出：

  ```
  CLICK|选中行数=1
  DRAG|选中行数=6
  COPY|字符数=198|首行=11:03:25.636 Tx DRAG-PROBE-LINE
  ```

  第三条是 Ctrl+C：焦点在日志列表上时复制选中的 6 行（这条同时证明"终端模式让位给复制"的接线是通的）。

### 11.26 行内自由选择：TextBlock 换成只读 TextBox（M12 收尾）

11.25 交付后使用者反馈很明确：**"依然仅能按行选中，我要的是自由复制"**，并给了具体例子——
`11:08:35.899 Rx SeriTerm loopback test` 里只想要 `loopback ` 这一段。也就是说"整行连选"不够，
要的是像文本编辑器那样**按字符**选。

**为什么不换宿主**

跨行逐字符选择需要单一文本宿主（`RichTextBox`/`FlowDocument`），而 WPF 的富文本控件不支持虚拟化：
要么给日志区加一个几千行的显示上限（现在 `LogDocument.MaxLines` 是 20 万），
要么丢掉 30 fps 批量刷新。两样都是这个工具的核心，不能为了"选择粒度"交换。

**改法：行极粒度 + 行内字符粒度共存**

- 内容列由 `TextBlock` 换成**只读 `TextBox`**（`LogLineText` 样式：去掉边框/底色/最小高度/焦点框/右键菜单，
  外观与原来的文本一致，多的只是能选字符）。每行一个文本框，但列表是虚拟化的，只有可见行存在，开销与行高同量级。
- 行模板从 `StackPanel` 改成 `Grid`（时间 104 / 方向 30 / 内容 `*`）：内容列拿到星号宽度，
  文本框才会在行宽内换行或裁剪，而不是把整行撑宽。
- 一次拖动按落点自动分流，这是关键：
  - 落点在**同一行内** → 交给文本框原生选字符，整行选择不动；
  - 拖到**别的行上** → 切换成"选多行"：清掉文本框里那段字符选择、把鼠标捕获转到列表上，
    之后按 11.25 的整行连选逻辑走（含拖出列表外继续扩选）。
- 复制优先取"选中的字符"，没有字符选择时才复制整行；状态栏会说清复制了什么
  （"已复制选中的文本（9 字）到剪贴板" / "已复制 6 行日志到剪贴板"）。
- `Ctrl+C` 改在列表的 `PreviewKeyDown` 里接管：只读文本框在"没有选中内容"时对 Copy 的处理不可靠，
  自己判断"有没有字符选择"更确定。

**过程中又被 WPF 教了一次（两条都改了才通）**

- **拖动期间 `Mouse.DirectlyOver` 不可用**：鼠标被行文本框捕获后，直接命中结果永远指向捕获元素，
  于是"跨行检测"永远认为鼠标还在锚点行上 —— 表现就是行内选字符好了、整行连选反而退回 1 行。
  改成用 `VisualTreeHelper.HitTest(LogList, position)` 自己按坐标命中。
- **冒泡的 `MouseMove` 收不到**：被文本框捕获期间，冒泡事件在文本框那一层就被消化了。
  改挂 `PreviewMouseMove`（隧道路由从根往下走，列表这一层必然经过）。

**实测（`tools/probe-log-drag.ps1`，真实鼠标事件 + UIA 读回，Debug 与发布产物结果一致）**

```
CLICK|选中行数=1                                              ← 点一下选中整行
DRAG|选中行数=6                                               ← 第 1 行拖到第 6 行：整行连选
COPY|字符数=198|首行=11:13:25.062 Tx DRAG-PROBE-LINE           ← 整行复制
TEXTSEL|行内拖选得到的字符=[BE-LINE]                           ← 行内按字符选中（"DRAG-PROBE-LINE" 的尾巴）
TEXTCOPY|剪贴板=[BE-LINE]                                     ← Ctrl+C 只复制了这一段字符
```

**仍然做不到的**：跨行的"部分选择"（第 1 行后半段 + 第 2 行前半段）——跨行只有整行连选。
要跨行逐字符就必须换宿主，代价写在 11.26 开头，等使用者决定是否值得。

### 11.27 选中看不清、查找没高亮"内容"：两处都是实测定位出来的

使用者看完 11.26 的效果反馈两条：**"选中区太不明显"**、**"查找时并没有高亮搜索内容"**。
两条都不是"加点颜色"能解决的，先把现象量清楚。

**问题一：选中行的底色被命中行的底色盖住了**

先用像素取样量（`artifacts/m12-search/02-selection.png`）：被选中的那两行是**同时命中**的行，
它们的行底色是命中蓝 `#CCE4F7`，而行底色画在行内 `Border` 上、选中底色画在行容器（`ListBoxItem`）模板上——
**内层盖住外层**，于是选中只剩行首那道竖条。加上原来选中色是 `#E8E8E8`（白底日志上几乎看不见），两条合起来就是"看不出选中"。

改法：
- 行容器模板的竖条加粗到 3 px；
- 在 **`DataTemplate.Triggers` 里补一条 `IsSelected` 触发器**（绑到 `AncestorType=ListBoxItem`），并排在命中/当前命中之后——
  这样选中底色画在行内 Border 上，才压得住命中底色；
- 顺手把四种状态的颜色档次拉开：普通命中行 `#E9F2FA`（极浅蓝）、选中行 `#A9C4DD`（明显更深的蓝灰）、
  当前命中行 `#FFEFC2`（淡琥珀）、命中字符 `#FFE49A` / 当前命中字符 `#FFBE3D`（深浅琥珀）。
  原来"当前命中整行刷成强调色 + 白字"也一并去掉：那样反而看不出命中的是哪一段。

**问题二：命中高亮只画了"整行"，没画"字符"**

行级底色只能说明"这行里有"，用户要的是"命中在哪一段"。做法：

- 行内容的只读文本框外面套一层 `Grid`，底层放一个 `Canvas` 专画高亮方块，上层放文本框（底色透明），
  方块位置由 `TextBox.GetRectFromCharacterIndex(i, trailingEdge)` 逐个字符量出来、同一视觉行上的合并成一个矩形，
  所以自动换行时也跟着文字走；
- 判定与定位统一到 Core 的 `SearchMatchFinder`（`IsHit` / `FindRanges`）：`LogDocument` 的搜索判定改成调用它，
  界面用它算高亮的区间。两边各写一套迟早会出现"计数说有命中、行里没有高亮"；
- 重画时机：行生成 / 数据上下文变化（虚拟化回收复用同一容器，`Loaded` 不会再触发）/ 尺寸变化 /
  关键字或大小写变化 / 字号与换行方式变化 / 列表滚动后新生成的行。

**过程中量出来的第二个坑：当前命中挪了位置，但高亮没重画**

改完发现"当前命中那一行的行底色是琥珀，字符高亮却是淡的（等于没更新）"。像素归类统计把这事摆平了：

```
y=165  行底色=204,228,247   淡琥珀=1    强琥珀=660     ← 行底色说"不是当前命中"，字符却按当前命中画
y=194  行底色=255,239,194   淡琥珀=1021 强琥珀=19      ← 恰好相反
```

原因：**`LogDocument.SetCurrentMatch` 不发 `SearchChanged`**。行底色走的是 `DisplayLine` 的属性通知（会更新），
字符高亮走的是 `SearchChanged` → 重画（不会更新），两者于是长期不一致。
修法：`SetCurrentMatch` 增加 `notify` 参数，上下跳转时发事件；整表重扫与新行命中那里后面本来就要发一次，
传 `notify: false` 免得一次搜索发两遍（原有的"参数未变不重复触发"单测因此仍然成立）。
修完同一份取样：

```
y=165  行底色=233,242,250   淡琥珀=668  强琥珀=5
y=194  行底色=255,239,194   淡琥珀=483  强琥珀=555     ← 行底色与字符高亮一致了
```

**验证**：单测 286 全绿（新增 `SearchMatchFinder` 定位、以及"上下跳转要通知界面重画"）；
新写的 `tools/probe-log-search.ps1` 在 Debug 与发布产物上各跑一遍（开回环 → Ctrl+F → 截图 → 读回命中计数与选中行数），
浅色与深色各截一张人工过目；11.26 的 `probe-log-drag.ps1` 回归通过（整行连选 6 行、行内选 `BE-LINE` 并复制成功）。
两处修复都只用针对性探针复现/验证，没有跑批量冒烟脚本。
- 复制：`ListBox.CommandBindings` 里挂 `ApplicationCommands.Copy`，右键菜单的「复制」指向同一命令；
  拼文本时按屏幕上的样子来（关掉时间戳就不带时间），剪贴板被占用时重试 3 次并把失败原因写到状态栏，
  成功则在状态栏回一句"已复制 N 行日志到剪贴板"（不弹框）。
- 顺手补的两处细节：右键点在未选中的行上要先选中它（WPF 的 `ListBox` 默认不这么干，
  否则"复制"复制的还是上一次的选中内容）；拖动选择时自动关掉"自动滚动"
  （判据是按下后位移超过 4 px，单纯点一行不受影响），不然新数据一来就把刚选中的行冲走。
- 与终端模式的冲突：终端模式下 `Ctrl+C` 是发 0x03 给设备。改成"键盘焦点在日志列表里时让位给复制"
  （`LogView.IsLogListFocused`），因为那时用户刚点过日志区，意图显然是复制。

**2) 查找收藏**

- 列表逻辑放 Core（`SeriTerm.Core/Search/SearchFavoriteList.cs`，纯函数 + 9 条单测）：
  空白输入不产生变化、忽略大小写去重、`Sanitize` 清掉手改配置留下的空白项与重复项。
  ViewModel 只负责把结果灌回 `ObservableCollection` 并把 `settings.SearchFavorites` 一起更新（退出时统一落盘）。
- 入口：搜索条上的「收藏」（当前关键字已收藏或为空时置灰），左栏「日志显示」里的标签列表——
  点标签 = 打开搜索条 + 填入关键字 + 跳到第一处命中，标签右侧 `×` 只删这一条。
  标签的删除按钮用自己的 `Command`：`Button.Click` 虽然会冒泡到外层标签，但外层按钮的命令只在它自己的 `OnClick` 里执行，
  所以点 × 不会连带套用一次关键字。
- 加这个按钮的同时把搜索条从 `StackPanel` 换成 `WrapPanel`：窄窗口下按钮折行，而不是被右侧裁掉。

**3) 保存 / 清空挪进左栏**

右栏那一行工具条（"接收日志" + 保存 + 清空）整行删掉，两颗按钮进左栏「日志显示」，
与字号表单同一列、等宽并排；右栏因此只剩"日志 + 发送"，日志区又多回一行高度。

**代价与边界（写下来免得下轮重新讨论）**：左栏内容又长了一截，1280×800 下「日志显示」的下半部分
（保存/清空、查找收藏标签）要滚动才看得到。可选的压缩手段是"数据位/校验位、停止位/流控 两两并排"（约省 100 px），
或者把「日志显示」提到「接收设置」之前；两者都还没做，等使用者定。

**验证**：单测 275 全绿（原 266 + 收藏逻辑 9 条）；改动后启动应用确实能起、能正常退出（退出码 0、无 `ui-errors.log`）；
按使用者要求**没有**跑 `ui-layout-check` / 冒烟脚本，界面部分由其自测。
拖动连选这一处例外：使用者反馈"选不中"之后，用新写的 `tools/probe-log-drag.ps1`（真实鼠标事件 + UIA 读选中数）
定位到是"WPF 不实现拖动连选"，修完再用同一支探针确认 1 → 6 行 —— 这是针对单个症状的复现/验证，不是批量回归。
`tools/ui-layout-check.ps1` 的"控件归属"断言已把 `保存/清空/查找收藏:` 一并纳入左栏判据，
下次谁再把这些控件挪回右栏，或谁手工跑一次校验，都会立刻发现。








