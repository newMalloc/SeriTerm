using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SeriTerm.App.Common;
using SeriTerm.App.Services;
using SeriTerm.Core.Documents;
using SeriTerm.Core.Framing;
using SeriTerm.Core.Logging;
using SeriTerm.Core.Pipeline;
using SeriTerm.Core.Presets;
using SeriTerm.Core.Search;
using SeriTerm.Core.Send;
using SeriTerm.Core.Serial;
using SeriTerm.Core.Terminal;
using SeriTerm.Core.Text;

namespace SeriTerm.App.ViewModels;

/// <summary>
/// 主窗口视图模型。
///
/// 线程模型（关键）：
/// <list type="bullet">
/// <item>串口读取线程只做两件事：累加计数、在 <c>_pipelineLock</c> 保护下把字节喂给 <see cref="ReceiveProcessor"/>；</item>
/// <item>界面定时器（约 30 fps）负责把管线产出的行批量搬到 <see cref="LogDocument"/>，
///       这样无论串口多快，界面每帧最多刷新一次；</item>
/// <item>断帧的"最后一帧"依赖定时器调用 FlushIdle 才会显示出来。</item>
/// </list>
/// </summary>
public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>界面刷新间隔（约 30 fps）。</summary>
    private static readonly TimeSpan UiRefreshInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>暂停显示期间最多缓冲的行数，超出部分丢弃（避免内存无上限增长）。</summary>
    private const int MaxPausedBufferLines = 10_000;

    /// <summary>断帧方式下拉项。</summary>
    private static readonly (FramingMode Value, string Display)[] FramingModeOptions =
    [
        (FramingMode.Gap, "空闲间隔断帧"),
        (FramingMode.Delimiter, "分隔符断帧"),
        (FramingMode.None, "不断帧（每块一行）"),
    ];

    private readonly ISerialTransport _transport;
    private readonly PortFriendlyNameProvider _friendlyNames;
    private readonly IUserNotifier _notifier;
    private readonly ISettingsStore _settingsStore;
    private readonly IThemeService _themeService;
    private readonly IFileDialogService _fileDialogs;
    private readonly ILogger<MainViewModel> _logger;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;

    private readonly object _pipelineLock = new();
    private readonly ReceiveProcessor _processor;
    private readonly List<DisplayLine> _pendingLines = [];
    private readonly List<DisplayLine> _scratch = [];
    private readonly List<DisplayLine> _pausedBuffer = [];

    private readonly TimedSender _timedSender;

    /// <summary>自动重连监督者（M6）。</summary>
    private readonly ReconnectSupervisor _reconnect;

    /// <summary>当前日志落盘会话（M7）；未开启时为 null。</summary>
    private SessionLogger? _sessionLogger;

    /// <summary>断帧间隔/分隔符输入防抖：避免每敲一个字符就重建断帧器并把挂起数据吐出来。</summary>
    private readonly DispatcherTimer _receiveOptionsDebounce;

    private CancellationTokenSource? _sendFileCts;

    private ReceiveOptions _appliedOptions;

    private long _rxBytes;
    private long _txBytes;

    private readonly DispatcherTimer _uiTimer;

    public MainViewModel(
        ISerialTransport transport,
        PortFriendlyNameProvider friendlyNames,
        IUserNotifier notifier,
        ISettingsStore settingsStore,
        IThemeService themeService,
        IFileDialogService fileDialogs,
        ILogger<MainViewModel> logger)
    {
        _transport = transport;
        _friendlyNames = friendlyNames;
        _notifier = notifier;
        _settingsStore = settingsStore;
        _themeService = themeService;
        _fileDialogs = fileDialogs;
        _logger = logger;
        _settings = settingsStore.Load();
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        Ports = [];
        BaudRateOptions =
        [
            .. SerialSettings.CommonBaudRates.Select(r => r.ToString(CultureInfo.InvariantCulture)),
            CustomBaudRateItem,
        ];
        DataBitOptions = SerialSettings.CommonDataBits;
        ParityOptions = [.. SerialSettings.ParityOptions.Select(o => new Choice<Parity>(o.Value, o.Display))];
        StopBitsOptions = [.. SerialSettings.StopBitsOptions.Select(o => new Choice<StopBits>(o.Value, o.Display))];
        HandshakeOptions = [.. SerialSettings.HandshakeOptions.Select(o => new Choice<Handshake>(o.Value, o.Display))];
        FramingOptions = [.. FramingModeOptions.Select(o => new Choice<FramingMode>(o.Value, o.Display))];
        LineEndingOptions = [.. SendPayloadBuilder.LineEndingOptions.Select(o => new Choice<LineEnding>(o.Value, o.Display))];
        EncodingOptions = StatefulTextDecoder.SupportedEncodingNames;

        // 定时发送器：发送动作复用界面上的发送内容与当前设置
        _timedSender = new TimedSender(SendCurrentPayloadAsync);
        _timedSender.SendFailed += OnTimedSendFailed;

        // 自动重连：传输层只报故障，重连策略由监督者负责。
        //
        // 注意这里必须显式给 Enabled 赋初值：AutoReconnect 字段默认就是 true，
        // 若配置文件里也是 true，属性值没有变化 → OnAutoReconnectChanged 不会被调用，
        // 监督者就会一直是"关"的（界面勾着自动重连却从不重连）。这个坑真的踩过。
        _reconnect = new ReconnectSupervisor(_transport) { Enabled = AutoReconnect };
        _reconnect.StatusChanged += OnReconnectStatusChanged;

        _receiveOptionsDebounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _receiveOptionsDebounce.Tick += (_, _) =>
        {
            _receiveOptionsDebounce.Stop();
            ApplyReceiveOptions();
        };

        // 注意顺序：_processor 必须先建好，因为 ApplySettingsToUi 改动接收设置时会触发
        // ApplyReceiveOptions，那里要用到 _processor。
        _processor = new ReceiveProcessor(BuildReceiveOptions());
        _appliedOptions = _processor.Options;

        ApplySettingsToUi(_settings);

        // 兜底：配置里的值与字段默认值相同时属性通知不会触发，这里再同步一次
        _reconnect.Enabled = AutoReconnect;

        Log.SearchChanged += (_, _) => UpdateSearchStatus();

        _transport.StateChanged += OnTransportStateChanged;
        _transport.BytesReceived += OnBytesReceived;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = UiRefreshInterval,
        };
        _uiTimer.Tick += (_, _) => OnUiRefresh();
        _uiTimer.Start();

        State = _transport.State;
        StatusText = State.ToDisplayText();
        UpdateThemeButtonText();
        UpdateSearchStatus();
    }

    /// <summary>请求把某一行滚入视野（由日志视图处理）。</summary>
    public event EventHandler<DisplayLine?>? ScrollToLineRequested;

    /// <summary>请求滚动到最新位置。</summary>
    public event EventHandler? ScrollToEndRequested;

    public LogDocument Log { get; } = new();

    // ---------- 集合与选项 ----------

    public ObservableCollection<PortItem> Ports { get; }

    /// <summary>
    /// 波特率下拉框的最后一项。它不是波特率，而是"切到手动输入"的开关：
    /// 选中后由视图清空输入框并把光标交还给用户（见 MainWindow.OnBaudRateSelectionChanged）。
    /// 列表里放字符串而不是 int，正是为了能混进这一项。
    /// </summary>
    public const string CustomBaudRateItem = "自定义输入…";

    public IReadOnlyList<string> BaudRateOptions { get; }

    public IReadOnlyList<int> DataBitOptions { get; }

    public IReadOnlyList<Choice<Parity>> ParityOptions { get; }

    public IReadOnlyList<Choice<StopBits>> StopBitsOptions { get; }

    public IReadOnlyList<Choice<Handshake>> HandshakeOptions { get; }

    public IReadOnlyList<Choice<FramingMode>> FramingOptions { get; }

    public IReadOnlyList<Choice<LineEnding>> LineEndingOptions { get; }

    public IReadOnlyList<string> EncodingOptions { get; }

    /// <summary>
    /// 日志字号可选值。原来是 A-/A+ 两个按钮，得先点一下才知道当前字号是多少，
    /// 也不像字号控件；直接用下拉框，当前值一眼可见。
    /// </summary>
    public IReadOnlyList<double> FontSizeOptions { get; } = [9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24];

    // ---------- 串口参数 ----------

    [ObservableProperty]
    private PortItem? _selectedPort;

    [ObservableProperty]
    private string _baudRateText = "115200";

    [ObservableProperty]
    private int _dataBits = 8;

    [ObservableProperty]
    private Choice<Parity>? _selectedParity;

    [ObservableProperty]
    private Choice<StopBits>? _selectedStopBits;

    [ObservableProperty]
    private Choice<Handshake>? _selectedHandshake;

    [ObservableProperty]
    private bool _dtrEnable;

    [ObservableProperty]
    private bool _rtsEnable;

    // ---------- 接收设置 ----------

    [ObservableProperty]
    private bool _hexDisplay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGapFraming))]
    [NotifyPropertyChangedFor(nameof(IsDelimiterFraming))]
    private Choice<FramingMode>? _selectedFraming;

    [ObservableProperty]
    private string _autoFrameGapText = "20";

    [ObservableProperty]
    private string _delimiterText = "\\r\\n";

    [ObservableProperty]
    private string _selectedEncodingName = "UTF-8";

    [ObservableProperty]
    private bool _showTimestamp;

    public bool IsGapFraming => SelectedFraming?.Value == FramingMode.Gap;

    public bool IsDelimiterFraming => SelectedFraming?.Value == FramingMode.Delimiter;

    // ---------- 发送设置 ----------

    [ObservableProperty]
    private string _sendText = "SeriTerm loopback test";

    [ObservableProperty]
    private bool _sendHex;

    [ObservableProperty]
    private Choice<LineEnding>? _selectedLineEnding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimedSendButtonText))]
    private bool _timedSendEnabled;

    [ObservableProperty]
    private string _timedSendIntervalText = "1.0";

    [ObservableProperty]
    private string _timedSendStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendFileButtonText))]
    private bool _isSendingFile;

    [ObservableProperty]
    private double _sendFileProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSendFileStatus))]
    private string _sendFileStatusText = string.Empty;

    public string TimedSendButtonText => TimedSendEnabled ? "停止定时" : "定时发送";

    public string SendFileButtonText => IsSendingFile ? "取消发送" : "发送文件";

    public bool HasSendFileStatus => !string.IsNullOrWhiteSpace(SendFileStatusText);

    // ---------- 日志落盘（M7） ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoggingEnabled))]
    private bool _saveLogToFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoggingEnabled))]
    private bool _saveRawLog;

    [ObservableProperty]
    private string _logDirectory = LogSessionOptions.DefaultDirectory;

    [ObservableProperty]
    private string _logStatusText = string.Empty;

    public bool IsLoggingEnabled => SaveLogToFile || SaveRawLog;

    // ---------- 终端模式（M5） ----------

    [ObservableProperty]
    private bool _terminalMode;

    [ObservableProperty]
    private bool _terminalLocalEcho;

    [ObservableProperty]
    private bool _terminalBackspaceSendsDel = true;

    /// <summary>当前已敲入但还没回车的终端输入（仅用于界面提示）。</summary>
    [ObservableProperty]
    private string _terminalEchoText = string.Empty;

    private readonly StringBuilder _terminalEchoBuffer = new();

    // ---------- 自动重连（M6） ----------

    [ObservableProperty]
    private bool _autoReconnect = true;

    [ObservableProperty]
    private bool _autoOpenOnStartup;

    /// <summary>
    /// 窗口背景是否用亚克力模糊透出桌面。默认开，但系统不支持/关了"透明效果"时
    /// <see cref="BackdropAvailable"/> 为 false，界面上这个开关会直接禁用。
    /// </summary>
    [ObservableProperty]
    private bool _blurBackground = true;

    /// <summary>当前系统能不能做背景模糊（能读到桌面壁纸才行）。</summary>
    public bool BackdropAvailable => DesktopBackdrop.IsAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReconnectStatus))]
    private string _reconnectStatusText = string.Empty;

    public bool HasReconnectStatus => !string.IsNullOrEmpty(ReconnectStatusText);

    // ---------- 配置预设（M8） ----------

    /// <summary>已保存的配置预设；下拉选择即套用。</summary>
    public ObservableCollection<SerialPreset> Presets { get; } = [];

    [ObservableProperty]
    private SerialPreset? _selectedPreset;

    /// <summary>套用预设期间抑制"选择变化 → 再套用"的回环。</summary>
    private bool _applyingPreset;

    [RelayCommand]
    private void SavePreset()
    {
        if (!TryBuildSettings(out var serial, out var error))
        {
            _notifier.ShowError("参数不合法", error);
            return;
        }

        var name = _notifier.AskText("保存预设", "预设名称：", SerialPreset.BuildDefaultName(serial));

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var preset = BuildPreset(name, serial);
        var updated = SerialPreset.Upsert([.. Presets], preset);

        _applyingPreset = true;
        try
        {
            Presets.Clear();
            foreach (var item in updated)
            {
                Presets.Add(item);
            }

            SelectedPreset = Presets.First(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _applyingPreset = false;
        }

        _settings.Presets = [.. Presets];
        StatusDetail = $"已保存预设：{preset.Name}";
        AddSystemLine($"已保存配置预设：{preset.Name}");
    }

    [RelayCommand]
    private void DeletePreset()
    {
        var current = SelectedPreset;

        if (current is null)
        {
            _notifier.ShowInfo("提示", "请先在预设下拉框里选择一个预设。");
            return;
        }

        var remaining = SerialPreset.Remove([.. Presets], current.Name, out var removed);

        if (!removed)
        {
            return;
        }

        _applyingPreset = true;
        try
        {
            Presets.Clear();
            foreach (var item in remaining)
            {
                Presets.Add(item);
            }

            SelectedPreset = null;
        }
        finally
        {
            _applyingPreset = false;
        }

        _settings.Presets = [.. Presets];
        StatusDetail = $"已删除预设：{current.Name}";
        AddSystemLine($"已删除配置预设：{current.Name}");
    }

    partial void OnSelectedPresetChanged(SerialPreset? value)
    {
        if (value is null || _applyingPreset)
        {
            return;
        }

        ApplyPreset(value);
    }

    private void ApplyPreset(SerialPreset preset)
    {
        _applyingPreset = true;
        try
        {
            var serial = preset.Serial;

            BaudRateText = serial.BaudRate.ToString();
            DataBits = DataBitOptions.Contains(serial.DataBits) ? serial.DataBits : 8;
            SelectedParity = ParityOptions.FirstOrDefault(o => o.Value == serial.Parity) ?? ParityOptions[0];
            SelectedStopBits = StopBitsOptions.FirstOrDefault(o => o.Value == serial.StopBits) ?? StopBitsOptions[0];
            SelectedHandshake = HandshakeOptions.FirstOrDefault(o => o.Value == serial.Handshake) ?? HandshakeOptions[0];
            DtrEnable = serial.DtrEnable;
            RtsEnable = serial.RtsEnable;

            var port = Ports.FirstOrDefault(p => string.Equals(p.PortName, serial.PortName, StringComparison.OrdinalIgnoreCase));

            if (port is not null)
            {
                SelectedPort = port;
            }

            SelectedFraming = FramingOptions.FirstOrDefault(o => o.Value == preset.Framing) ?? FramingOptions[0];
            AutoFrameGapText = preset.AutoFrameGapMilliseconds.ToString();
            DelimiterText = preset.DelimiterText;
            SelectedEncodingName = EncodingOptions.Contains(preset.EncodingName) ? preset.EncodingName : "UTF-8";
            HexDisplay = preset.HexDisplay;
            SelectedLineEnding = LineEndingOptions.FirstOrDefault(o => o.Value == preset.SendLineEnding) ?? LineEndingOptions[0];
            SendHex = preset.SendHex;
        }
        finally
        {
            _applyingPreset = false;
        }

        var suffix = IsOpen ? "（串口已打开，重新打开后生效）" : string.Empty;
        StatusDetail = $"已套用预设：{preset.Name}{suffix}";
        AddSystemLine($"已套用配置预设：{preset.Name}{suffix}");
    }

    private SerialPreset BuildPreset(string name, SerialSettings serial) => new()
    {
        Name = name,
        Serial = serial,
        Framing = SelectedFraming?.Value ?? FramingMode.Gap,
        AutoFrameGapMilliseconds = int.TryParse(AutoFrameGapText?.Trim(), out var gap) ? gap : 20,
        DelimiterText = DelimiterText,
        EncodingName = SelectedEncodingName,
        HexDisplay = HexDisplay,
        SendLineEnding = SelectedLineEnding?.Value ?? LineEnding.CrLf,
        SendHex = SendHex,
    };

    // ---------- 界面状态 ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    [NotifyPropertyChangedFor(nameof(OpenButtonText))]
    private TransportState _state = TransportState.Closed;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private string _statusDetail = string.Empty;

    [ObservableProperty]
    private string _themeButtonText = "切换到浅色";

    [ObservableProperty]
    private string _rxText = "0 B";

    [ObservableProperty]
    private string _txText = "0 B";

    [ObservableProperty]
    private double _logFontSize = 13;

    [ObservableProperty]
    private bool _lineWrap = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseButtonText))]
    private bool _isPaused;

    [ObservableProperty]
    private string _pausedHintText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingNewLines))]
    [NotifyPropertyChangedFor(nameof(NewDataBarText))]
    private int _pendingNewLines;

    [ObservableProperty]
    private bool _autoScroll = true;

    // ---------- 搜索状态 ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSearchOverlay))]
    private bool _searchVisible;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _searchCaseSensitive;

    [ObservableProperty]
    private string _searchStatusText = "输入关键字开始查找";

    public bool IsOpen => State == TransportState.Open;

    public bool CanEditSettings => State is TransportState.Closed or TransportState.Faulted;

    public string OpenButtonText => IsOpen ? "关闭串口" : "打开串口";

    public string PauseButtonText => IsPaused ? "继续显示" : "暂停显示";

    /// <summary>供自绘标题栏切换太阳/月亮图标。</summary>
    public bool IsDarkTheme => _themeService.IsDarkEffective;

    public bool HasPendingNewLines => PendingNewLines > 0;

    public string NewDataBarText => $"▾ {PendingNewLines} 条新数据（点击回到最新）";

    public AppSettings Settings => _settings;

    // ---------- 命令 ----------

    public Task InitializeAsync()
    {
        return InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        await RefreshPortsAsync().ConfigureAwait(true);

        if (AutoOpenOnStartup && CanEditSettings && SelectedPort is not null)
        {
            AddSystemLine($"自动打开串口：{SelectedPort.PortName}");
            await ToggleOpenAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RefreshPortsAsync()
    {
        _friendlyNames.Invalidate();

        var names = await Task.Run(PortEnumerator.GetPortNames).ConfigureAwait(true);
        var friendly = await _friendlyNames.GetAsync().ConfigureAwait(true);

        var previousPort = SelectedPort?.PortName;

        Ports.Clear();
        foreach (var name in names)
        {
            Ports.Add(new PortItem(name, friendly.TryGetValue(name, out var display) ? display : name));
        }

        SelectedPort =
            Ports.FirstOrDefault(p => string.Equals(p.PortName, previousPort, StringComparison.OrdinalIgnoreCase))
            ?? Ports.FirstOrDefault(p => string.Equals(p.PortName, _settings.LastSerial.PortName, StringComparison.OrdinalIgnoreCase))
            ?? Ports.FirstOrDefault();

        StatusDetail = Ports.Count == 0
            ? "未检测到串口设备，请检查 USB 转串口是否插好。"
            : $"检测到 {Ports.Count} 个串口。";

        _logger.LogInformation("刷新串口列表：{Count} 个", Ports.Count);
    }

    [RelayCommand]
    private async Task ToggleOpenAsync()
    {
        if (IsOpen)
        {
            await _transport.CloseAsync().ConfigureAwait(true);
            return;
        }

        // 用户手动操作优先：先停掉可能正在进行的自动重连
        await _reconnect.StopAsync().ConfigureAwait(true);

        if (!TryBuildSettings(out var settings, out var error))
        {
            _notifier.ShowError("参数不合法", error);
            return;
        }

        try
        {
            await _transport.OpenAsync(settings).ConfigureAwait(true);
            _settings.LastSerial = settings;
            StatusDetail = settings.ToShortDescription();
            AddSystemLine($"串口已打开：{settings.ToShortDescription()}");
        }
        catch (SerialLinkException ex)
        {
            StatusDetail = "打开失败。";
            _notifier.ShowError($"打开 {settings.PortName} 失败", ex.Message);
        }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var next = _themeService.IsDarkEffective ? ThemeMode.Light : ThemeMode.Dark;
        _themeService.Apply(next);
        _settings.Theme = next;
        UpdateThemeButtonText();
    }

    [RelayCommand]
    private void ToggleLineWrap() => LineWrap = !LineWrap;

    [RelayCommand]
    private void ClearLog()
    {
        Log.Clear();
        PendingNewLines = 0;
        _pausedBuffer.Clear();
        PausedHintText = string.Empty;

        lock (_pipelineLock)
        {
            _pendingLines.Clear();
            _processor.Reset();
        }

        UpdateSearchStatus();
        AddSystemLine("显示数据已清空。");
    }

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        if (Log.Lines.Count == 0)
        {
            _notifier.ShowInfo("提示", "当前没有可保存的数据。");
            return;
        }

        var path = _fileDialogs.AskSaveFile(
            $"SeriTerm_{DateTime.Now:yyyyMMdd_HHmmss}.log",
            "文本日志 (*.log)|*.log|所有文件 (*.*)|*.*",
            "保存显示数据");

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var builder = new StringBuilder();
            foreach (var line in Log.Lines)
            {
                builder.Append(line.TimeText)
                    .Append(" [")
                    .Append(line.DirectionText)
                    .Append("] ")
                    .AppendLine(line.Text);
            }

            // 带 BOM 的 UTF-8：记事本/Excel 打开中文不乱码
            await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(true)).ConfigureAwait(true);

            StatusDetail = $"已保存 {Log.Lines.Count} 行到 {path}";
            _notifier.ShowInfo("保存完成", $"已保存 {Log.Lines.Count} 行到：\n{path}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存显示数据失败");
            _notifier.ShowError("保存失败", ex.Message);
        }
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (IsPaused)
        {
            IsPaused = false;
            PausedHintText = string.Empty;

            if (_pausedBuffer.Count > 0)
            {
                var buffered = _pausedBuffer.Count;
                AppendToLog([.. _pausedBuffer]);
                _pausedBuffer.Clear();
                AddSystemLine($"已恢复显示（补显示暂停期间的 {buffered} 行）。");
            }

            return;
        }

        IsPaused = true;
        PausedHintText = "已暂停显示（数据仍在接收，恢复后补显示）";
    }

    // ---------- 自动滚动 ----------

    /// <summary>离开底部时调用：停止跟随。</summary>
    public void PauseAutoScroll()
    {
        if (AutoScroll)
        {
            AutoScroll = false;
        }
    }

    /// <summary>回到最新位置时调用：恢复跟随并滚到底。</summary>
    public void ResumeAutoScroll()
    {
        PendingNewLines = 0;

        var changed = !AutoScroll;
        AutoScroll = true;

        // 值发生变化时由 OnAutoScrollChanged 统一请求滚动，避免重复请求
        if (!changed)
        {
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnAutoScrollChanged(bool value)
    {
        if (!value)
        {
            return;
        }

        PendingNewLines = 0;
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleAutoScroll()
    {
        if (AutoScroll)
        {
            AutoScroll = false;
        }
        else
        {
            ResumeAutoScroll();
        }
    }

    [RelayCommand]
    private void ScrollToLatest() => ResumeAutoScroll();

    // ---------- 搜索 ----------

    [RelayCommand]
    private void OpenSearch() => SearchVisible = true;

    [RelayCommand]
    private void CloseSearch()
    {
        SearchVisible = false;
        SearchText = string.Empty;
        Log.ClearSearch();
        UpdateSearchStatus();
    }

    [RelayCommand]
    private void SearchNext() => MoveToMatch(Log.MoveNextMatch());

    [RelayCommand]
    private void SearchPrevious() => MoveToMatch(Log.MovePreviousMatch());

    private void MoveToMatch(DisplayLine? line)
    {
        if (line is null)
        {
            return;
        }

        // 跳到命中位置时必须停止跟随，否则新数据会立刻把视野拉走
        PauseAutoScroll();
        ScrollToLineRequested?.Invoke(this, line);
        UpdateSearchStatus();
    }

    partial void OnSearchTextChanged(string value)
    {
        Log.SetSearch(value, SearchCaseSensitive);
        OnPropertyChanged(nameof(CanAddSearchFavorite));
    }

    partial void OnSearchCaseSensitiveChanged(bool value) => Log.SetSearch(SearchText, value);

    // ---------- 查找收藏 ----------

    /// <summary>已收藏的查找关键字：点一下填回搜索框，点标签上的 × 删除。</summary>
    public ObservableCollection<string> SearchFavorites { get; } = [];

    /// <summary>有没有收藏（决定收藏栏这一段是否出现）。</summary>
    public bool HasSearchFavorites => SearchFavorites.Count > 0;

    /// <summary>
    /// 日志右上角那块浮层（查找框 + 收藏栏）要不要显示。
    /// 搜索打开、或者还存着收藏，两者占一条即可——收藏是"随时点一下就填回关键字"的入口，
    /// 只在搜索打开时才出现的话，就得先按「查找」才能用它。
    /// </summary>
    public bool ShowSearchOverlay => SearchVisible || HasSearchFavorites;

    /// <summary>当前关键字能不能存成收藏（非空且还没收藏过）。</summary>
    public bool CanAddSearchFavorite
        => !string.IsNullOrWhiteSpace(SearchText) && !SearchFavoriteList.Contains(SearchFavorites, SearchText);

    /// <summary>把搜索框里的关键字存进收藏（已存在或为空则什么也不做）。</summary>
    [RelayCommand]
    private void AddSearchFavorite()
    {
        var updated = SearchFavoriteList.Add(SearchFavorites, SearchText, out var added);

        if (!added)
        {
            StatusDetail = "这个关键字已经在收藏里了。";
            return;
        }

        ReplaceSearchFavorites(updated);
        StatusDetail = $"已收藏查找关键字：{SearchText.Trim()}";
    }

    /// <summary>删除一条收藏（标签上的 × 调用）。</summary>
    [RelayCommand]
    private void RemoveSearchFavorite(string? text)
    {
        var updated = SearchFavoriteList.Remove(SearchFavorites, text, out var removed);

        if (!removed)
        {
            return;
        }

        ReplaceSearchFavorites(updated);
        StatusDetail = $"已删除查找收藏：{text?.Trim()}";
    }

    /// <summary>点收藏标签：打开搜索条、填入关键字并跳到第一处命中。</summary>
    [RelayCommand]
    private void ApplySearchFavorite(string? text)
    {
        var normalized = SearchFavoriteList.Normalize(text);

        if (normalized is null)
        {
            return;
        }

        SearchVisible = true;
        SearchText = normalized;
        StatusDetail = $"查找：{normalized}";

        // 命中为 0 时 MoveToMatch 会直接返回，状态文本改由这里补一次
        if (Log.MoveNextMatch() is { } line)
        {
            MoveToMatch(line);
        }
        else
        {
            UpdateSearchStatus();
        }
    }

    /// <summary>复制之后的反馈（走状态栏，不弹框）。</summary>
    /// <param name="what">复制了什么，例如"3 行日志"或"选中的文本（12 字）"。</param>
    public void ReportCopyResult(string what, bool success)
        => StatusDetail = success
            ? $"已复制{what}到剪贴板"
            : "复制失败：剪贴板被其它程序占用，请稍后重试";

    private void ReplaceSearchFavorites(IReadOnlyList<string> items)
    {
        SyncSearchFavorites(items);
        _settings.SearchFavorites = [.. SearchFavorites];
        NotifyFavoritesChanged();
    }

    /// <summary>
    /// 把收藏集合同步成 <paramref name="items"/>，只做增删移、不整体清空。
    ///
    /// 为什么不能图省事写 Clear() + 逐个 Add()：Clear() 会让集合发出 Reset，
    /// ItemsControl 收到 Reset 会把所有行容器丢掉重建，而自动化的那一棵树不会跟着重建——
    /// 实测加第二条收藏之后，第一条在 UIA 树里就只剩一个没有子元素的 DataItem，
    /// 读屏软件和自动化脚本都找不到它。增量更新还顺带保住了收藏栏的滚动位置。
    /// </summary>
    private void SyncSearchFavorites(IReadOnlyList<string> items)
    {
        // 1) 删掉目标列表里已经没有的（从后往前走，索引不会失效）
        for (var i = SearchFavorites.Count - 1; i >= 0; i--)
        {
            if (!items.Contains(SearchFavorites[i]))
            {
                SearchFavorites.RemoveAt(i);
            }
        }

        // 2) 按目标顺序补齐或归位
        for (var i = 0; i < items.Count; i++)
        {
            if (i >= SearchFavorites.Count)
            {
                SearchFavorites.Add(items[i]);
                continue;
            }

            if (string.Equals(SearchFavorites[i], items[i], StringComparison.Ordinal))
            {
                continue;
            }

            var existing = SearchFavorites.IndexOf(items[i]);
            if (existing > i)
            {
                SearchFavorites.Move(existing, i);
            }
            else
            {
                SearchFavorites.Insert(i, items[i]);
            }
        }
    }

    private void NotifyFavoritesChanged()
    {
        OnPropertyChanged(nameof(HasSearchFavorites));
        OnPropertyChanged(nameof(CanAddSearchFavorite));
        OnPropertyChanged(nameof(ShowSearchOverlay));
    }

    private void UpdateSearchStatus()
        => SearchStatusText = !Log.HasSearch
            ? "输入关键字开始查找"
            : Log.MatchCount == 0
                ? "无匹配"
                : $"第 {Log.CurrentMatchIndex + 1} / 共 {Log.MatchCount} 条";

    [RelayCommand]
    private void ChooseLogDirectory()
    {
        var folder = _fileDialogs.AskFolder("选择日志保存目录", LogDirectory);

        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        LogDirectory = folder;
        RestartLogging();
    }

    [RelayCommand]
    private void OpenLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo { FileName = LogDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _notifier.ShowError("打开日志目录失败", ex.Message);
        }
    }

    // ---------- 日志落盘联动 ----------

    partial void OnSaveLogToFileChanged(bool value) => RestartLogging();

    partial void OnSaveRawLogChanged(bool value) => RestartLogging();

    partial void OnAutoReconnectChanged(bool value) => _reconnect.Enabled = value;

    private void RestartLogging()
    {
        StopLogging();

        if (!SaveLogToFile && !SaveRawLog)
        {
            LogStatusText = string.Empty;
            return;
        }

        try
        {
            _sessionLogger = new SessionLogger(new LogSessionOptions
            {
                Directory = string.IsNullOrWhiteSpace(LogDirectory) ? LogSessionOptions.DefaultDirectory : LogDirectory,
                TextEnabled = SaveLogToFile,
                RawEnabled = SaveRawLog,
                HexText = HexDisplay,
                EncodingName = SelectedEncodingName,
            });

            LogStatusText = $"正在记录到 {_sessionLogger.Directory}";
            AddSystemLine($"开始保存日志到：{_sessionLogger.Directory}");
        }
        catch (Exception ex)
        {
            _sessionLogger = null;
            LogStatusText = string.Empty;
            _sessionLogger = null;
            _notifier.ShowError("无法开始保存日志", ex.Message);
        }
    }

    private void StopLogging()
    {
        var logger = _sessionLogger;
        _sessionLogger = null;

        if (logger is null)
        {
            return;
        }

        // 释放会等待队列落盘，放到后台完成，避免界面卡住
        _ = logger.DisposeAsync().AsTask().ContinueWith(
            task => _ = task.Exception,
            TaskScheduler.Default);
    }

    private void OnReconnectStatusChanged(object? sender, ReconnectEventArgs e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            ReconnectStatusText = e.State == ReconnectState.Idle ? string.Empty : e.Message;

            if (!string.IsNullOrWhiteSpace(e.Message))
            {
                StatusDetail = e.Message;
            }

            if (e.State is ReconnectState.Reconnected or ReconnectState.GaveUp)
            {
                AddSystemLine(e.Message);
            }

            // 放弃重连才是真的没救了，这时必须强提示；重连成功不打扰用户
            if (e.State == ReconnectState.GaveUp)
            {
                _notifier.ShowError("自动重连已停止", e.Message);
            }
        });
    }

    // ---------- 终端模式（M5） ----------

    partial void OnTerminalModeChanged(bool value)
    {
        _terminalEchoBuffer.Clear();
        TerminalEchoText = string.Empty;

        // 终端模式下过滤 ANSI 转义序列，否则彩色/光标控制会把日志刷成乱码
        ApplyReceiveOptions();

        if (value)
        {
            AddSystemLine("已进入终端模式：键盘输入直接发送到串口（焦点在输入框时不生效）。");
        }
    }

    /// <summary>终端模式：敲入普通字符（立即发送，不等回车）。</summary>
    public async Task TerminalInputAsync(string text)
    {
        if (!TerminalMode || string.IsNullOrEmpty(text))
        {
            return;
        }

        await WriteTerminalBytesAsync(TerminalKeyEncoder.EncodeText(text, SelectedEncodingName)).ConfigureAwait(true);

        _terminalEchoBuffer.Append(text);
        TerminalEchoText = _terminalEchoBuffer.ToString();
    }

    /// <summary>终端模式：回车。本地回显时整行记成一条 Tx，避免一个字符一行把日志刷爆。</summary>
    public async Task TerminalEnterAsync()
    {
        if (!TerminalMode)
        {
            return;
        }

        var ending = TerminalKeyEncoder.EncodeEnter(SelectedLineEnding?.Value ?? LineEnding.CrLf);
        await WriteTerminalBytesAsync(ending).ConfigureAwait(true);

        if (TerminalLocalEcho)
        {
            var typed = TerminalKeyEncoder.EncodeText(_terminalEchoBuffer.ToString(), SelectedEncodingName);
            RecordTransmitted([.. typed, .. ending], DateTime.Now);
        }

        _terminalEchoBuffer.Clear();
        TerminalEchoText = string.Empty;
    }

    /// <summary>终端模式：退格。</summary>
    public async Task TerminalBackspaceAsync()
    {
        if (!TerminalMode)
        {
            return;
        }

        await WriteTerminalBytesAsync(TerminalKeyEncoder.EncodeBackspace(TerminalBackspaceSendsDel)).ConfigureAwait(true);

        if (_terminalEchoBuffer.Length > 0)
        {
            _terminalEchoBuffer.Length--;
            TerminalEchoText = _terminalEchoBuffer.ToString();
        }
    }

    /// <summary>终端模式：Ctrl + 字母（如 Ctrl+C → 0x03）。</summary>
    public async Task TerminalControlAsync(char letter)
    {
        if (!TerminalMode)
        {
            return;
        }

        await WriteTerminalBytesAsync(TerminalKeyEncoder.EncodeControl(letter)).ConfigureAwait(true);
    }

    /// <summary>终端模式：粘贴多行内容，逐行发送并补行尾。</summary>
    public async Task TerminalPasteAsync(string text)
    {
        if (!TerminalMode || string.IsNullOrEmpty(text))
        {
            return;
        }

        var ending = SelectedLineEnding?.Value ?? LineEnding.CrLf;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var payload = TerminalKeyEncoder.EncodeText(line, SelectedEncodingName);
            await WriteTerminalBytesAsync(payload).ConfigureAwait(true);
            await WriteTerminalBytesAsync(TerminalKeyEncoder.EncodeEnter(ending)).ConfigureAwait(true);

            if (TerminalLocalEcho)
            {
                RecordTransmitted([.. payload, .. TerminalKeyEncoder.EncodeEnter(ending)], DateTime.Now);
            }
        }

        _terminalEchoBuffer.Clear();
        TerminalEchoText = string.Empty;
    }

    private async Task WriteTerminalBytesAsync(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        if (!IsOpen)
        {
            _notifier.ShowInfo("提示", "请先打开串口。");
            return;
        }

        try
        {
            await _transport.WriteAsync(bytes).ConfigureAwait(true);
            Interlocked.Add(ref _txBytes, bytes.Length);
        }
        catch (SerialLinkException ex)
        {
            _notifier.ShowError("发送失败", ex.Message);
        }
    }

    // ---------- 发送 ----------

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!TryBuildSendPayload(out var payload, out var error))
        {
            _notifier.ShowError("发送内容不合法", error);
            return;
        }

        if (!IsOpen)
        {
            _notifier.ShowInfo("提示", "请先打开串口。");
            return;
        }

        try
        {
            await WriteAndRecordAsync(payload).ConfigureAwait(true);
        }
        catch (SerialLinkException ex)
        {
            _notifier.ShowError("发送失败", ex.Message);
        }
    }

    [RelayCommand]
    private void ToggleTimedSend()
    {
        if (!TimedSendEnabled)
        {
            if (!IsOpen)
            {
                _notifier.ShowInfo("提示", "请先打开串口。");
                return;
            }

            if (!TryBuildSendPayload(out _, out var contentError))
            {
                _notifier.ShowError("发送内容不合法", contentError);
                return;
            }

            if (!TryGetTimedSendInterval(out _, out var intervalError))
            {
                _notifier.ShowError("定时发送间隔不合法", intervalError);
                return;
            }
        }

        TimedSendEnabled = !TimedSendEnabled;
    }

    partial void OnTimedSendEnabledChanged(bool value)
    {
        if (value)
        {
            _timedSender.Interval = TimeSpan.FromSeconds(GetTimedSendSeconds());
            _timedSender.Start();
            AddSystemLine($"定时发送已开始：每 {_timedSender.Interval.TotalSeconds:0.##} 秒一次。");
            return;
        }

        _ = _timedSender.StopAsync();
        TimedSendStatusText = string.Empty;
        AddSystemLine("定时发送已停止。");
    }

    partial void OnTimedSendIntervalTextChanged(string value)
    {
        if (!TimedSendEnabled || !TryGetTimedSendInterval(out var seconds, out _))
        {
            return;
        }

        _ = RestartTimedSendAsync(seconds);
    }

    private async Task RestartTimedSendAsync(double seconds)
    {
        await _timedSender.StopAsync().ConfigureAwait(true);
        _timedSender.Interval = TimeSpan.FromSeconds(seconds);
        _timedSender.Start();
    }

    [RelayCommand]
    private async Task SendFileAsync()
    {
        if (IsSendingFile)
        {
            _sendFileCts?.Cancel();
            return;
        }

        if (!IsOpen)
        {
            _notifier.ShowInfo("提示", "请先打开串口。");
            return;
        }

        var path = _fileDialogs.AskOpenFile("所有文件 (*.*)|*.*", "选择要发送的文件");

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fileName = Path.GetFileName(path);
        _sendFileCts = new CancellationTokenSource();
        IsSendingFile = true;
        SendFileProgress = 0;
        SendFileStatusText = $"正在发送 {fileName}…";

        try
        {
            await using var stream = File.OpenRead(path);

            var progress = new Progress<double>(ratio =>
            {
                SendFileProgress = ratio * 100;
                SendFileStatusText = $"正在发送 {fileName}… {ratio * 100:0}%";
            });

            var sent = await StreamSendJob.SendAsync(
                stream,
                WriteFileChunkAsync,
                StreamSendJob.DefaultChunkSize,
                TimeSpan.Zero,
                progress,
                _sendFileCts.Token).ConfigureAwait(true);

            AddSystemLine($"文件发送完成：{fileName}（{sent} 字节）");
            SendFileStatusText = $"已发送 {sent} 字节";
        }
        catch (OperationCanceledException)
        {
            AddSystemLine($"文件发送已取消：{fileName}");
            SendFileStatusText = "已取消";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送文件失败");
            _notifier.ShowError("发送文件失败", ex.Message);
            SendFileStatusText = "发送失败";
        }
        finally
        {
            IsSendingFile = false;
            _sendFileCts?.Dispose();
            _sendFileCts = null;
        }
    }

    /// <summary>定时发送的发送动作（在后台线程执行，写日志时会自动切回界面线程）。</summary>
    private async ValueTask SendCurrentPayloadAsync(CancellationToken cancellationToken)
    {
        if (!TryBuildSendPayload(out var payload, out var error))
        {
            throw new InvalidOperationException(error);
        }

        await WriteAndRecordAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteAndRecordAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("串口尚未打开。");
        }

        // 先取时间：回环时数据可能在本机写完成之前就回来了，
        // 用发送前的时刻记 Tx 行，日志顺序才符合直觉（Tx 在回显的 Rx 之前）
        var sentAt = DateTime.Now;

        await _transport.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _txBytes, payload.Length);
        RecordTransmitted(payload, sentAt);
    }

    /// <summary>发送文件时的分块写出。逐块写日志会瞬间刷屏，因此只在开始/结束时留系统提示。</summary>
    private async ValueTask WriteFileChunkAsync(byte[] chunk, CancellationToken cancellationToken)
    {
        await _transport.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _txBytes, chunk.Length);
    }

    private bool TryBuildSendPayload(out byte[] payload, out string error)
        => SendPayloadBuilder.TryBuild(
            SendText,
            SendHex,
            SelectedLineEnding?.Value ?? LineEnding.CrLf,
            SelectedEncodingName,
            out payload,
            out error);

    private bool TryGetTimedSendInterval(out double seconds, out string error)
    {
        error = string.Empty;

        if (!double.TryParse(TimedSendIntervalText?.Trim(), out seconds) || seconds <= 0)
        {
            seconds = 0;
            error = "间隔必须是大于 0 的数字（秒）。";
            return false;
        }

        if (seconds < 0.05)
        {
            seconds = 0.05;
        }

        return true;
    }

    private double GetTimedSendSeconds()
        => TryGetTimedSendInterval(out var seconds, out _) ? seconds : 1.0;

    private void OnTimedSendFailed(object? sender, Exception exception)
    {
        _dispatcher.InvokeAsync(() =>
        {
            if (TimedSendEnabled)
            {
                TimedSendEnabled = false;
            }

            var message = exception is SerialLinkException serial ? serial.Message : exception.Message;
            StatusDetail = "定时发送已停止。";
            AddSystemLine($"定时发送失败已停止：{message}");
            _notifier.ShowError("定时发送失败", message);
        });
    }

    // ---------- 接收设置联动 ----------

    partial void OnHexDisplayChanged(bool value) => ApplyReceiveOptions();

    partial void OnSelectedFramingChanged(Choice<FramingMode>? value) => ApplyReceiveOptions();

    partial void OnAutoFrameGapTextChanged(string value) => ScheduleReceiveOptionsApply();

    partial void OnDelimiterTextChanged(string value) => ScheduleReceiveOptionsApply();

    partial void OnSelectedEncodingNameChanged(string value) => ApplyReceiveOptions();

    private void ScheduleReceiveOptionsApply()
    {
        _receiveOptionsDebounce.Stop();
        _receiveOptionsDebounce.Start();
    }

    private ReceiveOptions BuildReceiveOptions() => new()
    {
        Framing = SelectedFraming?.Value ?? FramingMode.Gap,
        AutoFrameGapMilliseconds = int.TryParse(AutoFrameGapText?.Trim(), out var gap) ? gap : 20,
        DelimiterText = DelimiterText,
        HexDisplay = HexDisplay,
        EncodingName = SelectedEncodingName,
        ShowTimestamp = ShowTimestamp,
        StripAnsi = TerminalMode,
    };

    private void ApplyReceiveOptions()
    {
        var options = BuildReceiveOptions();

        if (options.Framing == FramingMode.Delimiter
            && !BytePatternParser.TryParse(options.DelimiterText, out _, out var delimiterError))
        {
            StatusDetail = $"分隔符写法无效（{delimiterError}）已临时按空闲间隔断帧。";
        }

        List<DisplayLine>? flushed = null;

        lock (_pipelineLock)
        {
            _scratch.Clear();
            _processor.ApplyOptions(options, DateTime.Now, _scratch);

            if (_scratch.Count > 0)
            {
                flushed = [.. _scratch];
            }
        }

        if (flushed is not null)
        {
            AppendToLog(flushed);
        }

        // 只有显示方式变化才需要重刷历史行；断帧参数变化不影响已有内容
        var reformatNeeded = options.HexDisplay != _appliedOptions.HexDisplay
            || !string.Equals(options.EncodingName, _appliedOptions.EncodingName, StringComparison.OrdinalIgnoreCase);

        _appliedOptions = options;

        if (reformatNeeded)
        {
            Log.Reformat(_processor.Reformat);
        }
    }

    // ---------- 配置 ----------

    public bool TryBuildSettings(out SerialSettings settings, out string error)
    {
        settings = new SerialSettings();
        error = string.Empty;

        if (SelectedPort is null)
        {
            error = "请先选择串口。";
            return false;
        }

        if (!int.TryParse(BaudRateText?.Trim(), out var baudRate) || baudRate <= 0)
        {
            error = "波特率必须是大于 0 的整数。";
            return false;
        }

        settings = new SerialSettings
        {
            PortName = SelectedPort.PortName,
            BaudRate = baudRate,
            DataBits = DataBits,
            Parity = SelectedParity?.Value ?? Parity.None,
            StopBits = SelectedStopBits?.Value ?? StopBits.One,
            Handshake = SelectedHandshake?.Value ?? Handshake.None,
            DtrEnable = DtrEnable,
            RtsEnable = RtsEnable,
        };

        try
        {
            settings.Validate();
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void PersistSettings()
    {
        _settings.Theme = _themeService.Current;
        _settings.AutoScroll = AutoScroll;
        _settings.HexDisplay = HexDisplay;
        _settings.EncodingName = SelectedEncodingName;
        _settings.Framing = SelectedFraming?.Value ?? FramingMode.Gap;
        _settings.AutoFrameGapMilliseconds = int.TryParse(AutoFrameGapText?.Trim(), out var gap) ? gap : 20;
        _settings.DelimiterText = DelimiterText;
        _settings.SendHex = SendHex;
        _settings.SendLineEnding = SelectedLineEnding?.Value ?? LineEnding.CrLf;
        _settings.TimedSendIntervalSeconds = GetTimedSendSeconds();
        _settings.ShowTimestamp = ShowTimestamp;
        _settings.LineWrap = LineWrap;
        _settings.LogFontSize = LogFontSize;

        _settings.AutoReconnect = AutoReconnect;
        _settings.AutoOpenOnStartup = AutoOpenOnStartup;
        _settings.BlurBackground = BlurBackground;
        _settings.TerminalLocalEcho = TerminalLocalEcho;
        _settings.TerminalBackspaceSendsDel = TerminalBackspaceSendsDel;
        _settings.SaveLogToFile = SaveLogToFile;
        _settings.SaveRawLog = SaveRawLog;
        _settings.LogDirectory = LogDirectory;
        _settings.Presets = [.. Presets];
        _settings.SearchFavorites = [.. SearchFavorites];

        if (TryBuildSettings(out var serial, out _))
        {
            _settings.LastSerial = serial;
        }

        _settingsStore.Save(_settings);
    }

    private void ApplySettingsToUi(AppSettings settings)
    {
        var serial = settings.LastSerial;

        BaudRateText = serial.BaudRate.ToString();
        DataBits = DataBitOptions.Contains(serial.DataBits) ? serial.DataBits : 8;
        SelectedParity = ParityOptions.FirstOrDefault(o => o.Value == serial.Parity) ?? ParityOptions[0];
        SelectedStopBits = StopBitsOptions.FirstOrDefault(o => o.Value == serial.StopBits) ?? StopBitsOptions[0];
        SelectedHandshake = HandshakeOptions.FirstOrDefault(o => o.Value == serial.Handshake) ?? HandshakeOptions[0];
        DtrEnable = serial.DtrEnable;
        RtsEnable = serial.RtsEnable;

        HexDisplay = settings.HexDisplay;
        SelectedFraming = FramingOptions.FirstOrDefault(o => o.Value == settings.Framing) ?? FramingOptions[0];
        AutoFrameGapText = settings.AutoFrameGapMilliseconds.ToString();
        DelimiterText = settings.DelimiterText;
        SelectedEncodingName = EncodingOptions.Contains(settings.EncodingName) ? settings.EncodingName : "UTF-8";
        ShowTimestamp = settings.ShowTimestamp;
        AutoScroll = settings.AutoScroll;
        LineWrap = settings.LineWrap;
        LogFontSize = settings.LogFontSize < 9 ? 13 : settings.LogFontSize;

        SendHex = settings.SendHex;
        SelectedLineEnding = LineEndingOptions.FirstOrDefault(o => o.Value == settings.SendLineEnding) ?? LineEndingOptions[0];
        TimedSendIntervalText = settings.TimedSendIntervalSeconds.ToString("0.##", CultureInfo.InvariantCulture);

        // 日志目录要先于保存开关设置，因为开关变化会立刻用到目录
        LogDirectory = string.IsNullOrWhiteSpace(settings.LogDirectory)
            ? LogSessionOptions.DefaultDirectory
            : settings.LogDirectory;
        SaveRawLog = settings.SaveRawLog;
        SaveLogToFile = settings.SaveLogToFile;

        AutoReconnect = settings.AutoReconnect;
        AutoOpenOnStartup = settings.AutoOpenOnStartup;
        BlurBackground = settings.BlurBackground;
        TerminalLocalEcho = settings.TerminalLocalEcho;
        TerminalBackspaceSendsDel = settings.TerminalBackspaceSendsDel;

        // 预设只加载不套用：启动时的连接参数来自 LastSerial
        Presets.Clear();
        foreach (var preset in settings.Presets)
        {
            Presets.Add(preset);
        }

        // 收藏顺手清一遍脏数据（手改过配置文件时可能有空白项或重复项）
        SyncSearchFavorites(SearchFavoriteList.Sanitize(settings.SearchFavorites));
        _settings.SearchFavorites = [.. SearchFavorites];
        NotifyFavoritesChanged();
    }

    private void UpdateThemeButtonText()
    {
        ThemeButtonText = _themeService.IsDarkEffective ? "切换到浅色" : "切换到深色";
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    // ---------- 传输层事件 ----------

    private void OnBytesReceived(object? sender, BytesReceivedEventArgs e)
    {
        // 这里在串口读取线程上：只做计数与入管线，绝不碰 UI
        Interlocked.Add(ref _rxBytes, e.Length);

        lock (_pipelineLock)
        {
            _processor.ProcessReceived(e.Data, e.Timestamp, DateTime.Now, _pendingLines);
        }
    }

    private void OnTransportStateChanged(object? sender, TransportStateChangedEventArgs e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            State = e.NewState;
            StatusText = e.NewState.ToDisplayText();

            if (!string.IsNullOrWhiteSpace(e.Message))
            {
                StatusDetail = e.Message.Split('\n')[0];
            }

            switch (e.NewState)
            {
                case TransportState.Faulted when !string.IsNullOrWhiteSpace(e.Message):
                    AddSystemLine($"链路中断：{StatusDetail}");

                    // 自动重连会接管：只做非阻塞提示。
                    // 这里绝不能弹模态框——它挡住界面、也让用户以为程序卡死了，而他其实什么都不用做。
                    if (AutoReconnect)
                    {
                        AddSystemLine("已开启自动重连：设备插回后会自动重新打开串口。");
                    }
                    else
                    {
                        _notifier.ShowError("串口连接中断", e.Message);
                    }

                    break;

                case TransportState.Open:
                    // 重连成功也要把上一次的故障说明换掉，否则状态栏会停在"链路故障"的旧文案上
                    StatusDetail = _transport.CurrentSettings?.ToShortDescription() ?? StatusDetail;
                    break;

                case TransportState.Closed when e.OldState != TransportState.Closed:
                    AddSystemLine("串口已关闭。");
                    break;
            }
        });
    }

    // ---------- 界面刷新 ----------

    private void OnUiRefresh()
    {
        List<DisplayLine>? batch = null;

        lock (_pipelineLock)
        {
            _scratch.Clear();
            _processor.FlushIdle(Stopwatch.GetTimestamp(), DateTime.Now, _scratch);

            if (_scratch.Count > 0)
            {
                _pendingLines.AddRange(_scratch);
            }

            if (_pendingLines.Count > 0)
            {
                batch = [.. _pendingLines];
                _pendingLines.Clear();
            }
        }

        if (batch is not null)
        {
            if (IsPaused)
            {
                BufferWhilePaused(batch);
            }
            else
            {
                AppendToLog(batch);
            }
        }

        RxText = ByteSize.Format(Interlocked.Read(ref _rxBytes));
        TxText = ByteSize.Format(Interlocked.Read(ref _txBytes));

        if (TimedSendEnabled)
        {
            TimedSendStatusText = $"定时发送中：已发送 {_timedSender.SentCount} 次";
        }

        var logger = _sessionLogger;

        if (logger is not null)
        {
            var path = logger.TextFilePath ?? logger.RawFilePath;
            var name = path is null ? logger.Directory : Path.GetFileName(path);

            LogStatusText = logger.DroppedRecords > 0
                ? $"日志 {name}：已写入 {ByteSize.Format(logger.WrittenBytes)}，丢弃 {logger.DroppedRecords} 条（磁盘跟不上）"
                : $"日志 {name}：已写入 {ByteSize.Format(logger.WrittenBytes)}";
        }
    }

    private void AppendToLog(List<DisplayLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        Log.Append(lines);

        var logger = _sessionLogger;

        if (logger is not null)
        {
            foreach (var line in lines)
            {
                logger.Log(line);
            }
        }

        if (!AutoScroll)
        {
            PendingNewLines += lines.Count;
        }
    }

    private void BufferWhilePaused(List<DisplayLine> lines)
    {
        _pausedBuffer.AddRange(lines);

        if (_pausedBuffer.Count > MaxPausedBufferLines)
        {
            var overflow = _pausedBuffer.Count - MaxPausedBufferLines;
            _pausedBuffer.RemoveRange(0, overflow);
            PausedHintText = $"已暂停显示（缓冲已满，最早的 {overflow} 行被丢弃）";
            return;
        }

        PausedHintText = $"已暂停显示（{_pausedBuffer.Count} 行待显示）";
    }

    private void RecordTransmitted(byte[] payload, DateTime sentAt)
    {
        // 定时发送在后台线程触发，而写日志要碰绑定到界面的集合，必须切回界面线程
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => RecordTransmitted(payload, sentAt));
            return;
        }

        List<DisplayLine> lines = [];

        lock (_pipelineLock)
        {
            _processor.ProcessTransmitted(payload, sentAt, lines);
        }

        if (IsPaused)
        {
            BufferWhilePaused(lines);
        }
        else
        {
            AppendToLog(lines);
        }
    }

    private void AddSystemLine(string message)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => AddSystemLine(message));
            return;
        }

        List<DisplayLine> lines = [];

        lock (_pipelineLock)
        {
            _processor.ProcessSystem(message, DateTime.Now, lines);
        }

        if (IsPaused)
        {
            BufferWhilePaused(lines);
        }
        else
        {
            AppendToLog(lines);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _uiTimer.Stop();
        _receiveOptionsDebounce.Stop();
        _sendFileCts?.Cancel();
        _sendFileCts?.Dispose();

        _timedSender.SendFailed -= OnTimedSendFailed;
        await _timedSender.DisposeAsync().ConfigureAwait(false);

        _reconnect.StatusChanged -= OnReconnectStatusChanged;
        await _reconnect.DisposeAsync().ConfigureAwait(false);

        var logger = _sessionLogger;
        _sessionLogger = null;

        if (logger is not null)
        {
            await logger.DisposeAsync().ConfigureAwait(false);
        }

        _transport.StateChanged -= OnTransportStateChanged;
        _transport.BytesReceived -= OnBytesReceived;
        await _transport.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
