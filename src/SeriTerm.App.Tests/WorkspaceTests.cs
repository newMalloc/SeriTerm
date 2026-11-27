using System.IO;
using System.Text;
using System.Windows.Threading;
using SeriTerm.App.ViewModels;
using SeriTerm.Core.Framing;
using SeriTerm.Core.Logging;
using SeriTerm.Core.Mcp;
using SeriTerm.Core.Pipeline;
using SeriTerm.Core.Presets;
using SeriTerm.Core.Send;
using SeriTerm.Core.Serial;

namespace SeriTerm.App.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public Task OpenConnectionAppliesPresetToPortAndAllSerialParameters() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        var port = new PortItem("COM_OTHER", "测试设备");
        h.Vm.Ports.Add(port);
        await h.Connect();
        var serial = new SerialSettings { PortName = port.PortName, BaudRate = 9600, DataBits = 7, Parity = System.IO.Ports.Parity.Even };
        h.Vm.SelectedPreset = new SerialPreset { Name = "设备预设", Serial = serial, HexDisplay = true };
        await UiThread.Drain();
        Assert.Equal(serial, h.Transport.CurrentSettings);
        Assert.Equal(TransportState.Open, h.Transport.State);
        Assert.Equal(port, h.Vm.SelectedPort);
        Assert.True(h.Vm.HexDisplay);
        Assert.Empty(h.Notifier.Errors);
    });

    [Fact]
    public Task PresetSwitchReportsMissingPortOrOpenFailure() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        await h.Connect();
        h.Vm.SelectedPreset = new SerialPreset { Name = "缺失", Serial = new SerialSettings { PortName = "COM_MISSING" } };
        Assert.Equal(TransportState.Open, h.Transport.State);
        Assert.Equal("COM_TEST", h.Transport.CurrentSettings?.PortName);
        Assert.Null(h.Vm.SelectedPreset);
        Assert.Contains("找不到串口", Assert.Single(h.Notifier.Errors));

        var port = new PortItem("COM_OTHER", "测试设备");
        h.Vm.Ports.Add(port);
        h.Transport.OpenFailure = _ => new IOException("端口被占用");
        h.Vm.SelectedPreset = new SerialPreset { Name = "占用", Serial = new SerialSettings { PortName = port.PortName } };
        await UiThread.Drain();
        Assert.Equal(TransportState.Closed, h.Transport.State);
        Assert.Null(h.Vm.SelectedPreset);
        Assert.Contains("端口被占用", h.Notifier.Errors.Last());
        Assert.True(h.Vm.CanSelectPort);
    });

    private static Task<McpFramePage> ReadJournal(AppHarness h) => ((IMcpSession)h.Vm).ReadFramesAsync(
        new McpFrameQuery(0, 500, McpFrameDirection.All, true, true, 4096), CancellationToken.None);

    [Fact]
    public Task DisplayBacklogIsBoundedWithoutDroppingRawLog() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness(logging: true);
        var block = new byte[9 * 1024 * 1024];
        var initialFrames = (await ReadJournal(h)).TotalFrames;
        h.Transport.Receive(block);

        var journal = await ReadJournal(h);
        Assert.Equal(initialFrames + block.Length / 2048, journal.TotalFrames);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!h.Vm.Log.Lines.Any(line => line.Text.Contains("显示队列淘汰", StringComparison.Ordinal))
               && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Contains(h.Vm.Log.Lines, line => line.Text.Contains("显示队列淘汰", StringComparison.Ordinal));

        await h.Vm.DisposeAsync();
        var records = RawLogReader.ReadFile(Directory.GetFiles(h.DirectoryPath, "*.bin").Single());
        Assert.Equal(block.Length, records.Sum(record => record.Payload.Length));
    });

    [Fact]
    public Task ExportUsesSnapshotAndFinishesBeforeShutdown() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        h.Vm.Log.Append([new DisplayLine(1, DateTime.Now, LineDirection.Rx, [0x41], "原始内容")]);
        h.Dialogs.SavePath = Path.Combine(h.DirectoryPath, "export.log");
        _ = h.Vm.SaveLogCommand.ExecuteAsync(null);
        h.Vm.Log.Lines[0].Text = "后续修改";
        await h.Vm.DisposeAsync();
        Assert.Contains("原始内容", await File.ReadAllTextAsync(h.Dialogs.SavePath));
        Assert.DoesNotContain("后续修改", await File.ReadAllTextAsync(h.Dialogs.SavePath));
        Assert.Empty(h.Notifier.Errors);
    });
    [Fact]
    public Task PausedFramesAreLoggedImmediatelyAndNotRepublishedOnResume() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness(logging: true);
        h.Vm.TogglePauseCommand.Execute(null);
        h.Transport.Receive(Encoding.UTF8.GetBytes("paused-frame"));
        await UiThread.Drain();
        Assert.DoesNotContain(h.Vm.Log.Lines, l => l.Direction == LineDirection.Rx);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string? livePath;
        do
        {
            livePath = Directory.GetFiles(h.DirectoryPath, "*.bin").FirstOrDefault();
            if (livePath is not null && new FileInfo(livePath).Length > 0) break;
            await Task.Delay(10);
        } while (DateTime.UtcNow < deadline);
        Assert.NotNull(livePath);
        Assert.Single(RawLogReader.ReadFile(livePath!));
        var before = (await ReadJournal(h)).TotalFrames;
        var replayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Vm.Log.ContentChanged += (_, _) =>
        {
            if (h.Vm.Log.Lines.Any(l => l.Direction == LineDirection.Rx)) replayed.TrySetResult();
        };
        h.Vm.TogglePauseCommand.Execute(null);
        await replayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(h.Vm.Log.Lines.Where(l => l.Direction == LineDirection.Rx));
        // Resume adds one system line, but must not append the received frame again.
        Assert.Equal(before + 1, (await ReadJournal(h)).TotalFrames);
        await h.Vm.DisposeAsync();
        var records = RawLogReader.ReadFile(Directory.GetFiles(h.DirectoryPath, "*.bin").Single());
        Assert.Single(records);
        Assert.Equal("paused-frame", Encoding.UTF8.GetString(records[0].Payload));
    });

    [Fact]
    public Task ReceiveBurstYieldsToPauseAndSave() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        h.Vm.SelectedFraming = h.Vm.FramingOptions.Single(x => x.Value == FramingMode.Delimiter);
        h.Vm.Log.Append([new DisplayLine(0, DateTime.Now, LineDirection.Rx, [0x41], "already visible")]);
        h.Dialogs.SavePath = Path.Combine(h.DirectoryPath, "burst.log");

        const int frameCount = 1600;
        var frames = Enumerable.Range(0, frameCount).Select(i => $"frame {i:D4}").ToArray();
        var dispatcher = Dispatcher.CurrentDispatcher;

        // Queue a receive update first, as if bytes arrived just before the user clicked Pause.
        h.Transport.Receive(Encoding.UTF8.GetBytes(string.Join("\r\n", frames) + "\r\n"));
        await dispatcher.InvokeAsync(() =>
        {
            h.Vm.TogglePauseCommand.Execute(null);
            Assert.True(h.Vm.IsPaused);
            Assert.Equal("already visible", Assert.Single(h.Vm.Log.Lines).Text);
        }, DispatcherPriority.Input);

        h.Transport.Receive("while paused\r\n"u8.ToArray());
        var saving = await dispatcher.InvokeAsync(() => h.Vm.SaveLogCommand.ExecuteAsync(null), DispatcherPriority.Input);
        await saving;
        var saved = Assert.Single(await File.ReadAllLinesAsync(h.Dialogs.SavePath));
        Assert.EndsWith("[Rx] already visible", saved);

        var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedHint = $"{frameCount + 1:N0} 行待显示";
        void CheckBuffered()
        {
            if (h.Vm.PausedHintText.Contains(expectedHint, StringComparison.Ordinal)) buffered.TrySetResult();
        }
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(h.Vm.PausedHintText)) CheckBuffered();
        };
        CheckBuffered();
        await buffered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(h.Vm.Log.Lines);

        var caughtUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Vm.Log.ContentChanged += (_, _) =>
        {
            if (h.Vm.Log.Lines.LastOrDefault()?.Text == "after resume") caughtUp.TrySetResult();
        };
        h.Vm.TogglePauseCommand.Execute(null);
        Assert.Single(h.Vm.Log.Lines);
        h.Transport.Receive("after resume\r\n"u8.ToArray());
        await caughtUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "already visible" }.Concat(frames).Concat(["while paused", "after resume"]),
            h.Vm.Log.Lines.Where(l => l.Direction == LineDirection.Rx).Select(l => l.Text));
        Assert.Empty(h.Notifier.Errors);
    });

    [Fact]
    public Task ResumingLongFramesPreservesUtf8AcrossChunks() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        h.Vm.TogglePauseCommand.Execute(null);
        var payload = new string('A', DelimiterFrameSplitter.DefaultMaxPendingBytes - 1) + "中end";
        h.Transport.Receive(Encoding.UTF8.GetBytes(payload));
        var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(h.Vm.PausedHintText)
                && h.Vm.PausedHintText.Contains("2 行待显示", StringComparison.Ordinal))
                buffered.TrySetResult();
        };
        await buffered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Vm.TogglePauseCommand.Execute(null);
        var replayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Vm.Log.ContentChanged += (_, _) =>
        {
            if (h.Vm.Log.Lines.Count(line => line.Direction == LineDirection.Rx) == 2)
                replayed.TrySetResult();
        };
        await replayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(payload, string.Concat(h.Vm.Log.Lines.Where(line => line.Direction == LineDirection.Rx).Select(line => line.Text)));
    });

    [Fact]
    public Task ClosingWhilePausedPreservesIncompleteFrame() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness(logging: true);
        h.Vm.SelectedFraming = h.Vm.FramingOptions.Single(x => x.Value == FramingMode.Delimiter);
        h.Vm.TogglePauseCommand.Execute(null);
        h.Transport.Receive(Encoding.UTF8.GetBytes("unterminated"));
        await h.Vm.DisposeAsync();
        var records = RawLogReader.ReadFile(Directory.GetFiles(h.DirectoryPath, "*.bin").Single());
        Assert.Equal("unterminated", Encoding.UTF8.GetString(Assert.Single(records).Payload));
    });

    [Fact]
    public Task InvalidHexDisablesSendAndReportsReason() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        await h.Connect();
        h.Vm.SendHex = true;
        h.Vm.SendText = "01 GG";
        Assert.True(h.Vm.HasSendError);
        Assert.False(h.Vm.SendCommand.CanExecute(null));
        h.Vm.SendText = "01 FF";
        h.Vm.SelectedLineEnding = h.Vm.LineEndingOptions.Single(o => o.Value == LineEnding.None);
        Assert.True(h.Vm.SendCommand.CanExecute(null));
        Assert.Equal("01 FF", h.Vm.SendPreviewText);
        await h.Vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(new byte[] { 1, 255 }, Assert.Single(h.Transport.Writes));
        Assert.Single(h.Vm.SendHistory);
    });

    [Fact]
    public Task HistoryRestoresPayloadOptionsWithoutSending() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        h.Vm.SelectedSendHistory = new SendHistoryEntry("AA 55", true, LineEnding.None, "UTF-8");
        Assert.True(h.Vm.SendHex);
        Assert.Equal("AA 55", h.Vm.SendText);
        Assert.Equal(LineEnding.None, h.Vm.SelectedLineEnding!.Value);
        Assert.Empty(h.Transport.Writes);
        Assert.Null(h.Vm.SelectedSendHistory);
    });

    [Fact]
    public Task FileSendCanBeCancelledWhileAsyncCommandIsRunning() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        await h.Connect();
        h.Dialogs.OpenPath = Path.Combine(h.DirectoryPath, "send.dat");
        await File.WriteAllBytesAsync(h.Dialogs.OpenPath, new byte[8192]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Transport.BeforeWrite = async ct => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var sending = h.Vm.SendFileCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(h.Vm.IsSendingFile);
        Assert.True(h.Vm.CancelFileSendCommand.CanExecute(null));
        Assert.False(h.Vm.SendCommand.CanExecute(null));
        h.Vm.CancelFileSendCommand.Execute(null);
        await sending;
        Assert.False(h.Vm.IsSendingFile);
        Assert.Equal("已取消", h.Vm.SendFileStatusText);
        Assert.Empty(h.Notifier.Errors);
    });

    [Fact]
    public Task DisconnectStopsTimedSendWithoutErrorDialog() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        await h.Connect();
        h.Vm.SendText = "test";
        h.Vm.TimedSendIntervalText = "0.05";
        h.Vm.ToggleTimedSendCommand.Execute(null);
        await Task.Delay(120);
        h.Transport.SetState(TransportState.Closed);
        await UiThread.Drain();
        Assert.False(h.Vm.TimedSendEnabled);
        var count = h.Transport.Writes.Count;
        await Task.Delay(120);
        Assert.Equal(count, h.Transport.Writes.Count);
        Assert.Empty(h.Notifier.Errors);
    });

    [Fact]
    public Task StoppingDuringIntervalChangeCannotRestartTimer() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        await h.Connect();
        h.Vm.ToggleTimedSendCommand.Execute(null);
        h.Vm.TimedSendIntervalText = "0.05";
        h.Vm.ToggleTimedSendCommand.Execute(null);
        await Task.Delay(120);
        var count = h.Transport.Writes.Count;
        await Task.Delay(120);
        Assert.Equal(count, h.Transport.Writes.Count);
        Assert.False(h.Vm.TimedSendEnabled);
    });

    [Fact]
    public Task InvalidFramingInputKeepsLastValidConfiguration() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        h.Vm.SelectedFraming = h.Vm.FramingOptions.Single(x => x.Value == FramingMode.Delimiter);
        h.Vm.DelimiterText = "\\xZZ";
        await Task.Delay(450);
        Assert.NotEmpty(h.Vm.ReceiveValidationText);
        h.Transport.Receive(Encoding.UTF8.GetBytes("first\r\nsecond"));
        await UiThread.Drain();
        Assert.Single(h.Vm.Log.Lines.Where(l => l.Direction == LineDirection.Rx));
        Assert.Equal("first", h.Vm.Log.Lines.Last(l => l.Direction == LineDirection.Rx).Text);
    });

    [Fact]
    public Task RawLogImportDoesNotTransmitOrPublishToMcp() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        await using (var log = new SessionLogger(new LogSessionOptions { Directory = h.DirectoryPath, RawEnabled = true, TextEnabled = false }))
        {
            log.Log(new DisplayLine(1, DateTime.Now, LineDirection.Rx, [0x41], "A"));
        }
        h.Dialogs.OpenPath = Directory.GetFiles(h.DirectoryPath, "*.bin").Single();
        var before = (await ReadJournal(h)).TotalFrames;
        await h.Vm.ImportRawLogCommand.ExecuteAsync(null);
        Assert.Equal("A", Assert.Single(h.Vm.Log.Lines).Text);
        Assert.Equal(before, (await ReadJournal(h)).TotalFrames);
        Assert.Empty(h.Transport.Writes);
        Assert.Empty(h.Notifier.Errors);
    });
}
