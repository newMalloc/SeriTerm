using System.IO;
using System.Text;
using SeriTerm.Core.Framing;
using SeriTerm.Core.Logging;
using SeriTerm.Core.Mcp;
using SeriTerm.Core.Pipeline;
using SeriTerm.Core.Send;
using SeriTerm.Core.Serial;

namespace SeriTerm.App.Tests;

public sealed class WorkspaceTests
{
    private static Task<McpFramePage> ReadJournal(AppHarness h) => ((IMcpSession)h.Vm).ReadFramesAsync(
        new McpFrameQuery(0, 500, McpFrameDirection.All, true, true, 4096), CancellationToken.None);

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
        h.Vm.TogglePauseCommand.Execute(null);
        Assert.Single(h.Vm.Log.Lines.Where(l => l.Direction == LineDirection.Rx));
        // Resume adds one system line, but must not append the received frame again.
        Assert.Equal(before + 1, (await ReadJournal(h)).TotalFrames);
        await h.Vm.DisposeAsync();
        var records = RawLogReader.ReadFile(Directory.GetFiles(h.DirectoryPath, "*.bin").Single());
        Assert.Single(records);
        Assert.Equal("paused-frame", Encoding.UTF8.GetString(records[0].Payload));
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
