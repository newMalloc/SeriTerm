using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using SeriTerm.App.Services;
using SeriTerm.Core.Pipeline;
using SeriTerm.App.Controls;

namespace SeriTerm.App.Tests;

public sealed class LayoutTests
{
    [Fact]
    public Task F6TogglesPauseAndResume() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        var window = new MainWindow(h.Vm, h.Theme);
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var source = HwndSource.FromHwnd(handle)!;
        for (var i = 0; i < 2; i++)
        {
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.F6)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            window.RaiseEvent(key);
            Assert.True(key.Handled);
            Assert.Equal(i == 0, h.Vm.IsPaused);
        }
        window.Close();
    });

    [Fact]
    public Task SelectingAnotherOpenPortSwitchesTheConnection() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        await h.Connect();
        var port = new SeriTerm.App.ViewModels.PortItem("COM_OTHER", "COM_OTHER");
        h.Vm.Ports.Add(port);
        h.Vm.SelectedPort = port;
        Assert.True(h.Vm.CanSelectPort);
        await h.Vm.SwitchOpenPortAsync(port);
        await UiThread.Drain();
        Assert.Equal("COM_OTHER", h.Transport.CurrentSettings?.PortName);
        Assert.Equal(SeriTerm.Core.Serial.TransportState.Open, h.Transport.State);
        Assert.Empty(h.Notifier.Errors);
    });

    [Fact]
    public Task SearchFloatsAtTheTopRightWithoutResizingTheLog() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        await h.Connect();
        h.Vm.Log.Append([
            new DisplayLine(1, DateTime.Now, LineDirection.System, [], "测试会话 · 以下为自动化测试数据"),
            new DisplayLine(2, DateTime.Now, LineDirection.Tx, [0x41, 0x54], "AT"),
            new DisplayLine(3, DateTime.Now, LineDirection.Rx, [0x4F, 0x4B], "OK"),
            new DisplayLine(4, DateTime.Now, LineDirection.Rx, [0x45], "ERROR: checksum mismatch"),
        ]);
        h.Vm.TerminalMode = true;
        h.Vm.SendText = "AT+VERSION?";
        var window = new MainWindow(h.Vm, h.Theme);
        var root = (Border)window.Content;
        root.Background = window.Background;
        root.Measure(new Size(920, 600));
        root.Arrange(new Rect(0, 0, 920, 600));
        root.UpdateLayout();
        await UiThread.Drain();
        root.UpdateLayout();
        var log = (LogView)window.FindName("LogViewControl");
        var search = (FrameworkElement)log.FindName("SearchOverlay");
        var editor = (TextEditor)log.FindName("LogText");
        var closedBounds = editor.TransformToAncestor(root).TransformBounds(new Rect(editor.RenderSize));
        h.Vm.SearchText = "ERROR";
        h.Vm.SearchVisible = true;
        root.UpdateLayout();
        var searchBounds = search.TransformToAncestor(root).TransformBounds(new Rect(search.RenderSize));
        var textBounds = editor.TransformToAncestor(root).TransformBounds(new Rect(editor.RenderSize));
        Assert.Equal(closedBounds, textBounds);
        Assert.InRange(searchBounds.Width, 250, 390);
        Assert.InRange(textBounds.Right - searchBounds.Right, 19, 28);
        Assert.InRange(searchBounds.Top - textBounds.Top, 0, 10);
        Assert.Equal(1, h.Vm.Log.MatchCount);
        var output = Environment.GetEnvironmentVariable("SERITERM_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(output))
        {
            var bitmap = new RenderTargetBitmap(920, 600, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(output);
            using var file = File.Create(Path.Combine(output, "workspace-search-920x600.png"));
            encoder.Save(file);
        }
        window.Content = null;
    });

    [Fact]
    public Task LongLogHasGrabbableScrollbarAndKeepsFollowingNewText() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        var window = new MainWindow(h.Vm, h.Theme);
        var root = (Border)window.Content;
        var log = (LogView)window.FindName("LogViewControl");
        var editor = (TextEditor)log.FindName("LogText");
        h.Vm.Log.Append(Enumerable.Range(1, 7_500)
            .Select(i => new DisplayLine(i, DateTime.Now, LineDirection.Rx, [0x41], $"test log {i}"))
            .ToArray());
        root.Measure(new Size(1264, 737));
        root.Arrange(new Rect(0, 0, 1264, 737));
        await UiThread.Drain();
        root.UpdateLayout();

        var viewer = (ScrollViewer)editor.Template.FindName("PART_ScrollViewer", editor);
        var scrollbar = (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
        var track = (Track)scrollbar.Template.FindName("PART_Track", scrollbar);
        Assert.InRange(scrollbar.ActualWidth, 15, 20);
        Assert.True(track.Thumb.ActualHeight >= 32, $"Thumb too short: {track.Thumb.ActualHeight}");
        Assert.InRange(viewer.ScrollableHeight - viewer.VerticalOffset, 0, 1);
        Assert.True(h.Vm.AutoScroll);

        viewer.ScrollToTop();
        root.UpdateLayout();
        await UiThread.Drain();
        Assert.False(h.Vm.AutoScroll);

        h.Vm.SearchText = "test log 6000";
        h.Vm.SearchNextCommand.Execute(null);
        root.UpdateLayout();
        await UiThread.Drain();
        Assert.InRange(viewer.VerticalOffset, viewer.ScrollableHeight * .75, viewer.ScrollableHeight * .85);
        Assert.False(h.Vm.AutoScroll);

        h.Vm.ScrollToLatestCommand.Execute(null);
        h.Vm.Log.Append([new DisplayLine(7_501, DateTime.Now, LineDirection.Rx, [0x42], "newest")]);
        root.UpdateLayout();
        await UiThread.Drain();
        Assert.InRange(viewer.ScrollableHeight - viewer.VerticalOffset, 0, 1);
        Assert.True(h.Vm.AutoScroll);
        Assert.EndsWith("Rx newest\r\n", editor.Text);
        window.Content = null;
    });

    [Fact]
    public Task VerticalThumbTracksInsideLogWhileNewRecordsArrive() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        var window = new MainWindow(h.Vm, h.Theme);
        var root = (Border)window.Content;
        var log = (LogView)window.FindName("LogViewControl");
        var editor = (TextEditor)log.FindName("LogText");
        h.Vm.Log.Append(Enumerable.Range(1, 7_500)
            .Select(i => new DisplayLine(i, DateTime.Now, LineDirection.Rx, [], $"record {i}")).ToArray());
        root.Measure(new Size(1264, 737));
        root.Arrange(new Rect(0, 0, 1264, 737));
        await Settle();
        var viewer = (ScrollViewer)editor.Template.FindName("PART_ScrollViewer", editor);
        var bar = (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
        var track = bar.Track;
        var thumb = track.Thumb;
        var liveRecord = new DisplayLine(7_501, DateTime.Now, LineDirection.Rx, [], "incoming");

        thumb.RaiseEvent(new DragStartedEventArgs(4, 8));
        thumb.RaiseEvent(new DragDeltaEventArgs(0, -60));
        await Settle();
        var before = viewer.VerticalOffset;
        Assert.True(before < viewer.ScrollableHeight - 8);
        foreach (var dy in new[] { -24, -12, 10, 18 })
        {
            var expected = viewer.VerticalOffset + track.ValueFromDistance(0, dy);
            thumb.RaiseEvent(new DragDeltaEventArgs(-300, dy));
            await Settle();
            Assert.InRange(viewer.VerticalOffset, expected - 1, expected + 1);
        }
        var held = viewer.VerticalOffset;
        thumb.RaiseEvent(new DragDeltaEventArgs(-450, 0));
        h.Vm.Log.Append([liveRecord]);
        await Settle();
        Assert.InRange(viewer.VerticalOffset, held - 1, held + 1);
        thumb.RaiseEvent(new DragCompletedEventArgs(-450, -68, false));
        await Settle();
        Assert.False(h.Vm.AutoScroll);

        thumb.RaiseEvent(new DragStartedEventArgs(4, 8));
        thumb.RaiseEvent(new DragDeltaEventArgs(-300, track.ActualHeight));
        await Settle();
        Assert.False(h.Vm.AutoScroll);
        thumb.RaiseEvent(new DragCompletedEventArgs(-300, track.ActualHeight, false));
        await Settle();
        Assert.True(h.Vm.AutoScroll);
        Assert.InRange(viewer.ScrollableHeight - viewer.VerticalOffset, 0, 1);
        window.Content = null;

        async Task Settle()
        {
            root.UpdateLayout();
            await UiThread.Drain();
            root.UpdateLayout();
        }
    });

    [Fact]
    public Task LogTextSupportsCrossLineSelectionAndIncrementalEviction() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        h.Vm.ShowTimestamp = false;
        h.Vm.Log.MaxLines = 4;
        var log = new LogView { DataContext = h.Vm };
        var editor = (TextEditor)log.FindName("LogText");
        h.Vm.Log.Append([
            new DisplayLine(1, DateTime.Now, LineDirection.Rx, [0x61], "alpha"),
            new DisplayLine(2, DateTime.Now, LineDirection.Tx, [0x62], "beta\ngamma"),
            new DisplayLine(3, DateTime.Now, LineDirection.Rx, [0x63], "delta"),
            new DisplayLine(4, DateTime.Now, LineDirection.Rx, [0x64], "epsilon"),
        ]);

        Assert.True(editor.IsReadOnly);
        editor.Select(6, 13);
        Assert.Equal("ha\r\nTx beta\ng", editor.SelectedText);
        h.Vm.Log.Append([new DisplayLine(5, DateTime.Now, LineDirection.Rx, [0x65], "zeta")]);
        Assert.Equal("Rx delta\r\nRx epsilon\r\nRx zeta\r\n", editor.Text);

        h.Vm.Log.Reformat(_ => "41");
        Assert.Equal("Rx 41\r\nRx 41\r\nRx 41\r\n", editor.Text);
        h.Vm.ShowTimestamp = true;
        Assert.StartsWith(h.Vm.Log.Lines[0].TimeText, editor.Text);
        h.Vm.ClearLogCommand.Execute(null);
        Assert.EndsWith("-- 显示数据已清空。\r\n", editor.Text);
        Assert.DoesNotContain("Rx 41", editor.Text);
        h.Vm.Log.Clear();
        Assert.Empty(editor.Text);
        log.DataContext = null;
    });

    [Fact]
    public Task WindowClosesOnceWhenCleanupFinishesSynchronously() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        var window = new MainWindow(h.Vm, h.Theme);
        _ = new WindowInteropHelper(window).EnsureHandle();
        var closedCount = 0;
        window.Closed += (_, _) => closedCount++;
        window.Close();
        window.Close();
        Assert.Equal(0, closedCount);
        await UiThread.Drain();
        Assert.Equal(1, closedCount);
    });

    [Theory]
    [InlineData(1280, 800, ThemeMode.Dark)]
    [InlineData(1280, 800, ThemeMode.Light)]
    [InlineData(920, 600, ThemeMode.Dark)]
    [InlineData(920, 600, ThemeMode.Light)]
    public Task MainWorkspaceFitsWithoutClippingPrimaryActions(int width, int height, ThemeMode theme) => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        h.Theme.Apply(theme);
        var window = new MainWindow(h.Vm, h.Theme);
        // Measure and render the WPF visual tree in memory. No desktop window is shown.
        var root = (Border)window.Content;
        root.Background = window.Background;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        await UiThread.Drain();
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        foreach (var name in new[] { "PortCombo", "BaudRateCombo", "SendTextBox", "SendButton", "LogCard" })
        {
            var element = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));
            var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            Assert.True(bounds.Width > 20 && bounds.Height > 10, $"{name} has no usable size: {bounds}");
            Assert.True(bounds.Left >= 0 && bounds.Right <= width + 1 && bounds.Top >= 0 && bounds.Bottom <= height + 1,
                $"{name} clipped at {width}x{height}: {bounds}");
        }
        var send = (TextBox)window.FindName("SendTextBox");
        Assert.True(send.AcceptsReturn);
        Assert.True(((FrameworkElement)window.FindName("LogCard")).ActualHeight >= 450);
        var toolbar = (FrameworkElement)window.FindName("LogToolbar");
        Assert.InRange(toolbar.ActualHeight, 25, 48);
        var sendCard = (FrameworkElement)window.FindName("SendCard");
        Assert.InRange(sendCard.ActualHeight, 45, 52);
        var options = (ToggleButton)window.FindName("SendOptionsToggle");
        Assert.NotEqual(true, options.IsChecked);
        var sidebar = (ScrollViewer)window.FindName("SidebarScrollViewer");
        Assert.True(sidebar.IsAncestorOf(toolbar));
        foreach (var name in new[] { "LogSearchButton", "LogPauseButton", "LogSaveButton", "LogClearButton" })
        {
            var button = (Button)window.FindName(name);
            var bounds = button.TransformToAncestor(toolbar).TransformBounds(new Rect(button.RenderSize));
            Assert.True(bounds.Width >= 40 && bounds.Left >= 0 && bounds.Right <= toolbar.ActualWidth + 1, $"{name}: {bounds}");
        }
        Assert.Equal(290, sidebar.ActualWidth);
        Assert.NotEqual(Visibility.Visible, sidebar.ComputedVerticalScrollBarVisibility);
        foreach (var name in new[] { "HexDisplayCheck", "AutoScrollToggle" })
            Assert.True(sidebar.IsAncestorOf((DependencyObject)window.FindName(name)), $"{name} should be in the sidebar.");
        AssertCentered("LogFontSizeLabel", "LogFontSizeCombo");
        AssertCentered("LineEndingLabel", "LineEndingCombo");

        void AssertCentered(string labelName, string controlName)
        {
            var label = (FrameworkElement)window.FindName(labelName);
            var control = (FrameworkElement)window.FindName(controlName);
            var labelBounds = label.TransformToAncestor(root).TransformBounds(new Rect(label.RenderSize));
            var controlBounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
            var centerDifference = Math.Abs(labelBounds.Top + labelBounds.Height / 2 - controlBounds.Top - controlBounds.Height / 2);
            Assert.True(centerDifference <= 1, $"{labelName} is not centered with {controlName}: {centerDifference} DIP.");
        }

        var output = Environment.GetEnvironmentVariable("SERITERM_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"workspace-{theme}-{width}x{height}.png"));
            encoder.Save(file);
        }
        options.IsChecked = true;
        root.UpdateLayout();
        foreach (var name in new[] { "TimedIntervalBox", "SendFileButton", "TerminalModeCheck" })
        {
            var element = (FrameworkElement)window.FindName(name);
            var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            Assert.True(bounds.Width > 20 && bounds.Height > 10, $"{name} has no usable size: {bounds}");
            Assert.True(bounds.Left >= 0 && bounds.Right <= width + 1 && bounds.Top >= 0 && bounds.Bottom <= height + 1,
                $"{name} clipped after expanding options: {bounds}");
        }
        if (!string.IsNullOrWhiteSpace(output))
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"workspace-options-{theme}-{width}x{height}.png"));
            encoder.Save(file);
        }
        // Hiding the scrollbar must not make the lower settings unreachable or change the column width.
        sidebar.ScrollToBottom();
        root.UpdateLayout();
        Assert.True(sidebar.VerticalOffset > 0);
        Assert.Equal(290, sidebar.ActualWidth);
        window.Content = null;
    });
}
