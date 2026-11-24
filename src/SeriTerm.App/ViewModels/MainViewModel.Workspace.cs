using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SeriTerm.App.Common;
using SeriTerm.Core.Logging;
using SeriTerm.Core.Pipeline;
using SeriTerm.Core.Send;
using SeriTerm.Core.Serial;
using SeriTerm.Core.Text;

namespace SeriTerm.App.ViewModels;

public partial class MainViewModel
{
    private DispatcherTimer _portRefreshTimer = null!;
    private bool _refreshingPorts;
    private bool _switchingPort;
    private volatile bool _disposed;
    private int _timedSendRevision;
    private long _pausedBytes;
    private long _pausedDroppedLines;
    private long _rateTimestamp = Stopwatch.GetTimestamp();
    private long _previousRx;
    private long _previousTx;
    private string[] _availablePorts = [];

    public ObservableCollection<SendHistoryEntry> SendHistory { get; } = [];

    [ObservableProperty] private SendHistoryEntry? _selectedSendHistory;
    [ObservableProperty] private string _sendPreviewText = string.Empty;
    [ObservableProperty] private string _sendValidationText = string.Empty;
    [ObservableProperty] private bool _hasSendError;
    [ObservableProperty] private string _portHint = "正在检测串口设备…";
    [ObservableProperty] private string _rxRateText = "0 B/s";
    [ObservableProperty] private string _txRateText = "0 B/s";
    [ObservableProperty] private string _bufferStatusText = "0 行";
    [ObservableProperty] private string _receiveValidationText = string.Empty;

    public bool CanSend => !_disposed && IsOpen && !HasSendError && !IsSendingFile && !TimedSendEnabled;
    public bool CanStartFileSend => !_disposed && IsOpen && !IsSendingFile && !TimedSendEnabled;
    public bool CanToggleTimedSend => !_disposed && (TimedSendEnabled || (IsOpen && !IsSendingFile && !HasSendError));
    public bool CanToggleConnection => !_disposed && !_switchingPort && State != TransportState.Opening && (IsOpen || SelectedPort is not null);
    public bool CanSelectPort => !_disposed && !_switchingPort && State != TransportState.Opening;
    public string EmptyLogTitle => IsOpen ? "已连接，等待设备数据" : "从连接一台设备开始";
    public string EmptyLogHint => IsOpen ? "收到的数据会实时出现在这里。也可以在下方发送命令。"
        : "选择左侧串口和波特率，点击“打开串口”。插拔设备后列表会自动更新。";

    private void InitializeWorkspace()
    {
        UpdateSendPreview();
        _portRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _portRefreshTimer.Tick += async (_, _) => await UpdatePortsAsync(force: false);
    }

    private async Task UpdatePortsAsync(bool force)
    {
        if (_refreshingPorts || _switchingPort || _disposed) return;
        _refreshingPorts = true;
        try
        {
            var names = await Task.Run(PortEnumerator.GetPortNames);
            if (_disposed || (!force && names.SequenceEqual(_availablePorts, StringComparer.OrdinalIgnoreCase))) return;
            _friendlyNames.Invalidate();
            var friendly = await _friendlyNames.GetAsync();
            if (_disposed || _switchingPort) return;
            _availablePorts = names.ToArray();
            var previous = SelectedPort?.PortName ?? _settings.LastSerial.PortName;
            // A disappearing port must not silently redirect the next send to another device.
            var items = names.Select(name => new PortItem(name, friendly.GetValueOrDefault(name))).ToList();
            if (!string.IsNullOrWhiteSpace(previous) && !names.Contains(previous, StringComparer.OrdinalIgnoreCase))
                items.Insert(0, new PortItem(previous, "未连接"));
            Ports.Clear();
            foreach (var item in items) Ports.Add(item);
            SelectedPort = Ports.FirstOrDefault(p => string.Equals(p.PortName, previous, StringComparison.OrdinalIgnoreCase))
                ?? Ports.FirstOrDefault();
            PortHint = names.Count == 0 ? "未检测到设备 · 请插入 USB 串口并检查驱动"
                : $"检测到 {names.Count} 个串口 · 插拔自动刷新";
            if (force && !IsOpen) StatusDetail = PortHint;
        }
        finally { _refreshingPorts = false; }
    }

    partial void OnSelectedPortChanged(PortItem? value) => ToggleOpenCommand.NotifyCanExecuteChanged();

    public async Task SwitchOpenPortAsync(PortItem port)
    {
        if (_disposed || _refreshingPorts || _switchingPort || !IsOpen ||
            _transport.CurrentSettings is not { } current ||
            string.Equals(current.PortName, port.PortName, StringComparison.OrdinalIgnoreCase)) return;

        _switchingPort = true;
        OnPropertyChanged(nameof(CanSelectPort));
        ToggleOpenCommand.NotifyCanExecuteChanged();
        try
        {
            await _reconnect.StopAsync().ConfigureAwait(true);
            await _transport.CloseAsync().ConfigureAwait(true);
            var settings = current with { PortName = port.PortName };
            await _transport.OpenAsync(settings).ConfigureAwait(true);
            _settings.LastSerial = settings;
            StatusDetail = settings.ToShortDescription();
            AddSystemLine($"串口已切换：{settings.ToShortDescription()}");
        }
        catch (Exception ex)
        {
            StatusDetail = $"切换到 {port.PortName} 失败。";
            AddSystemLine($"切换到 {port.PortName} 失败：{ex.Message}");
            _logger.LogError(ex, "切换串口到 {PortName} 失败", port.PortName);
            _notifier.ShowError($"切换到 {port.PortName} 失败", ex.Message);
        }
        finally
        {
            _switchingPort = false;
            OnPropertyChanged(nameof(CanSelectPort));
            ToggleOpenCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnStateChanged(TransportState value)
    {
        OnPropertyChanged(nameof(CanSelectPort));
        NotifySendAvailability();
        ToggleOpenCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(EmptyLogTitle));
        OnPropertyChanged(nameof(EmptyLogHint));
    }

    partial void OnIsSendingFileChanged(bool value) => NotifySendAvailability();
    partial void OnSendTextChanged(string value) => UpdateSendPreview();
    partial void OnSendHexChanged(bool value) => UpdateSendPreview();
    partial void OnSelectedLineEndingChanged(Choice<LineEnding>? value) => UpdateSendPreview();

    private void NotifySendAvailability()
    {
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
        SendFileCommand.NotifyCanExecuteChanged();
        ToggleTimedSendCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSendPreview()
    {
        HasSendError = !TryBuildSendPayload(out var bytes, out var error);
        SendValidationText = HasSendError ? error : $"{bytes.Length:N0} 字节 · {SendPayloadBuilder.LineEndingText(SelectedLineEnding?.Value ?? LineEnding.CrLf)}";
        SendPreviewText = HasSendError ? error : HexCodec.Format(bytes.AsSpan(0, Math.Min(bytes.Length, 64)))
            + (bytes.Length > 64 ? " …" : string.Empty);
        NotifySendAvailability();
    }

    private SendHistoryEntry CurrentSendDraft() => new(SendText, SendHex,
        SelectedLineEnding?.Value ?? LineEnding.CrLf, SelectedEncodingName);

    private void RememberSend(SendHistoryEntry draft)
    {
        // Large file-like pastes should not grow the settings file without a bound.
        if (draft.Text.Length > 8192) return;
        SendHistory.Remove(draft);
        SendHistory.Insert(0, draft);
        while (SendHistory.Count > 30) SendHistory.RemoveAt(SendHistory.Count - 1);
    }

    partial void OnSelectedSendHistoryChanged(SendHistoryEntry? value)
    {
        if (value is null) return;
        SendHex = value.Hex;
        SelectedLineEnding = LineEndingOptions.FirstOrDefault(o => o.Value == value.LineEnding) ?? LineEndingOptions[0];
        SelectedEncodingName = EncodingOptions.Contains(value.EncodingName) ? value.EncodingName : "UTF-8";
        SendText = value.Text;
        SelectedSendHistory = null;
    }

    [RelayCommand]
    private void CancelFileSend() => _sendFileCts?.Cancel();

    [RelayCommand]
    private async Task StopReconnectAsync()
    {
        await _reconnect.StopAsync();
        ReconnectStatusText = string.Empty;
        StatusDetail = "已停止本次自动重连，可手动打开串口。";
    }

    [RelayCommand]
    private void ResetCounters()
    {
        Interlocked.Exchange(ref _rxBytes, 0);
        Interlocked.Exchange(ref _txBytes, 0);
        _previousRx = _previousTx = 0;
        _rateTimestamp = Stopwatch.GetTimestamp();
        RxText = TxText = "0 B";
        RxRateText = TxRateText = "0 B/s";
    }

    private void UpdateTrafficRates()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_rateTimestamp, now).TotalSeconds;
        if (elapsed < 1) return;
        var rx = Interlocked.Read(ref _rxBytes);
        var tx = Interlocked.Read(ref _txBytes);
        RxRateText = ByteSize.Format((long)(Math.Max(0, rx - _previousRx) / elapsed)) + "/s";
        TxRateText = ByteSize.Format((long)(Math.Max(0, tx - _previousTx) / elapsed)) + "/s";
        _previousRx = rx;
        _previousTx = tx;
        _rateTimestamp = now;
        UpdateBufferStatus();
    }

    private void UpdateBufferStatus()
    {
        BufferStatusText = $"{Log.Lines.Count:N0} 行 · {ByteSize.Format(Log.TotalBytes)}"
            + (Log.DroppedLines > 0 ? $" · 已淘汰 {Log.DroppedLines:N0} 行" : string.Empty);
    }

    [RelayCommand]
    private async Task ImportRawLogAsync()
    {
        var path = _fileDialogs.AskOpenFile("SeriTerm 原始日志 (*.bin)|*.bin", "打开原始日志（仅查看，不发送）");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var maxLines = Log.MaxLines;
            var maxBytes = Log.MaxBytes;
            var (records, total) = await Task.Run(() =>
            {
                var retained = new Queue<RawLogRecord>();
                long bytes = 0;
                long count = 0;
                foreach (var record in RawLogReader.EnumerateFile(path))
                {
                    if (_disposed) break;
                    count++;
                    retained.Enqueue(record);
                    bytes += record.Payload.Length;
                    while (retained.Count > maxLines || bytes > maxBytes)
                        bytes -= retained.Dequeue().Payload.Length;
                }
                return (retained.ToArray(), count);
            });
            if (_disposed) return;
            if (records.Length == 0) throw new InvalidOperationException("文件中没有完整的 SeriTerm 日志记录。");
            // Offline review must not republish historical traffic to MCP or session log files.
            var lines = records.Select((r, i) =>
            {
                var line = new DisplayLine(-i - 1, r.TimestampUtc.ToLocalTime(), (LineDirection)r.Direction, r.Payload, "");
                line.Text = _processor.Reformat(line);
                return line;
            }).ToArray();
            for (var start = 0; start < lines.Length; start += 600)
            {
                if (_disposed) return;
                Log.Append(lines.AsSpan(start, Math.Min(600, lines.Length - start)).ToArray());
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            StatusDetail = $"已打开 {Path.GetFileName(path)} · {total:N0} 帧（仅查看，保留最近 {records.Length:N0} 帧）";
        }
        catch (Exception ex) { _notifier.ShowError("打开日志失败", ex.Message); }
    }
}
