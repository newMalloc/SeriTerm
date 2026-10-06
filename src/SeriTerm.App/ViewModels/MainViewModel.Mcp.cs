using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeriTerm.App.Common;
using SeriTerm.App.Services;
using SeriTerm.App.Services.Mcp;
using SeriTerm.Core.Mcp;
using SeriTerm.Core.Framing;
using SeriTerm.Core.Pipeline;
using SeriTerm.Core.Serial;
using SeriTerm.Core.Text;

namespace SeriTerm.App.ViewModels;

/// <summary>
/// 主视图模型的 AI 接入（MCP）部分。
///
/// 拆成单独一个 partial 文件的原因很直接：<see cref="IMcpSession"/> 的实现要读一堆私有字段
/// （传输层、日志、计数器），做成独立类就得把它们全公开；而这些恰恰是"只给 AI 用"的表面，
/// 公开出去只会让主文件更难读。
///
/// 线程约定：串口动作本身不占界面线程（传输层有自己的读取线程），但**读写界面状态必须回界面线程**，
/// 所以下面统一用 <c>OnUiAsync</c> 取快照、写状态；帧缓冲自己带锁，可以在管道线程上直接读。
/// </summary>
public partial class MainViewModel : IMcpSession
{
    /// <summary>连接断掉多久之后才在日志里说"AI 已断开"。客户端是"一次调用连一次"，
    /// 不留这段时间的话，每次工具调用都会刷出"已连接/已断开"两行。</summary>
    private static readonly TimeSpan McpIdleAuditDelay = TimeSpan.FromSeconds(30);

    /// <summary>同一条 AI 提示在这个间隔内只写一次（跑偏的模型会连着重试同一个被拒的动作）。</summary>
    private static readonly TimeSpan AuditRepeatSuppression = TimeSpan.FromSeconds(10);

    private readonly FrameJournal _mcpJournal = new();
    private readonly object _auditGate = new();

    private McpControlServer? _mcpServer;
    private DispatcherTimer? _mcpIdleTimer;
    private McpLinkError? _lastLinkError;
    private string _lastAuditMessage = string.Empty;
    private DateTime _lastAuditAt = DateTime.MinValue;
    private bool _mcpSessionAudited;

    // ---------- 界面上的「AI 接入」面板 ----------

    /// <summary>MCP 服务总开关（默认开）。关掉就没人能连进来。</summary>
    [ObservableProperty]
    private bool _mcpEnabled = true;

    /// <summary>是否给 AI 完全权限（可发送/开关串口）。默认关，即只读。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpPermissionText))]
    private bool _mcpFullPermission;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpClientText))]
    private int _mcpClientCount;

    /// <summary>启动失败的原因（管道被占、权限不足等）；空表示正常。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpServiceStateText))]
    private string _mcpStartupError = string.Empty;

    public string McpPermissionText => McpFullPermission ? "完全权限" : "只读";

    public string McpClientText => McpClientCount > 0
        ? $"已接入 {McpClientCount} 个 AI 客户端"
        : "暂无 AI 客户端";

    public string McpPipeName => McpProtocol.PipeName;

    public string McpServiceStateText
    {
        get
        {
            if (!McpEnabled)
            {
                return "未启用";
            }

            if (!string.IsNullOrEmpty(McpStartupError))
            {
                return $"启动失败：{McpStartupError}";
            }

            return McpFullPermission
                ? "监听中 · 完全权限（AI 可以发送数据）"
                : "监听中 · 只读（AI 只能看）";
        }
    }

    /// <summary>给 AI 客户端用的配置片段（Claude Desktop / Cursor / VS Code 都能直接粘）。</summary>
    public string McpClientConfigJson
    {
        get
        {
            var exe = Environment.ProcessPath ?? "SeriTerm.exe";

            var config = new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["seriterm"] = new JsonObject
                    {
                        ["command"] = exe,
                        ["args"] = new JsonArray { "--mcp-stdio" },
                    },
                },
            };

            return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
    }

    [RelayCommand]
    private void CopyMcpConfig()
        => ReportCopyResult("MCP 配置", ClipboardText.TrySet(McpClientConfigJson));

    partial void OnMcpEnabledChanged(bool value)
    {
        if (value)
        {
            _mcpServer?.Start();
        }
        else
        {
            _ = _mcpServer?.StopAsync();
        }

        McpStartupError = string.Empty;
        OnPropertyChanged(nameof(McpServiceStateText));
    }

    partial void OnMcpFullPermissionChanged(bool value)
    {
        // 启动时回填配置也会走到这里（那时控制面还没建好），不该在日志里留一行"权限已改"
        if (_mcpServer is not null)
        {
            AddSystemLine(value
                ? "[AI] 权限已改为完全权限：AI 现在可以打开/关闭串口并发送数据（发送有速率与长度限制，且每条都会记在这里）。"
                : "[AI] 权限已改回只读：AI 不能再发送任何数据。");
        }

        OnPropertyChanged(nameof(McpServiceStateText));
    }

    // ---------- 生命周期 ----------

    /// <summary>在构造函数末尾调用：建好控制面并按配置决定是否开始监听。</summary>
    private void InitializeMcp()
    {
        _mcpIdleTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = McpIdleAuditDelay,
        };

        _mcpIdleTimer.Tick += (_, _) =>
        {
            _mcpIdleTimer.Stop();

            if (_mcpSessionAudited && McpClientCount == 0)
            {
                _mcpSessionAudited = false;
                AddSystemLine("[AI] 已断开。");
            }
        };

        _mcpServer = new McpControlServer(this, _logger);
        _mcpServer.ClientCountChanged += OnMcpClientCountChanged;
        _mcpServer.Failed += OnMcpServerFailed;

        if (McpEnabled)
        {
            _mcpServer.Start();
        }

        OnPropertyChanged(nameof(McpServiceStateText));
    }

    private Task ShutdownMcpAsync()
    {
        _mcpIdleTimer?.Stop();

        if (_mcpServer is not null)
        {
            _mcpServer.ClientCountChanged -= OnMcpClientCountChanged;
            _mcpServer.Failed -= OnMcpServerFailed;
            return _mcpServer.StopAsync();
        }

        return Task.CompletedTask;
    }

    private void OnMcpServerFailed(object? sender, string message)
        => _dispatcher.InvokeAsync(() =>
        {
            McpStartupError = message;
            OnPropertyChanged(nameof(McpServiceStateText));
            AddSystemLine($"[AI] MCP 服务启动失败：{message}");
        });

    private void OnMcpClientCountChanged(object? sender, int count)
        => _dispatcher.InvokeAsync(() =>
        {
            McpClientCount = count;

            if (count > 0)
            {
                _mcpIdleTimer?.Stop();
                _mcpIdleTimer?.Start();

                if (!_mcpSessionAudited)
                {
                    _mcpSessionAudited = true;
                    AddSystemLine($"[AI] 已接入（权限：{McpPermissionText}）。");
                }
            }
        });

    /// <summary>日志区每次成行时同步一份给 AI 读的帧缓冲（暂停显示也照收）。</summary>
    private void PublishToJournal(List<DisplayLine> lines) => _mcpJournal.Append(lines);

    // ---------- IMcpSession ----------

    public McpPermission Permission => McpFullPermission ? McpPermission.Full : McpPermission.ReadOnly;

    public string EncodingName => string.IsNullOrWhiteSpace(SelectedEncodingName) ? "UTF-8" : SelectedEncodingName;

    public McpLinkError? LastError => Volatile.Read(ref _lastLinkError);

    public Task<IReadOnlyList<McpPortInfo>> ListPortsAsync(CancellationToken cancellationToken)
        => OnUiAsync<IReadOnlyList<McpPortInfo>>(() =>
            [.. Ports.Select(port => new McpPortInfo(port.PortName, port.Display, ReferenceEquals(port, SelectedPort)))]);

    public Task<McpStatus> GetStatusAsync(CancellationToken cancellationToken) => OnUiAsync(BuildStatus);

    public Task<McpFramePage> ReadFramesAsync(McpFrameQuery query, CancellationToken cancellationToken)
    {
        var (frames, cursor, hasMore) = _mcpJournal.Read(query.Since, query.Limit, query.Direction);
        var encoding = StatefulTextDecoder.Resolve(EncodingName);
        var described = new List<McpFrameInfo>(frames.Count);

        foreach (var frame in frames)
        {
            described.Add(DescribeFrame(frame, encoding, query.IncludeHex, query.IncludeText, query.MaxChars));
        }

        return Task.FromResult(new McpFramePage(
            described,
            cursor,
            _mcpJournal.OldestSeq,
            _mcpJournal.TotalFrames,
            _mcpJournal.EvictedFrames,
            hasMore));
    }

    public async Task<McpPatternResult> WaitForPatternAsync(McpPatternQuery query, CancellationToken cancellationToken)
    {
        if (!McpPatternMatcher.TryCreate(query, out var matcher, out var error) || matcher is null)
        {
            throw new McpArgumentException(error);
        }

        var encoding = StatefulTextDecoder.Resolve(EncodingName);
        var cursor = query.Since < 0 ? _mcpJournal.LastSeq : query.Since;
        var deadline = DateTime.UtcNow.AddMilliseconds(query.TimeoutMilliseconds);

        var output = new List<JournalFrame>();
        var context = new Queue<JournalFrame>();
        var added = new HashSet<long>();
        var matchCount = 0;
        var scanned = 0;
        long lastMatchSeq = cursor;

        while (true)
        {
            var (frames, nextCursor, _) = _mcpJournal.Read(cursor, 500, query.Direction);

            foreach (var frame in frames)
            {
                scanned++;

                var payload = frame.Payload;
                var text = matcher.NeedsText ? McpPatternMatcher.Decode(payload, encoding) : string.Empty;

                if (matcher.IsMatch(payload, text))
                {
                    // 命中前的上下文帧：方便模型理解"这条消息前面发生了什么"
                    foreach (var item in context)
                    {
                        if (added.Add(item.Seq))
                        {
                            output.Add(item);
                        }
                    }

                    if (added.Add(frame.Seq))
                    {
                        output.Add(frame);
                    }

                    matchCount++;
                    lastMatchSeq = frame.Seq;

                    if (matchCount >= query.MaxMatches)
                    {
                        return BuildPatternResult(output, matched: true, timedOut: false, scanned, lastMatchSeq, encoding, query.MaxChars);
                    }
                }

                if (query.ContextFrames > 0)
                {
                    context.Enqueue(frame);

                    while (context.Count > query.ContextFrames)
                    {
                        context.Dequeue();
                    }
                }
            }

            cursor = nextCursor;

            var remaining = deadline - DateTime.UtcNow;

            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            // 等新帧（或等到超时）。返回 false 不要紧：下一轮会先扫一遍再判超时。
            await _mcpJournal.WaitForNewAsync(cursor, remaining, cancellationToken).ConfigureAwait(false);
        }

        return BuildPatternResult(
            output,
            matched: matchCount > 0,
            timedOut: matchCount == 0,
            scanned,
            matchCount > 0 ? lastMatchSeq : cursor,
            encoding,
            query.MaxChars);
    }

    public async Task<McpStatus> OpenAsync(McpOpenRequest request, CancellationToken cancellationToken)
    {
        // 1) 在界面线程上把"界面当前设置 + 本次覆盖"合成一份参数
        var settings = await OnUiAsync(() => BuildOpenSettings(request)).ConfigureAwait(false);

        // 2) 打开（传输层自己管线程，界面线程不参与）
        await _reconnect.StopAsync().ConfigureAwait(false);
        await _transport.OpenAsync(settings, cancellationToken).ConfigureAwait(false);

        // 3) 回界面线程：界面显示要跟实际连接一致，否则"界面说谎"
        await OnUiAsync(() =>
        {
            ApplyOpenedSettingsToUi(settings);
            StatusDetail = settings.ToShortDescription();
            AddSystemLine($"[AI] 已打开串口：{settings.ToShortDescription()}");
        }).ConfigureAwait(false);

        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpStatus> CloseAsync(CancellationToken cancellationToken)
    {
        await _reconnect.StopAsync().ConfigureAwait(false);
        await _transport.CloseAsync().ConfigureAwait(false);
        await OnUiAsync(() => AddSystemLine("[AI] 已关闭串口。")).ConfigureAwait(false);

        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpStatus> SetBaudRateAsync(int baudRate, CancellationToken cancellationToken)
    {
        await _transport.SetBaudRateAsync(baudRate, cancellationToken).ConfigureAwait(false);

        await OnUiAsync(() =>
        {
            BaudRateText = baudRate.ToString(CultureInfo.InvariantCulture);
            AddSystemLine($"[AI] 波特率已改为 {baudRate}，串口保持打开。");
        }).ConfigureAwait(false);

        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> WriteAsync(McpWriteRequest request, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("串口尚未打开，无法发送。");
        }

        await WriteAndRecordAsync(request.Payload, cancellationToken).ConfigureAwait(false);

        // 审计行：用户必须能一眼看出"这条数据不是我自己发的"
        await OnUiAsync(() => AddSystemLine($"[AI] 发送 {request.Payload.Length} 字节：{request.Preview}")).ConfigureAwait(false);

        return request.Payload.Length;
    }

    /// <summary>写一条"来自 AI"的提示。同一条内容短时间内重复出现时只写一次，避免被重试刷屏。</summary>
    public void Audit(string message)
    {
        lock (_auditGate)
        {
            var now = DateTime.UtcNow;

            if (string.Equals(message, _lastAuditMessage, StringComparison.Ordinal)
                && now - _lastAuditAt < AuditRepeatSuppression)
            {
                return;
            }

            _lastAuditMessage = message;
            _lastAuditAt = now;
        }

        AddSystemLine(message);
    }

    // ---------- 内部实现 ----------

    private McpStatus BuildStatus()
    {
        var current = _transport.CurrentSettings;
        var baudRate = current?.BaudRate
            ?? (int.TryParse(BaudRateText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null);

        return new McpStatus(
            Open: IsOpen,
            PortName: current?.PortName ?? SelectedPort?.PortName,
            PortDisplayName: SelectedPort?.Display,
            BaudRate: baudRate,
            DataBits: current?.DataBits ?? DataBits,
            Parity: SerialSettings.ParityText(current?.Parity ?? SelectedParity?.Value ?? Parity.None),
            StopBits: SerialSettings.StopBitsText(current?.StopBits ?? SelectedStopBits?.Value ?? StopBits.One),
            Handshake: SerialSettings.HandshakeText(current?.Handshake ?? SelectedHandshake?.Value ?? Handshake.None),
            DtrEnable: current?.DtrEnable ?? DtrEnable,
            RtsEnable: current?.RtsEnable ?? RtsEnable,
            RxBytes: Interlocked.Read(ref _rxBytes),
            TxBytes: Interlocked.Read(ref _txBytes),
            EncodingName: EncodingName,
            FramingMode: (SelectedFraming?.Value ?? FramingMode.Gap).ToString(),
            AutoFrameGapMilliseconds: int.TryParse(AutoFrameGapText?.Trim(), out var gap) ? gap : 20,
            DelimiterText: DelimiterText ?? string.Empty,
            TimestampEnabled: ShowTimestamp,
            ReconnectStatus: ReconnectStatusText ?? string.Empty,
            AutoReconnect: AutoReconnect,
            JournalFrames: _mcpJournal.Count,
            JournalEvictedFrames: _mcpJournal.EvictedFrames,
            LogDroppedLines: Log.DroppedLines,
            Permission: Permission,
            ConnectedClients: McpClientCount);
    }

    /// <summary>把本次请求合到界面当前设置上（只读界面状态，必须在界面线程调用）。</summary>
    private SerialSettings BuildOpenSettings(McpOpenRequest request)
    {
        if (!TryBuildSettings(out var current, out var error))
        {
            throw new McpArgumentException(error);
        }

        var settings = new SerialSettings
        {
            PortName = string.IsNullOrWhiteSpace(request.PortName) ? current.PortName : request.PortName.Trim(),
            BaudRate = request.BaudRate ?? current.BaudRate,
            DataBits = request.DataBits ?? current.DataBits,
            Parity = ParseOption(request.Parity, current.Parity, "parity"),
            StopBits = ParseOption(request.StopBits, current.StopBits, "stopBits"),
            Handshake = ParseOption(request.Handshake, current.Handshake, "handshake"),
            DtrEnable = request.DtrEnable ?? current.DtrEnable,
            RtsEnable = request.RtsEnable ?? current.RtsEnable,
        };

        settings.Validate();
        return settings;
    }

    /// <summary>打开成功后把界面上的端口/参数改成实际生效的那一套。</summary>
    private void ApplyOpenedSettingsToUi(SerialSettings settings)
    {
        SelectedPort = Ports.FirstOrDefault(p => string.Equals(p.PortName, settings.PortName, StringComparison.OrdinalIgnoreCase))
            ?? SelectedPort;

        BaudRateText = settings.BaudRate.ToString(CultureInfo.InvariantCulture);
        DataBits = settings.DataBits;
        SelectedParity = ParityOptions.FirstOrDefault(o => o.Value == settings.Parity) ?? SelectedParity;
        SelectedStopBits = StopBitsOptions.FirstOrDefault(o => o.Value == settings.StopBits) ?? SelectedStopBits;
        SelectedHandshake = HandshakeOptions.FirstOrDefault(o => o.Value == settings.Handshake) ?? SelectedHandshake;
        DtrEnable = settings.DtrEnable;
        RtsEnable = settings.RtsEnable;
    }

    private static TEnum ParseOption<TEnum>(string? text, TEnum fallback, string fieldName)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (!Enum.TryParse<TEnum>(text.Trim(), ignoreCase: true, out var value))
        {
            throw new McpArgumentException(
                $"参数 {fieldName} 的值 “{text}” 无效。可选：{string.Join('、', Enum.GetNames<TEnum>().Select(ToCamel))}。");
        }

        return value;
    }

    private static string ToCamel(string name)
        => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static McpFrameInfo DescribeFrame(JournalFrame frame, Encoding encoding, bool includeHex, bool includeText, int maxChars)
    {
        var payload = frame.Payload;
        string? hex = null;
        string? text = null;
        var truncated = false;

        if (includeHex)
        {
            // 一个字节写成 "FF " 是 3 个字符，按字符预算换算回来
            var maxBytes = Math.Max(1, maxChars / 3);
            var slice = payload.Length > maxBytes ? payload[..maxBytes] : payload;

            if (payload.Length > maxBytes)
            {
                truncated = true;
            }

            hex = HexCodec.Format(slice);
        }

        if (includeText)
        {
            var decoded = encoding.GetString(payload);

            if (decoded.Length > maxChars)
            {
                decoded = decoded[..maxChars];
                truncated = true;
            }

            text = decoded;
        }

        return new McpFrameInfo(
            frame.Seq,
            frame.Time,
            FrameJournal.ToProtocolText(frame.Direction),
            frame.Raw.Length,
            hex,
            text,
            truncated);
    }

    private static McpPatternResult BuildPatternResult(
        List<JournalFrame> frames,
        bool matched,
        bool timedOut,
        int scanned,
        long cursor,
        Encoding encoding,
        int maxChars)
    {
        var described = frames
            .Select(frame => DescribeFrame(frame, encoding, includeHex: true, includeText: true, maxChars))
            .ToList();

        return new McpPatternResult(matched, timedOut, scanned, cursor, described);
    }

    /// <summary>回到界面线程取值。</summary>
    private Task<T> OnUiAsync<T>(Func<T> func)
        => _dispatcher.CheckAccess() ? Task.FromResult(func()) : _dispatcher.InvokeAsync(func).Task;

    /// <summary>回到界面线程执行。</summary>
    private Task OnUiAsync(Action action)
        => _dispatcher.CheckAccess() ? RunInline(action) : _dispatcher.InvokeAsync(action).Task;

    private static Task RunInline(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
