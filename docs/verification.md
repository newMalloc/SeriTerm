# 验证记录（实测证据）

本文件保存 SeriTerm 各里程碑落地后的**实测证据**。README 只保留结论与用法，原始数字、坐标、字节数放在这里，
目的是让"改之前 → 改之后"的差异可以被复核，而不是只有一句"已修复"。

## 验证环境

下列出现的坐标、像素、字节数都出自这套开发环境。换机器数值会变，但**相对差异**（开关前后差多少）是可复现的。

| 项 | 值 |
|---|---|
| 系统 | Windows 10（build 19045） |
| 显示 | 单显示器，系统 DPI 125%，工作区 2560×1540 |
| 回环硬件 | USB-TTL，端口 TX 与 RX 短接，脚本默认使用 `COM5`（可用环境变量 `SERITERM_LOOPBACK_PORT` 覆盖） |
| UI 自动化 | Windows PowerShell 5.1 + UIAutomationClient（`tools/` 下脚本） |

UI 自动化脚本一律使用**真实鼠标拖动 / 真实虚拟键注入 + UIA 读回断言**，不做截图比对；因此下面的数字是
控件树里读回来的真实几何与文本，而不是"看起来像对了"。

## 自动化测试

`dotnet test SeriTerm.sln`：**360 通过 / 0 失败**（352 个纯逻辑单测 + 8 个回环集成测试）。

单测覆盖：断帧边界（空闲 / 分隔符 / 跨块 / 空帧 / 上限）、HEX 与字节模式解析、ANSI 过滤、终端按键编码、
GB2312 / UTF-8 跨块解码、显示行存储与淘汰、搜索与淘汰联动、发送组装、定时发送、文件分块发送、
日志落盘与重放读取、重连退避与重连流程、故障归类（拔线 vs 端口被占用）、配置预设增删改，
以及 AI 接入（MCP）的帧缓冲、限速器、关键词匹配、派发层护栏、stdio 协议与管道客户端（52 个，详见下文）。

没有串口的机器（例如 CI runner）上，8 个回环测试整组 **skipped**，结果仍为绿。CI 日志实测
（`8f3074e` 那一跑，当时总数 297）：

```
Passed!  - Failed:     0, Passed:   291, Skipped:     6, Total:   297, Duration: 3 s - SeriTerm.Tests.dll (net8.0)
```

⚠️ 上一条 CI（`3ca6335`）是**红的**，抓到一个真实的竞态：`ReconnectSupervisor.BeginReconnect` 里
`Task.Run` 的 lambda 捕获的是字段 `_cts`，而 `StopAsync` 会先 `_cts = null` 再 await 这个任务，
lambda 若在置空之后才开始跑就是空引用（`ReconnectSupervisorTests.手动停止后不应继续重连` 报
`NullReferenceException`，由 `StopAsync` 的 `await loop` 冒出来）。这个窗口极小，本地跑几百次都不出现，
修掉之后补了「故障后立刻停止不应把异常抛给调用方」这条回归测试（发完故障不等 `IsReconnecting` 就直接
`StopAsync`，重复 200 次打这个时序窗口）。

## 端到端回环（UI 自动化真实点击，含发布产物本身）

- 打开串口 → 发送 → Rx/Tx 计数一致，日志按帧一行显示；
- 自动滚动：初始 On → 向上滚动后自动 **Off** → 拉回底部自动 **On**，并出现"▸ N 条新数据"提示条；
- 切"十六进制显示"后**已有行全部重刷为 HEX**（系统提示行保留）；Ctrl+F 命中计数与跳转正确；
- **分隔符断帧**：发送 `41 0D 0A 42 0D 0A` 后 Rx 显示为独立的 `A`、`B` 两行（若断帧失效会合成一行 `A B`）；
  发布版实测 11 轮全部正确；
- **定时发送**：0.2 秒间隔连续发送，每轮产生 1 条 Tx + 2 条 Rx，计数与字节数吻合；
- **日志落盘**：日志文件按预期生成（文本 404 B / 原始 108 B = 4 条记录 × 27 B），文本日志含时间戳、方向与真实换行，中文不乱码；
- **启动时自动打开串口**：启动即为"已打开"状态；
- **终端模式**：在日志区直接敲 `AT` + 回车 → Rx/Tx 各 4 字节（`AT` + CRLF 双向往返），Tx 侧按整行回显一条；
- **配置预设**：从下拉框选中预设后，波特率变为 921600、十六进制显示自动勾选；
- 深色 / 浅色两套主题跑同一套自动化验证。

## 真机拔插与自动重连

`tools/ui-reconnect-smoke.ps1`（人工拔掉 USB-TTL，由脚本判定现象）：

- 拔线后提示为「**COM5 连接已中断：设备已被拔出或被禁用**」，而不是误报"被其它程序抢占"；
- 自动重连开启时**不弹模态框**；
- 设备插回后 **1 秒左右**自动重连成功；
- 全过程界面文本：`链路中断…` → `已开启自动重连…` → `已重连 COM5（第 1 次尝试）。`

## 自绘标题栏与窗口背景

原来的系统标题栏在 Windows 10 上永远是一块白条（详见开发规格 11.19），改为自绘：

- 运行中切主题，顶部标题栏与客户区**同时**变色（旧版实测只有客户区变，标题栏停在 `#FFFFFF`）；
- `WM_NCHITTEST` 实测：标题栏空白处 = `HTCAPTION`（可拖动），三个窗口按钮 = `HTCLIENT`（可点击），
  四边 = `HTTOPLEFT` / `HTTOP` 等缩放码；
- 合成鼠标实测：拖动标题栏窗口按位移精确跟随（-100 / +60 像素），双击标题栏最大化、再双击还原；
- 最大化后窗口矩形与显示器**工作区完全一致**（`0,0,2560,1540`），不盖任务栏；
- 任务栏按钮保留（`WS_EX_APPWINDOW`）。

窗口背景模糊（默认开，标题栏的半实心圆图标可关）：背景是壁纸降采样 + 三次盒式模糊后的图，
按屏幕坐标对齐窗口（窗口移动时背景跟着桌面走），深 / 浅主题各有一层蒙版保证文字对比度；
发布版回环日志截图确认时间戳 / Tx / Rx 三色文字清晰可读。

## 退出路径

点关闭后 **1 秒内退出、退出码 0、配置已落盘**。修复前每次关窗都会弹「SeriTerm 发生严重错误」并且进程退不掉
（成因见开发规格 11.22）。

## 主界面信息架构（M11）

`tools/ui-layout-check.ps1`，全部为 UIA 实测断言，失败项 0：

- **假按钮与重复项归零**：`⚙` / `?` / `A-` / `A+` / `SeriTerm` 文案的按钮计数 = 0；
  "自动滚动"复选框 = 1 且不再有第二个开关（原来工具栏一个、侧栏一个）；未搜索时"输入关键字开始查找"文本 = 0 处
  （原来搜索条和状态栏各显示一份）。26 个关键控件全部存在。
- **日志上方的按钮只剩"保存 / 清空"**：查找、暂停显示、自动换行、自动滚动、字号全部搬进左栏「日志显示」一节，
  UIA 实测这 5 个控件的右边界（624 / 726 / 726 / 726 / 435 px）都在日志区左边界（787 px）左侧。
  （M12 起"保存 / 清空"也一并移入左栏，右栏日志上方不再有工具条那一行。）
- **"发送"不用再滚动去找**：发送区固定在日志下方（原来在最左栏底部，800 px 高的窗口里要滚动才看得到）。
- **字号下拉框真的生效**：13 号 → 20 号，日志行高由 26 px 变成 39 px（UIA 实测行矩形）。
- **预设**：未选中时显示"未选择预设"占位；选中 `COM5 1000000 8N1` 后占位消失、波特率框读回 `1000000`。
- **终端模式**子选项（本地回显 / 退格发 0x7F）勾选后出现在发送区，发送按钮仍在窗口内。
- **暂停 / 继续、清空**实测有效（清空后日志行 5 → 1）。
- **最小窗口 920×800**：发送区选项行自动折行，`发送文件` 右边缘 606 px < 窗口右边缘 1390 px，不裁切。
- 主按钮文案由"打开 / 关闭"改为"**打开串口 / 关闭串口**"（原来只写"打开"，看不出打开的是什么）。
- 四个既有冒烟基线一字未变：M4 `独立Rx行A=11 独立Rx行B=11 合并行A_B=11`、
  M5 `Tx整行AT=1 Rx字符A=1 Rx字符T=1 4B计数元素=2`、M7 `原始日志字节数=108`、M8 `波特率=921600 十六进制显示=On`。

## 日志选中复制与查找收藏（M12）

- 日志内容**可以像文本一样自由选择**：鼠标拖过任意一段字符即可选中
  （例：一行 `11:08:35.899 Rx SeriTerm loopback test` 里只选中 `loopback ` 再 `Ctrl+C`，剪贴板就只有这几个字）。
  **跨行也是按字符**：第 1 行的后半段 + 中间整行 + 最后一行的前半段能一次选中（见 11.30），
  拖动不再把整行连选（"拖两行却选中两整行"是使用者报的问题）。
  （2026-10-08 性能修订后**整行都是 `TextBlock`**、一个 `TextBox` 都没有，见下文「日志区吞吐与输入延迟」。）
- 选中的字符画在行内一层 `Canvas` 上（垫在各列文字之下），方块位置按字符量出来：
  `TextPointer.GetCharacterRect`（文本框的 `GetRectFromCharacterIndex` 内部用的就是这套）
  从"第 i 个字符之后"往回看拿字形盒，宽度取"下一个字符的左边缘 − 本字符左边缘"，
  所以跨列、跨行、**自动换行**都对得准（探针实测：折行后一段 300 字的底色画成 5 块、分布在 4 个视觉行上）；
  方块只落在选择覆盖的那几行上（其余行的画布是空的）。
- **底色铺满整行**（行高 = 行容器高 = 底色层高，探针实测都是 17.2 px）：不换行的行纵向按整行铺，
  换行的行按视觉行铺且行内相邻两块严丝合缝（缝隙 0 处）。行与行之间不漏背景色横线
  ——2026-10-08 修的就是这个（改前每行被 `Border` 的上下 1px 内边距裁掉，实机截图逐像素量到缝在
  y=135/136、152、169/170、186/187、203/204）。
- 点一下整行仍是"选中整行"（行首出现强调色竖条 + **明显更深的蓝灰底色**），
  `Ctrl+C` 或右键「复制」把选中行按屏幕上的样子写进剪贴板（时间戳列关掉时不带时间）。
  复制优先级：**自由选择 → 选中的整行**（行内原生选择那条路径随文本框一起删了），状态栏会说清复制了什么
  （"已复制选中的文本（40 字 / 2 行）到剪贴板" / "已复制 6 行日志到剪贴板"）。
- **双击选词**由 `LogTextSelection.WordRange` 补回（原来靠文本框原生能力）：字母/数字/下划线算一个词，
  非单词字符选中它自己——与文本框的原生行为一致（时间戳里双击 `17:46:55.205` 只选 `17`，探针有 6 条单测钉住）。
- **查找会高亮命中的那一段字符**（不只是整行底色）：命中的字符画在行文本背后，
  淡琥珀 = 普通命中、强琥珀 = 当前命中（"第 N / 共 M 条"指向的那一条）。
  判定与定位来自 Core 的 `SearchMatchFinder`，与搜索计数用的是同一份规则，不会出现
  "计数说有命中、行里却没高亮"这种自相矛盾的界面。
  **关掉搜索条（✕ 或 Esc）时高亮会一起消失**——`tools/probe-log-search.ps1` 会关一遍搜索再数一次
  "还有多少行留着高亮横条"，期望 0（实测 142 → 0）。
- ⚠️ WPF 的 `ListBox` **自己不支持"按住拖动连选"**（实测：从第 1 行拖到第 6 行，选中数仍是 1），
  所以自由选择是自己实现的（含拖到列表外时继续扩选并滚进视野）；行内拖动也是同一套自绘逻辑
  （2026-10-08 之前是内容列文本框的原生能力，随文本框一起删了）。跨行部分选择由 C# 探针验证
  （真实窗口 + 真实鼠标事件 `SetCursorPos`/`mouse_event`：拖 10 步 → 读回 `SelectedText`
  与每行选择方块的几何与数量），`tools/probe-log-drag.ps1` 是等价的 UIA 版本（需 COM5 空闲）。
- 拖动选择会自动停止"自动滚动"（否则新数据一到就把选中的行冲走；单纯点一行不会关）。
- **查找收藏**：搜索条上「收藏」把当前关键字存进日志右上角的「查找收藏」浮层，行点一下即填回搜索框并跳到第一处命中，
  行尾 `×` 只删这一条；收藏随 `settings.json` 持久化，忽略大小写去重，启动时会清掉手改配置留下的空白 / 重复项。
- 接收日志的**保存 / 清空**也移进左栏「日志显示」，右栏日志上方那一行工具条整行取消，日志区再多一行高度。
- 本轮没有跑 UIA 批量自检脚本（按要求留给使用者自测）：新增逻辑以单元测试覆盖，单测 290 全绿
  （本次新增 `LogTextSelection` 跨行取字 8 条 + `DisplayLine.ToDisplayText` 2 条；另有收藏列表 9 条、查找定位 12 条等）。
  6 条回环集成测试因使用者的实例正占着 COM5 而未能运行（`dotnet test --filter` 排除后 290/290 通过）。

## 日志区吞吐与输入延迟（2026-10-08 性能修订）

**症状**：使用者的原话是"接收日志量超过几十 KB，几千行后，日志列表明显卡顿"；
第二轮补充："只有几千行时拖进度条也卡，**关闭串口后拖动依然卡顿**"。

**结论**：卡的根源不是**累计行数**（虚拟化本身是好的：工装实测 16 万行时只生成 33~44 个行容器，
与总量无关），而是**每行的界面开销 × 每步回收重绑的行数**。跟随最新数据或拖动滚动条时，
一屏（约 34 行）容器全部回收 / 重绑 / 重排 / 重绘，而每个行容器里有 3 个只读 `TextBox`——
那是 WPF 里最重的控件之一（每个都自带 `ScrollViewer` + `TextBoxView` + `TextEditor`，
只读也一样，还各挂一套 TSF 文本服务）。速率一上来界面线程被占满，
而刷新用的是 `DataBind` 优先级（8），高于输入（5），于是点击与滚动排在后面——体感就是"卡"，
并且**与日志总量、与串口开没开都无关**。

**真机采样**（使用者本人拖滚动条，`dotnet-counters collect --format csv` +
`dotnet-trace --providers Microsoft-DotNetCore-SampleProfiler`，跑在改前那一版上）：

- 分配速率 **28~70 MB/s**（空闲时 0~2 MB/s）、每秒 **16~42 ms 停在 GC**、gen0 4~9 次/秒、gen1 2~6 次/秒；
- 进程 CPU 峰值 **6.36%（约一个核跑满）**；
- 热点栈：`TextStore.OnTextContainerChange`、`TextServicesDisplayAttributePropertyRanges.OnEndEdit`（TSF 文本服务）、
  `AutomationInteropProvider.RaiseStructureChangedEvent`、`AutomationPeer.RaisePropertyChanged*`（UIA 事件）、
  `CriticalHandle.Finalize`、`PenThreadWorker`——全是 `TextBox` 那条路径。

于是"每滚动一步这一屏约 34 行全部回收重绑"，每行约 0.3 ms CPU + 30~45 KB 垃圾，
一步 20~40 ms，拖动条自然跟不上手。

**量法**（工装在 `artifacts/logbench/perf/`；`artifacts/` 被 `.gitignore` 忽略，不进提交）：
同构复刻日志行模板的 WPF 工程，按 10 行 / 5 ms（2000 行/秒）注入，用一个 **Input 优先级**的探针
量"输入要等多久才被伺候"，每档跑 3 秒：

| 行模板 | 实际吃下 | 输入延迟 P95 | 进程 CPU（单核） |
|---|---|---|---|
| 改前：3 只读 TextBox + 2 层 Canvas | 434~530 行/秒 | 146~157 ms | 87~97% |
| 去掉两层 Canvas（仍 3 个 TextBox） | 527 行/秒 | 93 ms | 68% |
| 中间态：2 TextBlock + 1 只读 TextBox | 618~626 行/秒 | 30 ms | 10~20% |
| **本次改法**：整行 3 个 TextBlock + 2 层 Canvas | **638 行/秒（注入上限）** | **29 ms** | **5~6%** |
| 合并成一个普通 TextBox（未采用） | 637 行/秒（注入上限） | 30 ms | 4~5% |

"注入上限"= 工装自己的 `DispatcherTimer` 分辨率（~15.6 ms）把速率顶在 638 行/秒，不是该档的天花板。
另一轮"每批 10 行 + 强制渲染"的测量里排序一致：改前 47.4 ms/批、2 TextBlock + 1 TextBox 21.2 ms/批、
整行 TextBlock 2.3 ms/批、单个大文本框 0.9 ms/批。每滚动 10 行的分配量：
改前 339 KB、中间态 424 KB、本次 **254 KB**。

**为什么没选"合并成一个普通文本框"**：它确实够快，但（1）是 O(文本总量) 的——每行成本随已显示文本增长
（实测 107 KB → 95 µs/行、1.7 MB → 166 µs/行、5.4 MB → 146 µs/行，且有 23 ms 级偶发停顿），必须封顶；
（2）会丢掉 Tx/Rx/系统按行着色（普通 `TextBox` 只能一个颜色，要着色得上 `RichTextBox`/`FlowDocument`，
那既不虚拟化也更慢）、丢掉逐行命中高亮、丢掉 20 万行的显示上限。

**改动**

- `LogView.xaml`：三列全部 → `TextBlock`（新样式 `LogColumnLabel` / `LogLineText`）。
  `LogLineText` 刻意 `HorizontalAlignment=Left`——只按内容宽度占位，字符矩形是相对控件量的，控件越窄越省。
- `LogView.xaml.cs`：字符坐标不再依赖文本框。`PartCharRect` 的 TextBlock 分支用
  `TextPointer.GetCharacterRect`（"第 i 个字符之后"往回看）+ "下一个字符的左边缘" 算宽度
  （字形盒宽度不等于步进，末字符还常带行尾富余）；`ToCaret` / `DrawPartSelection` 按字形盒的
  `Top`/`Bottom` 分带，所以自动换行时逐字命中与选中底色都落在正确的视觉行上。
- `LogView.xaml` 行模板：选择底色层从 `Padding="6,1"` 的 `Border` **里**挪到行根 `Grid`
  （在 Border 里会被内边距裁掉上下各 1px，连续选中时行间露出 2px 背景色）；不换行的行纵向铺满整行
  （`IsSingleVisualLine` 用"最高的一段 vs 一个字符盒"的量值判定，不猜字体行距）。
- `LogView.xaml.cs`：删掉全部原生选择路径（`_anchorTextBox` → `_anchorText`、`_textSelectionBox`、
  `OnLineTextSelectionChanged`、复制优先级里的中间那档）；双击选词改由 `LogTextSelection.WordRange` 补回。
- `MainViewModel.cs`：单次界面刷新最多搬 600 行（`MaxLinesPerFlush`），`FlushPendingLines` 改用
  `GetRange` + `RemoveRange`（不再整条复制一遍积压）。原来一次把整条积压搬完，积压越大这一个回调越久，
  而回调期间新数据还在到——会自我放大；切成定长块后单个回调的耗时有了上限，剩下的行不会丢
  （30 Hz 的界面定时器与后续数据到达都会继续触发排空）。
- `Themes/Shared.xaml`：右键菜单整套自绘（见下节）。

**验证**

- 一次性 C# 探针（`artifacts/logbench/probe/`：真的 `SeriTerm.App` + 真的 `LogView` + 真的行模板，
  窗口开在屏幕外，不碰串口、不碰使用者的 `settings.json`）：
  - `checks` **22 项断言全过**——行里是 3 个 `TextBlock` 且**一个 `TextBox` 都没有**、三列都没被拉伸；
    字符步进与 `FormattedText` 的独立测量一致（**7.147 px**）；跨行按字符提取出的文本与逐行拼出来的一致；
    时间列第 3 个字符起那块方块的左边缘 / 宽度（9 个字符）对得上、**高度 = 整行高（17.2 px）**、
    每块都从行顶起铺；**底色层盖满行容器（层高 = 行高 = 行容器高 = 17.2 px）**；方向列整列 2 个字符；
    中间整行只铺一块且铺满整行；清掉选择后已生成的 35 行底色全空；
    折行后一段 300 字的底色画成 5 块、分布在 4 个视觉行上，**行内相邻两块缝隙 0 处**。
  - `drag` **4 项全过**（真鼠标 `SetCursorPos` + `mouse_event`，从第 2 行时间列拖到第 5 行内容列）：
    拖出 4 行、首段是按下行的真后缀（截断点落在时间戳里，不是内容列开头）、末段是松开行的前缀、
    起始行分段画而中间整行只铺一块。
- `dotnet test`：**366 个用例 / 0 失败**（358 通过 + 8 个回环用例 skipped——COM5 被使用者的实例占着）。
  本次新增 6 条 `LogTextSelection.WordRange` 单测（双击选词：字母/数字/下划线的词、非单词字符选它自己、
  时间戳与中文各一条）。
- ⚠️ 本轮**没验到**的三件事，留给使用者实测：
  1. **真实串口链路下的吞吐**（工装灌的是合成行，不是真收）；
  2. **长时间高负载稳定性**（工装每档只跑 3 秒）；
  3. 这一版（整行 `TextBlock`）**没有重新采一遍拖动时的 CPU / 分配曲线**——上面那组
     28~70 MB/s、16~42 ms/s GC 是**改前**那一版在真机上拖出来的；改后的真机手感由使用者实测。

## 右键菜单配色（2026-10-08）

- 现象：日志区右键菜单里「复制」那一项**看不清**（使用者原话："右键 复制文字描述完全看不清"）。
  截图逐像素取色：鼠标指向的那一项底色 **`#C3E0EE`（系统浅蓝）**、文字 **`#E8E8E8`**，
  对比度 **1.1:1**——等于白字写在浅蓝上。
- 根因：WPF 默认（Aero2）`MenuItem` 模板只在"鼠标指向（`IsHighlighted`）"这一档上换成系统浅蓝底，
  文字取 `SystemColors.HighlightTextBrushKey`；而本主题为了让日志选中的白字更清楚，
  把这个键覆盖成了 `#FFFFFF`（`Themes/Dark.xaml`）。**样式触发器压不过控件模板里的触发器**，
  所以改颜色没用，只能整套自绘。
- 改法：`Themes/Shared.xaml` 新增隐式 `ContextMenu` / `MenuItem` 样式（`ControlTemplate` 自绘），
  底色 `PanelBackgroundBrush`、边框 `PanelBorderBrush`、文字 `ForegroundBrush`、
  悬停 `ControlHoverBrush`、禁用 50% 透明，不再引用任何系统色；顺带收掉没有快捷键的项右侧那 16px 空白
  （系统给文本框自动生成的右键菜单不填 `InputGestureText`）。模板里没有子菜单用的 `Popup`
  ——全应用只有日志区那一个单层菜单。
- 验证（探针 `menu` / `menu-light` 两档，真鼠标悬停到菜单项上再渲染整张菜单、量对比度）：

  | | 悬停底色 | 文字 | 对比度 |
  |---|---|---|---|
  | 改前（临时关掉新样式复现） | `#C3E0EE` | `#E8E8E8` | **1.1:1** |
  | 改后 · 深色主题 | `#3A3A3D` | `#E8E8E8` | **9.3:1** |
  | 改后 · 浅色主题 | `#EDEDED` | `#1F1F1F` | **14.1:1** |

  断言同时钉住"悬停底色 = 主题悬停色"和"承载 Header 的那个 `TextBlock` 的前景色 = 主题前景色"
  （默认模板是在控件模板触发器里改后者，`MenuItem.Foreground` 上看不出来），
  并确认把这套样式临时关掉时这几条断言确实会失败——探针抓得住这个缺陷。

## 查找框与收藏栏叠在日志右上角（M13）

`tools/probe-search-overlay.ps1` 实测数字：

- **日志区不再为搜索条让出一行**：打开查找前后，日志列表顶边都是 **223 px，位移 0**
  （改之前搜索条是日志上方的一行，一打开就把日志压下去约 40 px）。
- **浮层确实贴右上角**：浮层右边界距日志区右边 **48 px**、顶边界距上边 **24 px**；查找框左边缘 1719 px，
  而日志区从 787 px 开始——它落在右半边、压在最上面几行日志上，而不是把日志推下去。
- **收藏栏是每项一行的垂直列表**：两条收藏的行矩形 左 1657 / 宽 537，顶边 404 与 443（相差 39 = 一行行高），
  左右边界对齐、上下叠放，**两行行高之和 72 px**（不是横排标签）。
- **关掉搜索后收藏栏原地常驻**：搜索框消失、两条收藏行都还在（距顶 57 px / 距右 44 px），
  点一下 `loopba` 那一行，搜索框回填 `loopba` 并跳到第一处命中——收藏是"随时点一下就填回去"的入口。
- **逐条删除**：删掉一条后只剩另一条；两条都删完浮层里的收藏块消失（搜索还开着，所以查找框仍在）。
- ⚠️ 顺带修掉一个只有自动化 / 读屏软件才会碰到的问题：原来收藏列表每次变动都是 `Clear()` + 逐个 `Add()`，
  集合发出 **Reset** 后 `ItemsControl` 会把行容器全部丢掉重建，而 UIA 那一棵树不会跟着重建——
  实测加进第二条收藏之后，**第一条在 UIA 树的 Button 节点就没了名字**（只剩一个空的 DataItem），
  脚本和读屏软件都找不到它。改成增量同步（增 / 删 / 移动，见开发规格 11.29）后两条都在树里。

## 应用图标（M11 起用，M14 换成设计稿）

图标换成 Photoshop 导出的设计稿（`src/SeriTerm.App/Assets/seriterm.png`，1571×1571，带 alpha 通道，
SHA256 `536922B7…`），由 `tools/make-icon.ps1 -CropMode Subject` 生成 `seriterm.ico`（97,241 字节，7 帧）。
实测：

- **取景判定的稳健性**：整幅内容包围盒 `{30,462,1527×617}`；按"每列/每行不透明像素数 ≥0.2 倍图高"选出的主体
  包围盒 `{386,463,911×616}`。门槛从 0.13 到 0.20 倍图高，结果逐像素一致——波形与箭头整列最多十几像素，
  外壳每列 380+ 像素，两者相差一个量级，判定不吃门槛。
- **边缘没有毛边**：256px 帧里 715 个半透明边缘像素平均 RGB `(27,41,74)`（描边深蓝），
  近白像素 0.00%、近黑像素 0.00%——既没有透明区域的白色 RGB 渗出来，也没有预乘没还原的发黑。
  16px 帧内容 bbox `(0,3)-(14,12)`，不透明占比 56.2%。
- **两条图标通道都验过**（Win32 资源 vs WPF pack URI 资源是两条路）：
  `bin\Release\net8.0-windows\SeriTerm.exe`、`artifacts\publish\SeriTerm.exe`、`artifacts\publish-fd\SeriTerm.exe`
  抽出的 32px 图标与新 ICO 的 32px 帧**最大像素差均为 0**；换图标前的发布产物则是与旧 ICO 差 0。
  WPF 侧用 `iconcheck` 探针加载 `pack://application:,,,/SeriTerm;component/Assets/seriterm.ico`：
  解码器报 7 帧，按 `MainWindow.xaml` 的 `Image Width=16 Height=16` 渲染出的 16×16 有
  136 不透明 / 8 半透明 / 112 全透明像素，与直接渲染 16px 帧差 0（与 32px、256px 帧差 240）。
- 图标随 exe 走：已缓存的资源管理器图标与正在运行的旧实例不会自己更新，需重启程序 / 刷新图标缓存。

## 「关于」入口与窗口（版本 / 提交 / GitHub）

标题栏右上角加了一颗「关于」按钮（切换主题按钮右边、窗口按钮左边，标 <kbd>F1</kbd> 也能开），
点开是 470×317 DIP 的模态对话框：版本号、提交号、运行时、许可，以及 GitHub 仓库 / 发布版下载两个入口。
实测（探针 `aboutprobe`：真实 MainWindow + 真实 AboutWindow，150% 缩放，窗口 1507×926 DIP）：

- **位置**：标题栏一行自左向右为`模糊背景 x=1234.7`、`切换到浅色 x=1270.7 w=44`、`关于 x=1314.7 w=44`、
  竖分隔线、`最小化 x=1374`、`最大化 x=1418`、`关闭 x=1462`——「关于」紧挨主题按钮右侧、在窗口按钮左侧，宽 44 高 32。
- **是模态**：点击到对话框关闭约 1.4 s（多次实测 1323~1702 ms，大部分是探针自己的等待），
  期间探针在嵌套消息循环里完成了全部检查；关闭后 `Application.Current.Windows` 里没有残留窗口。
- **版本号不是界面自己编的**：对话框显示的版本 / 短提交号与程序集信息版本（`<版本>+<完整提交号>`）
  逐段一致——探针用反射独立读出来比对，所以每次构建都会重新验一遍，这里不写死具体号
  （写本文档时是 `1.0.1+3ca6335…`）。
- **图标用的是专门的帧**：显示帧 `128×128`，当前 DPI 下需要 96 物理像素；默认帧是 16×16（会糊）。
- **按钮只剩两个**：对话框正文里的按钮清单实测为 `GitHub 仓库 / 发布版下载`，
  没有「关闭」「复制版本信息」「使用说明」「开发文档」；关闭靠标题栏的 ✕（`AutomationProperties.Name="关闭"` 仍在）
  或 Esc——真键盘 Esc 在两轮里都关掉了窗口（发键前断言过窗口在前台，`IsActive=True`）。
- **表面是不透明的**（修复"灰蒙蒙"之后）：开着模糊背景时 `Application.Resources` 里的
  `WindowBackgroundBrush` 确实被覆盖成 alpha=168（探针先断言这条，避免"没复现出问题"被当成"已修"），
  而「关于」窗口渲染出来仍是 浅色 `#F4F4F4` alpha=255 / 深色 `#1E1E1E` alpha=255。
  对比度：浅色正文 14.99:1、次要文字 4.85:1；深色正文 13.61:1、次要文字 6.00:1
  （修复前浅色次要文字落在 `#A1A1A1` 上只有 2.06:1，低于 WCAG AA 正文要求的 4.5:1）。
- **单文件绿色版同样通过**：把探针按同样参数发布成 72,442,529 字节的单文件再跑一遍，`RESULT|通过`，
  图标帧仍为 128、Esc 关窗、表面不透明——`BitmapFrame.Decoder` 在单文件包里可用。
- **深浅两套主题各截一张**（标题栏与对话框），配色跟着主题字典走；提交号那一行在信息版本没有 `+` 段时整行收起。
- 探针只读使用者的配置（`ProbeSettingsStore.Save` 拒绝写入）：运行期间
  `%AppData%\SeriTerm\settings.json` 的 `LastWriteTime` 保持不变。

## 发布产物命名（文件名带版本号）

`tools/publish.ps1` 现在把产物改名成 `SeriTerm-<版本>-<RID>[-fd].exe`，版本读自产物自身的版本资源
（属性页里的"产品版本"，形如 `1.0.0+<提交号>`，取 `+` 之前那段），所以文件名与 exe 里显示的版本必然一致。实测：

- 自包含：`artifacts/publish/SeriTerm-1.0.1-win-x64.exe` 67,125,094 字节（64.0 MB）；
  框架依赖：`artifacts/publish-fd/SeriTerm-1.0.1-win-x64-fd.exe` 1,693,029 字节（1.6 MB）。
- 改名之后 exe 自身没变：`ProductVersion = 1.0.1+<提交号>`、`ProductName = SeriTerm 串口调试助手`、
  32×32 图标仍能抽出。
- 改名不会把程序弄坏：把探针按同样参数发成单文件（72,442,529 字节）、改名成
  `aboutprobe-9.9.9-win-x64.exe` 后运行，84 行输出、`RESULT|通过`。
- `release.yml` 的标签一致性检查（`v*` 去掉 `v` 必须等于产物版本，否则失败并提示先改 `<Version>`）
  把判断原样在本地跑过正/反两个用例：`v1.0.0` 通过、`v1.0.1` 按预期失败。
- ⚠️ 未实测：`release.yml` 只能在 GitHub Actions 上跑；框架依赖版的重命名没有实际运行过
  （真程序启动会去开 COM5、关窗时写使用者的 `settings.json`），它与自包含版是同一条 apphost + 单文件机制。

## 发布说明（Release 正文 / tag 说明）

Release 说明正文由 `release.yml` 从 `CHANGELOG.md` 里切出对应版本的那一节，再补上文件名、字节数、
SHA256、系统要求与文档链接。切分逻辑用同一段 PowerShell 在本地验过：

- `GITHUB_REF_NAME=v1.0.2` → 切出 `CHANGELOG.md` 第 9 行起的那一节（从 `## v1.0.2` 到下一个 `## v` 之前，
  含"修复 / 测试"两段，末尾对齐到"自动化测试 296 → 297"）；
- `GITHUB_REF_NAME=v9.9.9` → 落进"CHANGELOG.md 里没有这一节"的失败分支（工作流会在这里 throw，不发布）。

已发布的 Release（v1.0.0 / v1.0.1）正文按同一格式补齐；**tag 对象本身不可改**——已推送的 tag 只有
强推才能改写，所以那两个标签的说明保持原样，新标签起按 `CHANGELOG.md` 那节写。

## 小体积启动器（NativeAOT + 自动装运行时）

`src/SeriTerm.Launcher` 用 NativeAOT 编成**原生 exe**（自己不含 .NET 运行时），内嵌 Brotli 压缩的框架依赖
程序体（1,698,661 字节 → 625,533 字节）。以下数字均为本机实测（Windows 10 19045、VS 2022 自带的 MSVC、
本机唯一安装的 SDK 是 9.0.304；CI 用 8.0.x 构建，两者产物哈希不同但流程一致）。

**体积拆解**

| 对象 | 字节 | 大小 |
|---|---|---|
| 启动器本体（不内置程序体） | 3,311,104 | 3.16 MB |
| 最终产物 `SeriTerm-1.0.4-win-x64.exe` | 3,936,768 | 3.75 MB |
| 自包含完整版 `SeriTerm-1.0.4-win-x64-full.exe` | 67,127,223 | 64.0 MB |

相差 **17.1 倍**。换掉托管 HTTPS 之前本体是 6,170,624 字节（5.88 MB），用同一份源码做两个对照得到归因：

- 把下载那段整个换成桩（无 HttpClient/TLS）：**2,755,072 字节** → 托管 HTTPS 占 **3.25 MB**；
- 把 Brotli 解包换成桩：**5,396,480 字节** → Brotli 解码器占 **0.77 MB**。

因此改成 P/Invoke 调 `winhttp.dll`（TLS 走系统 SChannel、代理与证书都用系统的），本体回到 3.16 MB。

**NativeAOT 下踩到的两个坑（都已实测确认）**

- `Activator.CreateInstance(Type.GetTypeFromCLSID(...))` + `[ComImport]` 会抛
  `PlatformNotSupportedException: PlatformNotSupported_ComInterop`（NativeAOT 默认关闭内置 COM 互操作；
  加 `BuiltInComInteropSupport=true` 或 `RuntimeHostConfigurationOption` 都不生效，编译产物字节数也没变）。
  改成 `CoCreateInstance` + 按 vtable 序号取函数指针后，`IProgressDialog` 正常弹出（实测窗口标题、
  两行文字、进度条与取消按钮都正常，`HasUserCancelled` 可读）。
- `WinVerifyTrust` 走 `WTD_CHOICE_FILE` 只认**内嵌**签名：`kernel32.dll` 通过（0x00000000），
  而 `cmd.exe`／`notepad.exe` 返回 `0x800B0100`（TRUST_E_NOSIGNATURE，它们是目录签名）。
  官方运行时安装包是内嵌签名（`CN=.NET, O=Microsoft Corporation`），实测返回 0x00000000，
  所以这条检查不会误杀；反过来未签名文件（自己编的 exe）也确实返回 0x800B0100。

**下载链路**

- `https://aka.ms/windowsdesktop-runtime-8.0-win-x64` 是**死链**（302 到 Bing 搜索页）；
  可用的是 `https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe` → 301 →
  `builds.dotnet.microsoft.com/.../windowsdesktop-runtime-8.0.31-win-x64.exe`，58,715,896 字节（56.0 MB）。
- 走启动器的 WinHTTP 路径下载该文件：58,715,896 字节、用时 5.3 s、
  SHA256 `C375DFD80A967405CFEFF634912C1FCCC56261CE3D9209EA473F60A21184D1CC`，
  与另一次用托管 HttpClient 独立下载的哈希逐字节一致，`WinVerifyTrust` 返回 0x00000000。

**分支验证**

- 已装运行时（本机 8.0.19，来源为 `HKLM\...\WOW6432Node\...\sharedfx` 注册表）→ `--diagnose` 退出码 0，
  直接运行则解包到 `%LocalAppData%\SeriTerm\app\1.0.4.0\SeriTerm.exe` 并启动主程序；
  故意投放的假旧版本目录 `app\0.9.9.0` 被自动清掉；运行前后 `%AppData%\SeriTerm\settings.json`
  的 SHA256 完全一致（`EC769344…F6B920`）。
- 未装运行时（用 `SERITERM_DOTNET_ROOT` 指向空目录模拟）→ 依次出现
  「SeriTerm · 需要 .NET 桌面运行时」与「SeriTerm」两个对话框，点「否」后退出码 **2**。
- ⚠️ **未实测**：安装程序本身的执行（`/install /passive /norestart` + UAC）。跑它会改动本机已装的
  .NET 8 桌面运行时（8.0.19 → 8.0.31），所以只验证到"下载完成 + 签名通过 + 退出码分支"为止。

## AI 接入（MCP，v1.1.0）

链路跨三个进程（AI 客户端 → `SeriTerm.exe --mcp-stdio` 桥 → 界面进程），所以证据分两层：
单测覆盖协议与护栏，`tools/mcp-smoke.ps1` 做真实的端到端。

**自动化**

- `dotnet test --filter "FullyQualifiedName~Mcp"`：**52 通过 / 0 失败**；
- 覆盖：帧缓冲淘汰与游标语义（含"方向过滤也要推进游标"）、限速器令牌补充与突发上限、
  关键词匹配三种模式与非法输入、派发层的权限/限速/长度/参数四类拒绝路径、stdio 协议（通知不回应、
  未知方法 -32601、坏 JSON -32700、后端不可用时工具级 `[not_connected]`）、命名管道客户端
  （用自定义管道名起真的 `NamedPipeServerStream`，不碰真实运行时用的那条）。

**端到端（COM5 回环，Debug 构建，界面进程真实运行）**

只读档（默认配置）——`pwsh -File tools/mcp-smoke.ps1 -Exe <exe>`：

```
tools/list → count 4：serial_list_ports / serial_get_status / serial_read_frames / serial_wait_for_pattern
serial_get_status → open=true, portName=COM5, baudRate=1000000, permission=readOnly, connectedClients=1
serial_open  → isError=true  [permission_denied] ...
serial_write → isError=true  [permission_denied] ...
```

完全权限档（`settings.json` 里 `McpPermission: "Full"`）——加 `-ExpectFullPermission`：

```
tools/list → count 8（多出 serial_open / serial_close / serial_set_baud_rate / serial_write）
serial_get_status → open=false, permission=full
serial_open  → open=true, baudRate=1000000        （界面上的端口/参数同步成这次实际生效的一套）
serial_write → sentBytes=20, hex=73 65 … 6B 65 0D 0A, rateLimitRemaining=4
serial_wait_for_pattern("seriterm-mcp-smoke", since=0) → matched=true, scannedFrames=1,
    frames[0] = { seq 5, rx, 20 bytes, text "seriterm-mcp-smoke\r\n" }
serial_read_frames(since=0, direction=rx) → nextCursor=5, oldestCursor=1, totalFrames=5, evictedFrames=0
```

`rateLimitRemaining=4` 正是"`serial_open` 与 `serial_write` 各消耗一个令牌"（突发 6）的结果——
顺带证明了开关串口与发送走的是同一个限速桶。

审计行（`serial_read_frames` 取 `direction=system`，就是界面上那几行）：

```
[AI] 已接入（权限：完全权限）。
[AI] 已打开串口：COM5 1000000,8,None,1
[AI] 发送 20 字节：seriterm-mcp-smoke
```

**这一版踩到的坑（都已修，留作以后的路标）**

- 桥接层最初把整个 `params`（`{name, arguments}`）当成工具参数转发，界面进程按"未知参数 `name`"拒绝，
  所有工具调用都会失败。契约里现在写明了线上形状，回归用例断言假后端只收到 `arguments` 里的字段。
- `McpProtocol.WireJson` 少了 `TypeInfoResolver`：`JsonNode.ToJsonString(WireJson)` 会抛
  `InvalidOperationException`（"must specify a TypeInfoResolver setting before being marked as read-only"）。
  已给两个选项实例补上 `DefaultJsonTypeInfoResolver`。
- `volatile` 不能修饰可空值类型（`McpPermission?`），权限缓存改用 `volatile int` + 位移编码。
- 目标机上的 `PowerShell 7 + System.IO.Ports` 与 UIA 都正常工作，冒烟脚本用 `ProcessStartInfo` 重定向
  stdin/stdout 即可驱动桥进程，不需要额外工具。

**发布产物本身**（`artifacts\publish\SeriTerm-1.1.0-win-x64.exe`，即默认下载的那个 3.8 MB 启动器）

- 双击后解包到 `%LocalAppData%\SeriTerm\app\1.1.0.0\SeriTerm.exe` 并拉起界面进程（实测进程路径与目录名一致）；
- 把**启动器自己**配成 MCP server 也能用：启动器会把 `--mcp-stdio` 原样转给主程序，并且**留在原地陪着**它
  （原本的启动器是"拉起主程序就自己退出"，那样 MCP 客户端会认为 server 立刻断开）。
  实测同一条冒烟命令换成启动器路径即全绿：`pwsh -File tools/mcp-smoke.ps1 -Exe artifacts\publish\SeriTerm-1.1.0-win-x64.exe`
  → `initialize` 返回 `serverVersion: 1.1.0`、只读档 4 个工具、两个写工具 `[permission_denied]`、退出码 0。

## README 截图重拍（v1.1.0）

**为什么不是直接截屏**：这台开发机长期停在锁屏上，锁屏时 DWM 不为用户桌面交出像素——
`PrintWindow`（哪怕带 `PW_RENDERFULLCONTENT`）和 `CopyFromScreen` 出来的都是黑图（实测：整屏捕获是一张
纯色 2560×1600，窗口捕获是全黑 2560×1540）。

**做法**：写一次性工装 `artifacts/shot/`（WPF，被 .gitignore 忽略），它
① 用真的 DI 组合根起**真的** `MainViewModel` + `MainWindow` 并最大化；
② 用 `RenderTargetBitmap` 把窗口自己画进位图——这条路不过 DWM，锁屏下照样出图；
③ 打开真实 COM5 回环，把 12 行"设备输出"逐条**真发真收**（所以 Tx/Rx 成对、时间戳与断帧都是真的）；
④ 期间用 `McpPipeClient` 真的调一次 `serial_write`（日志里因此有 `[AI]` 审计行），
    再用一个长 `serial_wait_for_pattern` 把管道连接挂住，卡片上就真的显示"已接入 1 个 AI 客户端"；
⑤ 最后打开 `Ctrl+F` 浮层、关键字 `error`。
演示态配置（完全权限、115200、预置查找收藏、关掉背景模糊）由 `artifacts/shot/run.ps1` 临时替换
`settings.json`，跑完立即还原（本机配置属于使用者，不该被一次截图改掉）。

**成品**：`docs/images/main.png`，2560×1540（125% DPI 全屏）、208 762 字节。
图里同时能看到：左栏「AI 接入」卡片（启用 + 完全权限 + 已接入 1 个客户端 + 复制配置）、
状态栏 `MCP: 完全权限`（警示色）、右上 `Ctrl+F` 实时查找（`error` 命中 `第 1 / 共 4 条`、字符级高亮）
与「查找收藏」（error / OK / retry / boot），以及日志里成对的 `Tx`/`Rx` 与两行 `[AI]` 审计。

## 已知边界的实测依据

- 左栏在 1280×800 下需要滚动：「日志保存」一节从 y = 1267 px 才开始，而窗口底边在 810 px。
- 查找浮层遮挡范围：浮层右边界距日志区右边 48 px、顶边界距上边 24 px；无收藏、未搜索时整块折叠。
- 收藏多于 6 条时列表在 176 px 内自行滚动，浮层不会一直往下长。
- 「AI 接入」卡片在 2560×1540（本机最大化）下**不需要滚动**即可看到：
  UIA 读回的矩形是标题 `34,1112–513,1145`、两个复选框 `…,1155–1182` 与 `…,1188–1215`、
  「复制配置」按钮 `417,1112–513,1145`、状态文字 `…,1224–1247` 与 `…,1253–1276`，
  而窗口状态栏在 y≈1504。更矮的窗口（如 1280×800）下它和「日志保存」一样需要滚动才能看到——
  这一点与上面那条既有边界同源，没有额外恶化。
- 状态栏的 `MCP: 只读` 在 Rx/Tx 之后、贴右边界：UIA 矩形 `2445,1504–2495,1531` 与 `2503,1504–2543,1531`。
- 发布产物文件名带版本号（见上一节）：自包含 `SeriTerm-1.0.1-win-x64.exe` 单文件 64.0 MB（67,125,094 字节；自包含 + 压缩，`PublishTrimmed` 必须关闭：WPF 不支持裁剪）。
  换成设计稿图标后比上一版大 256 KB（图标本身从 10 KB 变成 97 KB，且它同时进 Win32 图标资源与 WPF 资源包）；
  加「关于」窗口后再多几 KB。
