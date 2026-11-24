using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using SeriTerm.App.Services;
using SeriTerm.Core.Framing;
using SeriTerm.Core.Pipeline;
using SeriTerm.App.Controls;
using SeriTerm.Core.Send;

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
        await UiThread.Drain();
        var pause = (Button)window.FindName("LogPauseButton");
        var icon = Assert.IsType<System.Windows.Shapes.Path>(pause.Content);
        var pauseGeometry = icon.Data.ToString();
        for (var i = 0; i < 2; i++)
        {
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.F6)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            window.RaiseEvent(key);
            Assert.True(key.Handled);
            Assert.Equal(i == 0, h.Vm.IsPaused);
            Assert.Equal(i == 0 ? "继续显示" : "暂停显示", AutomationProperties.GetName(pause));
            Assert.Equal(i == 1, icon.Data.ToString() == pauseGeometry);
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
        h.Vm.SelectedTheme = theme;
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
        var connection = (Button)window.FindName("ConnectionButton");
        var pause = (Button)window.FindName("LogPauseButton");
        var pauseOrigin = pause.TranslatePoint(new Point(), connection);
        Assert.InRange(pauseOrigin.Y, -1, 1);
        Assert.InRange(Math.Abs(pause.ActualHeight - connection.ActualHeight), 0, 1);
        Assert.True(pauseOrigin.X > connection.ActualWidth);
        Assert.True(sidebar.IsAncestorOf(connection) && sidebar.IsAncestorOf(pause));
        foreach (var name in new[] { "LogSearchButton", "LogSaveButton", "LogClearButton" })
        {
            var button = (Button)window.FindName(name);
            var bounds = button.TransformToAncestor(toolbar).TransformBounds(new Rect(button.RenderSize));
            Assert.True(bounds.Width >= 40 && bounds.Left >= 0 && bounds.Right <= toolbar.ActualWidth + 1, $"{name}: {bounds}");
        }
        Assert.Equal(new GridLength(290), ((ColumnDefinition)window.FindName("SidebarColumn")).Width);
        var sidebarWidth = sidebar.ActualWidth;
        // UseLayoutRounding 在 125% 缩放下会把 362.5 个物理像素对齐到整数。
        var dpiScale = VisualTreeHelper.GetDpi(sidebar).DpiScaleX;
        Assert.InRange(Math.Abs(sidebarWidth * dpiScale - 290 * dpiScale), 0, 0.500001);
        Assert.NotEqual(Visibility.Visible, sidebar.ComputedVerticalScrollBarVisibility);
        foreach (var name in new[] { "HexDisplayCheck", "AutoScrollToggle" })
            Assert.True(sidebar.IsAncestorOf((DependencyObject)window.FindName(name)), $"{name} should be in the sidebar.");
        var framing = (ComboBox)window.FindName("FramingCombo");
        var fullFramingWidth = framing.ActualWidth;
        foreach (var (mode, fieldName) in new[] { (FramingMode.Gap, "FrameGapInput"), (FramingMode.Delimiter, "FrameDelimiterInput") })
        {
            h.Vm.SelectedFraming = h.Vm.FramingOptions.Single(option => option.Value == mode);
            root.UpdateLayout();
            var field = (TextBox)window.FindName(fieldName);
            var origin = field.TranslatePoint(new Point(), framing);
            Assert.Equal(Visibility.Visible, field.Visibility);
            Assert.InRange(origin.Y, -1, 1);
            Assert.True(origin.X > framing.ActualWidth && origin.X + field.ActualWidth <= fullFramingWidth + 1);
        }
        h.Vm.SelectedFraming = h.Vm.FramingOptions.Single(option => option.Value == FramingMode.None);
        root.UpdateLayout();
        Assert.Equal(fullFramingWidth, framing.ActualWidth);
        var logCard = (FrameworkElement)window.FindName("LogCard");
        var logBounds = logCard.TransformToAncestor(root).TransformBounds(new Rect(logCard.RenderSize));
        var gear = (ToggleButton)window.FindName("SettingsButton");
        var settings = (SettingsFlyout)window.FindName("SettingsPanel");
        Assert.False(settings.IsVisible);
        gear.IsChecked = true;
        root.UpdateLayout();
        Assert.Equal(logBounds, logCard.TransformToAncestor(root).TransformBounds(new Rect(logCard.RenderSize)));
        AssertSettingsBounds();
        AssertCentered("LogFontSizeLabel", "FontSizeBox");
        AssertCentered("LineEndingLabel", "EndingBox");
        Capture("settings");

        ((Button)settings.FindName("AppearanceButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var glassRow = (FrameworkElement)settings.FindName("GlassSettingsRows");
        Assert.Equal(Visibility.Collapsed, glassRow.Visibility);
        h.Vm.BackgroundMode = BackdropMode.Desktop;
        root.UpdateLayout();
        Assert.Equal(Visibility.Visible, glassRow.Visibility);
        var slider = (Slider)settings.FindName("GlassTransparencySlider");
        Assert.InRange(slider.ActualWidth, 120, settings.ActualWidth);
        var blurSlider = (Slider)settings.FindName("GlassBlurSlider");
        Assert.Equal(slider.ActualWidth, blurSlider.ActualWidth);
        Assert.Equal(slider.TranslatePoint(new Point(), settings).X, blurSlider.TranslatePoint(new Point(), settings).X);
        AssertSettingsBounds();
        Capture("settings-glass");
        h.Vm.BackgroundMode = BackdropMode.Image;
        root.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, glassRow.Visibility);
        AssertSettingsBounds();
        Capture("settings-appearance");
        gear.IsChecked = false;
        root.UpdateLayout();

        void AssertCentered(string labelName, string controlName)
        {
            var label = (FrameworkElement)settings.FindName(labelName);
            var control = (FrameworkElement)settings.FindName(controlName);
            var labelBounds = label.TransformToAncestor(root).TransformBounds(new Rect(label.RenderSize));
            var controlBounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
            var centerDifference = Math.Abs(labelBounds.Top + labelBounds.Height / 2 - controlBounds.Top - controlBounds.Height / 2);
            Assert.True(centerDifference <= 1, $"{labelName} is not centered with {controlName}: {centerDifference} DIP.");
        }

        void AssertSettingsBounds()
        {
            var bounds = settings.TransformToAncestor(root).TransformBounds(new Rect(settings.RenderSize));
            Assert.InRange(bounds.Width, 340, 380);
            Assert.InRange(width - bounds.Right, 0, 20);
            Assert.InRange(bounds.Top, 40, 50);
            Assert.InRange(bounds.Bottom, 150, height - 10);
        }

        void Capture(string name)
        {
            var output = Environment.GetEnvironmentVariable("SERITERM_UI_ARTIFACTS");
            if (string.IsNullOrWhiteSpace(output)) return;
            Directory.CreateDirectory(output);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"{name}-{theme}-{width}x{height}.png"));
            encoder.Save(file);
        }
        Capture("workspace");
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
        Capture("workspace-options");
        // Hiding the scrollbar must not make the lower settings unreachable or change the column width.
        ((Expander)window.FindName("AdvancedConnectionExpander")).IsExpanded = true;
        root.UpdateLayout();
        sidebar.ScrollToBottom();
        root.UpdateLayout();
        Assert.True(sidebar.VerticalOffset > 0);
        Assert.Equal(sidebarWidth, sidebar.ActualWidth);
        window.Content = null;
    });

    [Fact]
    public Task SettingsApplyToTheWorkspaceAndKeepKeysOutOfTheSerialTerminal() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        await h.Connect();
        var window = new MainWindow(h.Vm, h.Theme);
        var root = (Border)window.Content;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var source = HwndSource.FromHwnd(handle)!;
        var gear = (ToggleButton)window.FindName("SettingsButton");
        var settings = (SettingsFlyout)window.FindName("SettingsPanel");
        h.Vm.TerminalMode = true;
        gear.IsChecked = true;
        root.Measure(new Size(920, 600));
        root.Arrange(new Rect(0, 0, 920, 600));
        await UiThread.Drain();

        var encoding = (ComboBox)settings.FindName("EncodingBox");
        encoding.SetCurrentValue(Selector.SelectedItemProperty, h.Vm.EncodingOptions.First(name => name != "UTF-8"));
        var ending = (ListBox)settings.FindName("EndingBox");
        ending.SetCurrentValue(Selector.SelectedItemProperty, h.Vm.LineEndingOptions.Single(option => option.Value == LineEnding.Lf));
        var buffer = (ComboBox)settings.FindName("BufferBox");
        buffer.SetCurrentValue(Selector.SelectedValueProperty, 16);
        Assert.Equal(encoding.SelectedItem, h.Vm.SelectedEncodingName);
        Assert.Equal(LineEnding.Lf, h.Vm.SelectedLineEnding!.Value);
        Assert.Equal(16L * 1024 * 1024, h.Vm.Log.MaxBytes);

        RaiseKey(Key.Enter);
        await UiThread.Drain();
        Assert.Empty(h.Transport.Writes);

        var appearance = (Button)settings.FindName("AppearanceButton");
        appearance.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var theme = (ListBox)settings.FindName("ThemeBox");
        theme.SetCurrentValue(Selector.SelectedValueProperty, ThemeMode.Light);
        Assert.Equal(ThemeMode.Light, h.Theme.Current);

        var picturePath = Path.Combine(h.DirectoryPath, "wallpaper.png");
        var picture = new PngBitmapEncoder();
        picture.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr24, null, new byte[12], 6)));
        using (var file = File.Create(picturePath)) picture.Save(file);
        h.Vm.BackgroundImagePath = picturePath;
        h.Vm.BackgroundMode = BackdropMode.Image;
        h.Vm.BackgroundImageOpacity = 0.55;
        var backdrop = (FrameworkElement)window.FindName("BackdropLayer");
        Assert.Equal(Visibility.Visible, backdrop.Visibility);
        Assert.Equal(0.55, backdrop.Opacity);
        h.Vm.BackgroundImagePath = Path.Combine(h.DirectoryPath, "missing.png");
        Assert.Equal(Visibility.Collapsed, backdrop.Visibility);

        var log = (LogView)window.FindName("LogViewControl");
        var editor = (TextEditor)log.FindName("LogText");
        h.Vm.ShowLogLineNumbers = true;
        Assert.True(editor.ShowLineNumbers);
        Assert.True(RaiseKey(Key.Escape).Handled);
        Assert.False(gear.IsChecked);
        Assert.Equal(16, h.Vm.Settings.LogBufferMiB);

        window.Close();
        await UiThread.Drain();

        KeyEventArgs RaiseKey(Key key)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(args);
            return args;
        }
    });
}
