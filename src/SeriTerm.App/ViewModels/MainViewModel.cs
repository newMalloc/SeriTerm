using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SeriTerm.App.Common;
using SeriTerm.App.Services;
using SeriTerm.Core.Serial;

namespace SeriTerm.App.ViewModels;

/// <summary>
/// 主窗口视图模型。M1 范围：串口枚举、参数配置、打开/关闭、收发字节计数，
/// 以及一个最小的文本发送入口（正式的 HEX / 行尾 / 定时发送在 M4 补齐）。
/// </summary>
public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISerialTransport _transport;
    private readonly PortFriendlyNameProvider _friendlyNames;
    private readonly IUserNotifier _notifier;
    private readonly ISettingsStore _settingsStore;
    private readonly IThemeService _themeService;
    private readonly ILogger<MainViewModel> _logger;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;

    /// <summary>计数器用 Interlocked 累加，避免读线程每收一块数据就跨线程刷 UI。</summary>
    private long _rxBytes;
    private long _txBytes;

    private readonly DispatcherTimer _counterTimer;

    public MainViewModel(
        ISerialTransport transport,
        PortFriendlyNameProvider friendlyNames,
        IUserNotifier notifier,
        ISettingsStore settingsStore,
        IThemeService themeService,
        ILogger<MainViewModel> logger)
    {
        _transport = transport;
        _friendlyNames = friendlyNames;
        _notifier = notifier;
        _settingsStore = settingsStore;
        _themeService = themeService;
        _logger = logger;
        _settings = settingsStore.Load();
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        Ports = [];
        BaudRateOptions = SerialSettings.CommonBaudRates;
        DataBitOptions = SerialSettings.CommonDataBits;
        ParityOptions = [.. SerialSettings.ParityOptions.Select(o => new Choice<Parity>(o.Value, o.Display))];
        StopBitsOptions = [.. SerialSettings.StopBitsOptions.Select(o => new Choice<StopBits>(o.Value, o.Display))];
        HandshakeOptions = [.. SerialSettings.HandshakeOptions.Select(o => new Choice<Handshake>(o.Value, o.Display))];

        _transport.StateChanged += OnTransportStateChanged;
        _transport.BytesReceived += OnBytesReceived;

        // 200 ms 批量刷新一次计数，而不是每块数据刷一次
        _counterTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _counterTimer.Tick += (_, _) => FlushCounters();
        _counterTimer.Start();

        ApplySettingsToUi(_settings);
        State = _transport.State;
        StatusText = State.ToDisplayText();
        UpdateThemeButtonText();
    }

    // ---------- 集合与选项 ----------

    public ObservableCollection<PortItem> Ports { get; }

    public IReadOnlyList<int> BaudRateOptions { get; }

    public IReadOnlyList<int> DataBitOptions { get; }

    public IReadOnlyList<Choice<Parity>> ParityOptions { get; }

    public IReadOnlyList<Choice<StopBits>> StopBitsOptions { get; }

    public IReadOnlyList<Choice<Handshake>> HandshakeOptions { get; }

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

    // ---------- 状态 ----------

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
    private string _sendText = "SeriTerm loopback test";

    public bool IsOpen => State == TransportState.Open;

    /// <summary>只有关闭或故障状态下才允许改参数。</summary>
    public bool CanEditSettings => State is TransportState.Closed or TransportState.Faulted;

    public string OpenButtonText => IsOpen ? "关闭" : "打开";

    // ---------- 命令 ----------

    /// <summary>启动时调用。</summary>
    public Task InitializeAsync() => RefreshPortsAsync();

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
            StatusDetail = "串口已关闭。";
            return;
        }

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

    /// <summary>
    /// M1 的最小发送入口：固定 UTF-8 + CRLF。M4 会替换为完整的
    /// 文本/HEX 切换、行尾可选、定时发送与文件发送。
    /// </summary>
    [RelayCommand]
    private async Task SendAsync()
    {
        if (!IsOpen)
        {
            _notifier.ShowInfo("提示", "请先打开串口。");
            return;
        }

        var payload = Encoding.UTF8.GetBytes(SendText + "\r\n");

        try
        {
            await _transport.WriteAsync(payload).ConfigureAwait(true);
            Interlocked.Add(ref _txBytes, payload.Length);
        }
        catch (SerialLinkException ex)
        {
            _notifier.ShowError("发送失败", ex.Message);
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

    public AppSettings Settings => _settings;

    /// <summary>窗口关闭时保存配置。</summary>
    public void PersistSettings()
    {
        _settings.Theme = _themeService.Current;

        if (TryBuildSettings(out var settings, out _))
        {
            _settings.LastSerial = settings;
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
    }

    private void UpdateThemeButtonText()
        => ThemeButtonText = _themeService.IsDarkEffective ? "切换到浅色" : "切换到深色";

    // ---------- 传输层事件 ----------

    private void OnBytesReceived(object? sender, BytesReceivedEventArgs e)
    {
        // 只做计数：这里在串口读取线程上，绝不能碰 UI
        Interlocked.Add(ref _rxBytes, e.Length);
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

            if (e.NewState == TransportState.Faulted && !string.IsNullOrWhiteSpace(e.Message))
            {
                _notifier.ShowError("串口连接中断", e.Message);
            }
        });
    }

    private void FlushCounters()
    {
        RxText = ByteSize.Format(Interlocked.Read(ref _rxBytes));
        TxText = ByteSize.Format(Interlocked.Read(ref _txBytes));
    }

    public async ValueTask DisposeAsync()
    {
        _counterTimer.Stop();
        _transport.StateChanged -= OnTransportStateChanged;
        _transport.BytesReceived -= OnBytesReceived;
        await _transport.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
