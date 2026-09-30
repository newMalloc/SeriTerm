# SeriTerm

Windows 串口调试助手（C# / WPF / .NET 8），复刻 [lingguang「串口调试助手」](https://lgblog.github.io/Help/zh-Hans/) 的核心串口能力。

> 开发规格与模块清单见 [docs/development-plan.md](docs/development-plan.md)。

## 当前进度

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M0 | 解决方案骨架、依赖注入、深/浅主题（含系统标题栏跟随）、配置持久化 | ✅ 完成 |
| M1 | 端口枚举（含 WMI 设备友好名）、参数配置、打开/关闭、收发字节计数 | ✅ 完成 |
| M2 | 接收管线与日志视图（虚拟化列表、HEX/文本、编码切换、自动断帧） | ⏳ 进行中 |
| M9 | Ctrl+F 实时搜索、自动滚动智能开关 | 计划中 |
| M3–M8 | 断帧完善、发送、终端模式、自动重连、日志落盘、主题与发布 | 计划中 |

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
docs/development-plan.md       # 开发规格（范围、模块、里程碑、风险）
tools/capture-window.ps1       # 启动应用并截图，用于界面回归检查
src/
  SeriTerm.Core/               # 纯逻辑层，零 WPF 依赖，可单测
    Serial/                    # 参数模型、传输抽象与串口实现、端口枚举、错误翻译
  SeriTerm.App/                # WPF 表现层
    Services/                  # 主题、配置存储、用户提示、设备友好名
    ViewModels/                # MainViewModel 等
    Themes/                    # Shared.xaml + Dark.xaml + Light.xaml
  SeriTerm.Tests/              # xUnit（含 COM5 回环集成测试）
```

## 关键设计约束

这几条是这类工具不丢数据、不卡死的前提，改动时请勿违反：

1. **不使用 `SerialPort.DataReceived` 事件**：它在线程池上触发、会丢事件，高波特率必丢数据。传输层用专用后台线程做阻塞读。
2. **读取使用有限超时（100 ms）+ 取消标志轮询**：`SerialPort` 的取消令牌无法可靠中断已阻塞的读，`Close()` 可能永久挂住。
3. **读取线程绝不碰 UI**：只做计数/入队，界面侧用定时器批量刷新。
4. **主题全部走 `DynamicResource`**：切换深/浅色无需重启。

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
