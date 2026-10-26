using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeriTerm.App.Services;
using SeriTerm.Core.Pipeline;
using SeriTerm.App.Controls;

namespace SeriTerm.App.Tests;

public sealed class LayoutTests
{
    [Fact]
    public Task SearchOccupiesItsOwnRowAndDoesNotCoverReceivedData() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        await h.Connect();
        h.Vm.Log.Append([
            new DisplayLine(1, DateTime.Now, LineDirection.System, [], "测试会话 · 以下为自动化测试数据"),
            new DisplayLine(2, DateTime.Now, LineDirection.Tx, [0x41, 0x54], "AT"),
            new DisplayLine(3, DateTime.Now, LineDirection.Rx, [0x4F, 0x4B], "OK"),
            new DisplayLine(4, DateTime.Now, LineDirection.Rx, [0x45], "ERROR: checksum mismatch"),
        ]);
        h.Vm.SearchText = "ERROR";
        h.Vm.SearchVisible = true;
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
        var list = (FrameworkElement)log.FindName("LogList");
        var searchBounds = search.TransformToAncestor(root).TransformBounds(new Rect(search.RenderSize));
        var listBounds = list.TransformToAncestor(root).TransformBounds(new Rect(list.RenderSize));
        Assert.True(listBounds.Top >= searchBounds.Bottom - 1);
        Assert.True(listBounds.Height >= 40, $"Log viewport too short: {listBounds}");
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
        Assert.InRange(((FrameworkElement)window.FindName("LogToolbar")).ActualHeight, 35, 48);
        var sendCard = (FrameworkElement)window.FindName("SendCard");
        Assert.InRange(sendCard.ActualHeight, 45, 52);
        var options = (ToggleButton)window.FindName("SendOptionsToggle");
        Assert.NotEqual(true, options.IsChecked);
        var sidebar = (ScrollViewer)window.FindName("SidebarScrollViewer");
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
