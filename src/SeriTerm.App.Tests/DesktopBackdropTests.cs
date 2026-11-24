using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using SeriTerm.App.Controls;
using SeriTerm.App.Services;
using SeriTerm.App.ViewModels;

namespace SeriTerm.App.Tests;

public sealed class DesktopBackdropTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // RenderTargetBitmap 只能看到 WPF 图层。这个回归需要交互桌面、系统透明效果和真实 DWM。
    private sealed class DesktopFactAttribute : FactAttribute
    {
        public DesktopFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SERITERM_TEST_DESKTOP_BLUR") != "1")
                Skip = "Set SERITERM_TEST_DESKTOP_BLUR=1 on an interactive desktop with transparency enabled.";
        }
    }

    [DesktopFact]
    public Task LogShowsBlurredChangingContentBehindTheWindow() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        h.Vm.AutoOpenOnStartup = false;
        h.Vm.BackgroundMode = BackdropMode.Desktop;
        var work = SystemParameters.WorkArea;
        var behind = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Left = work.Left, Top = work.Top, Width = work.Width, Height = work.Height, Topmost = true,
        };
        var main = new MainWindow(h.Vm, h.Theme)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = work.Left, Top = work.Top,
            Width = 920, Height = 600, ShowInTaskbar = false, Topmost = true,
        };
        try
        {
            behind.Show();
            main.Owner = behind;
            main.Show();
            main.Activate();
            await h.Connect();
            var log = (Border)main.FindName("LogCard");
            var samplePoint = new Point(115, 380); // 像素采样范围需避开日志左侧边框和右上角设置浮层。
            var gear = (ToggleButton)main.FindName("SettingsButton");
            var settings = (SettingsFlyout)main.FindName("SettingsPanel");
            var slider = (Slider)settings.FindName("GlassTransparencySlider");
            foreach (var theme in new[] { ThemeMode.Dark, ThemeMode.Light })
            {
                h.Vm.SelectedTheme = theme;
                h.Vm.BackgroundMode = BackdropMode.Desktop;
                h.Vm.GlassTransparency = 8;
                var red = await SampleOver(Brushes.Red);
                var blue = await SampleOver(Brushes.Blue);
                Assert.True(red.R > blue.R + 3 && blue.B > red.B + 3, $"{theme}: {red}, {blue}");
                var black = await SampleOver(Brushes.Black);
                var white = await SampleOver(Brushes.White);
                output.WriteLine($"{theme} log over black/white: {black} / {white}");
                Assert.InRange(white.G - black.G, 0, 22);

                // 同一位置的两个色标在背后生成锐利边界，玻璃里边界两侧都应混入另一侧的颜色。
                var center = log.PointToScreen(samplePoint);
                var seam = behind.PointFromScreen(center).X / behind.ActualWidth;
                var edge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                edge.GradientStops.Add(new GradientStop(Colors.Red, seam));
                edge.GradientStops.Add(new GradientStop(Colors.Blue, seam));
                await SampleOver(edge);
                Assert.True(ScreenColor(center, -4).B > red.B && ScreenColor(center, 4).R > blue.R);

                // 模拟背后窗口的行纹，验证字号和按钮高度的细节不会透进日志区。
                var stripes = new LinearGradientBrush
                {
                    MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0),
                    EndPoint = new Point(32, 0), SpreadMethod = GradientSpreadMethod.Repeat,
                };
                stripes.GradientStops.Add(new GradientStop(Colors.Black, 0.5));
                stripes.GradientStops.Add(new GradientStop(Colors.White, 0.5));
                await SampleOver(stripes);
                var period = (int)Math.Ceiling(32 * VisualTreeHelper.GetDpi(behind).DpiScaleX);
                var stripeLevels = CaptureRow(center).Skip(SampleRadius).Take(period).Select(c => c.G).ToArray();
                output.WriteLine($"{theme} stripe range: {stripeLevels.Min()}..{stripeLevels.Max()}");
                Assert.InRange(stripeLevels.Max() - stripeLevels.Min(), 0, 4);

                gear.IsChecked = true;
                ((Button)settings.FindName("AppearanceButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await UiThread.Drain();
                Assert.True(slider.IsVisible && slider.IsEnabled);
                var blur = (Slider)settings.FindName("GlassBlurSlider");
                Assert.True(blur.IsVisible && blur.IsEnabled);
                var track = (Track)slider.Template.FindName("PART_Track", slider);
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(track.ActualWidth / 4, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                Assert.InRange(h.Vm.GlassTransparency, 30, 40);
                var previousRange = -1;
                foreach (var level in new[] { 0, AppSettings.DefaultGlassTransparency, 100 })
                {
                    slider.SetCurrentValue(RangeBase.ValueProperty, (double)level);
                    Assert.Equal(level, h.Vm.GlassTransparency);
                    var onBlack = await SampleOver(Brushes.Black);
                    var onWhite = await SampleOver(Brushes.White);
                    var range = onWhite.G - onBlack.G;
                    output.WriteLine($"{theme} at {level}%: {onBlack} / {onWhite}");
                    if (level == 0) Assert.Equal(onBlack, onWhite);
                    Assert.True(range > previousRange);
                    previousRange = range;
                }
                if (h.Vm.GlassBlurStep == 1)
                {
                    // Opaque whites and grays in the rear window must not show through as bright shapes.
                    // The dark material should still transmit the chroma of the scene.
                    Assert.InRange(previousRange, 1, 12);
                    if (theme == ThemeMode.Dark)
                    {
                        h.Vm.GlassTransparency = 42;
                        var warm = await SampleOver(new SolidColorBrush(Color.FromRgb(178, 56, 85)));
                        var cool = await SampleOver(new SolidColorBrush(Color.FromRgb(43, 30, 79)));
                        Assert.InRange(warm.R - warm.G, 55, 85);
                        Assert.InRange(cool.B - cool.G, 18, 35);
                        h.Vm.GlassTransparency = 100;
                    }
                }
                var sharpEdge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                sharpEdge.GradientStops.Add(new GradientStop(Color.FromRgb(136, 60, 72), seam));
                sharpEdge.GradientStops.Add(new GradientStop(Color.FromRgb(50, 58, 133), seam));
                var previousEdgeContrast = 2d;
                for (var level = 0; level <= 2; level += h.Vm.GlassBlurStep)
                {
                    blur.SetCurrentValue(RangeBase.ValueProperty, (double)level);
                    await SampleOver(sharpEdge);
                    Assert.Equal(level, h.Vm.GlassBlurLevel);
                    Assert.Equal(100, h.Vm.GlassTransparency);
                    var row = CaptureRow(center);
                    var far = row[SampleRadius + 100].B - row[SampleRadius - 100].B;
                    Assert.True(far > 5, $"{theme} blur {level}: background edge disappeared ({far})");
                    var near = row[SampleRadius + 6].B - row[SampleRadius - 6].B;
                    var contrast = (double)near / far;
                    output.WriteLine($"{theme} blur {level}: edge contrast {contrast:F3}");
                    Assert.True(contrast < previousEdgeContrast - 0.05, $"blur {level}: {contrast:F3}, previous {previousEdgeContrast:F3}");
                    if (level == 0) Assert.InRange(contrast, 0.9, 1.1);
                    previousEdgeContrast = contrast;
                }
                blur.SetCurrentValue(RangeBase.ValueProperty, 0d);
                gear.IsChecked = false;
                Assert.Equal(100, h.Vm.Settings.GlassTransparency);
                Assert.Equal(0, h.Vm.Settings.GlassBlurLevel);

                gear.IsChecked = true;
                ((Button)settings.FindName("AppearanceButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await UiThread.Drain();
                var reset = (Button)settings.FindName("ResetGlassButton");
                var invoke = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(reset).GetPattern(PatternInterface.Invoke));
                invoke.Invoke();
                await UiThread.Drain();
                Assert.Equal(45, slider.Value);
                Assert.Equal(2, blur.Value);
                gear.IsChecked = false;
                Assert.Equal(45, h.Vm.Settings.GlassTransparency);
                Assert.Equal(2, h.Vm.Settings.GlassBlurLevel);
                h.Vm.BackgroundMode = BackdropMode.Solid;
                Assert.Equal(await SampleOver(Brushes.Red), await SampleOver(Brushes.Blue));
            }

            async Task<Color> SampleOver(Brush brush)
            {
                behind.Background = brush;
                await UiThread.Drain();
                await Task.Delay(350);
                return ScreenColor(log.PointToScreen(samplePoint));
            }
        }
        finally
        {
            main.Close();
            await UiThread.Drain();
            behind.Close();
        }
    });

    [DesktopFact]
    public Task StrongGlassTracksTheWindowDuringAnInteractiveMove() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        h.Vm.AutoOpenOnStartup = false;
        h.Vm.BackgroundMode = BackdropMode.Desktop;
        h.Vm.GlassBlurLevel = AppSettings.StrongGlassBlur;
        h.Vm.GlassTransparency = 49;
        var work = SystemParameters.WorkArea;
        var behind = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Left = work.Left, Top = work.Top, Width = work.Width, Height = work.Height, Topmost = true,
        };
        var main = new MainWindow(h.Vm, h.Theme)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = work.Left + 16, Top = work.Top + 16,
            Width = 920, Height = 600, ShowInTaskbar = false, Topmost = true,
        };
        try
        {
            behind.Show();
            main.Owner = behind;
            main.Show();
            main.Activate();
            await h.Connect();
            var hwnd = new WindowInteropHelper(main).Handle;
            var log = (Border)main.FindName("LogCard");
            var probe = new Point(220, 280);
            var start = log.PointToScreen(probe);
            var boundary = new Point(start.X + 40, start.Y);
            var seam = behind.PointFromScreen(boundary).X / behind.ActualWidth;
            var edge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            edge.GradientStops.Add(new GradientStop(Colors.Red, seam));
            edge.GradientStops.Add(new GradientStop(Colors.Blue, seam));
            behind.Background = edge;
            await UiThread.Drain();
            await Task.Delay(500);
            var (before, restingWidth) = SampleGlass(start, boundary);
            Assert.True(before.R > before.B + 15, $"start={before}");
            Assert.True(restingWidth > 35, $"strong blur width={restingWidth}px");

            // 用系统菜单的“移动”和方向键走真实的标题栏移动循环，只向测试窗口投递消息。
            const int wmSysCommand = 0x0112, scMove = 0xF010, wmKeyDown = 0x0100;
            bool moving = false;
            HwndSource.FromHwnd(hwnd)!.AddHook(ObserveMove);
            Assert.True(PostMessage(hwnd, wmSysCommand, scMove, 0));
            try
            {
                for (var step = 0; step < 40 && log.PointToScreen(probe).X < boundary.X + 45; step++)
                {
                    Assert.True(PostMessage(hwnd, wmKeyDown, 0x27, 1)); // VK_RIGHT
                    await Task.Delay(25);
                }
                var at = log.PointToScreen(probe);
                Assert.True(moving && at.X >= boundary.X + 45, $"move loop={moving}, x={at.X}, start={start.X}");
                var (during, movingWidth) = SampleGlass(at, boundary);
                Assert.True(during.B > during.R + 15, $"before={before}, moving={during}");
                output.WriteLine($"Blur width (10–90%): resting={restingWidth}px, moving={movingWidth}px, pixels={before}/{during}");
                Assert.InRange(Math.Abs(movingWidth - restingWidth), 0, 5);
            }
            finally
            {
                PostMessage(hwnd, wmKeyDown, 0x0D, 1); // VK_RETURN
            }
            for (var i = 0; moving && i < 20; i++) await Task.Delay(20);
            Assert.False(moving);
            await Task.Delay(450);
            var (after, finalWidth) = SampleGlass(log.PointToScreen(probe), boundary);
            output.WriteLine($"Released width={finalWidth}px, pixel={after}");
            Assert.True(after.B > after.R + 15, $"released={after}");
            Assert.InRange(Math.Abs(finalWidth - restingWidth), 0, 5);
            Assert.Equal(AppSettings.StrongGlassBlur, h.Vm.GlassBlurLevel);
            Assert.Equal(49, h.Vm.GlassTransparency);

            behind.Background = Brushes.Lime;
            await UiThread.Drain();
            await Task.Delay(250);
            var (updated, _) = SampleGlass(log.PointToScreen(probe), boundary);
            Assert.True(updated.G > updated.B + 15, $"background changed after release={updated}");

            if (h.Vm.GlassBlurStep == 1)
            {
                var surface = GetWindow(hwnd, 2); // GW_HWNDNEXT
                Assert.NotEqual(0, GetWindowLong(surface, -20) & 0x08000000); // WS_EX_NOACTIVATE
                main.WindowState = WindowState.Minimized;
                await UiThread.Drain();
                Assert.False(IsWindowVisible(surface));
                main.WindowState = WindowState.Normal;
                await UiThread.Drain();
                Assert.True(IsWindowVisible(surface));
                main.Width += 48;
                await UiThread.Drain();
                Assert.True(GetWindowRect(hwnd, out var windowBounds));
                Assert.True(GetWindowRect(surface, out var glassBounds));
                Assert.Equal(windowBounds, glassBounds);
                h.Vm.BackgroundMode = BackdropMode.Solid;
                Assert.False(IsWindow(surface));
            }

            nint ObserveMove(nint window, int message, nint wParam, nint lParam, ref bool handled)
            {
                if (message == 0x0231) moving = true;
                if (message == 0x0232) moving = false;
                return 0;
            }
        }
        finally
        {
            main.Close();
            await UiThread.Drain();
            behind.Close();
        }
    });

    [DesktopFact]
    public Task DropDownBlursTheWorkspaceAndRestoresSolidFill() => UiThread.Run(async () =>
    {
        await using var h = new AppHarness();
        h.Vm.AutoOpenOnStartup = false;
        var work = SystemParameters.WorkArea;
        var behind = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Left = work.Left, Top = work.Top, Width = work.Width, Height = work.Height, Topmost = true,
        };
        var main = new MainWindow(h.Vm, h.Theme)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = work.Left, Top = work.Top,
            Width = 920, Height = 600, ShowInTaskbar = false, Topmost = true,
        };
        try
        {
            behind.Show();
            main.Owner = behind;
            main.Show();
            main.Activate();
            var root = (Grid)((Border)main.Content).Child;
            var probe = new Border { IsHitTestVisible = false };
            root.Children.Add(probe);
            var combo = (ComboBox)main.FindName("BaudRateCombo");
            foreach (var theme in new[] { ThemeMode.Dark, ThemeMode.Light })
            {
                h.Vm.SelectedTheme = theme;
                h.Vm.BackgroundMode = BackdropMode.Desktop;
                behind.Background = theme == ThemeMode.Dark ? Brushes.White : Brushes.Black;
                probe.Background = null;
                await Task.Delay(450);
                var log = (Border)main.FindName("LogCard");
                var logPoint = log.PointToScreen(new Point(180, 380));
                var logBeforeOpening = ScreenColor(logPoint);
                combo.IsDropDownOpen = true;
                await UiThread.Drain();
                var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                var pane = Assert.IsType<GlassFlyout>(popup.Child);
                var scroll = Assert.IsType<ScrollViewer>(pane.Child);
                Assert.NotEqual(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
                Assert.Equal(0, scroll.ScrollableHeight);
                var lastOption = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(combo.Items.Count - 1));
                Assert.Equal(MainViewModel.CustomBaudRateItem, lastOption.Content);
                Assert.InRange(lastOption.TranslatePoint(new Point(0, lastOption.ActualHeight), scroll).Y, 0, scroll.ActualHeight);
                Assert.True(pane.UseGlass);
                if (SystemParameters.ClientAreaAnimation) Assert.True(pane.HasAnimatedProperties);
                await Task.Delay(200);
                var logAfterOpening = ScreenColor(logPoint);
                // 合成帧切换时色调可能略有变化，接近纯白或纯黑则表示主窗口失去了磨砂层。
                Assert.InRange(Math.Abs(logBeforeOpening.G - logAfterOpening.G), 0, 10);
                var point = pane.PointToScreen(new Point(pane.ActualWidth - 31, 16));
                var red = await Paint(Brushes.Red);
                var blue = await Paint(Brushes.Blue);
                Assert.True(red.R > blue.R + 50 && blue.B > red.B + 50, $"{theme}: {red}, {blue}");
                Assert.Equal(1, pane.Opacity);
                var black = await Paint(Brushes.Black);
                var white = await Paint(Brushes.White);
                output.WriteLine($"{theme} dropdown over black/white: {black} / {white}");
                Assert.InRange(white.G - black.G, 50, 75);
                var seam = probe.PointFromScreen(point).X / probe.ActualWidth;
                var edge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                edge.GradientStops.Add(new GradientStop(Colors.Red, seam));
                edge.GradientStops.Add(new GradientStop(Colors.Blue, seam));
                await Paint(edge);
                var left = ScreenColor(point, -3);
                var right = ScreenColor(point, 3);
                Assert.True(left.B > red.B + 1 && right.R > blue.R + 1,
                    $"{theme}: red {red}, left edge {left}, right edge {right}, blue {blue}");
                h.Vm.BackgroundMode = BackdropMode.Solid;
                Assert.Equal(await Paint(Brushes.Red), await Paint(Brushes.Blue));
                combo.IsDropDownOpen = false;

                async Task<Color> Paint(Brush brush)
                {
                    probe.Background = brush;
                    await UiThread.Drain();
                    await Task.Delay(200);
                    return ScreenColor(point);
                }
            }
        }
        finally
        {
            main.Close();
            await UiThread.Drain();
            behind.Close();
        }
    });

    private const int SampleRadius = 128;

    private static Color ScreenColor(Point point, int offsetX = 0) => CaptureRow(point)[SampleRadius + offsetX];

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern int GetPixel(nint dc, int x, int y);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    // 一次复制包含边缘及其模糊邻域的同一帧，避免比较不同合成帧的像素。
    private static (Color Color, int EdgeWidth) SampleGlass(Point sample, Point edge)
    {
        var row = CaptureRow(edge);
        var red = row.Skip(SampleRadius - 96).Take(193).Select(c => c.R).ToArray();
        var range = red[0] - red[^1];
        var first = Array.FindIndex(red, r => r <= red[^1] + range * 0.9);
        var last = Array.FindIndex(red, r => r <= red[^1] + range * 0.1);
        return (row[SampleRadius + (int)sample.X - (int)edge.X], last - first);
    }

    private static Color[] CaptureRow(Point center)
    {
        const int size = 2 * SampleRadius + 1;
        DwmFlush();
        var screen = GetDC(0);
        var copy = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, size, size);
        var previous = SelectObject(copy, bitmap);
        try
        {
            Assert.True(BitBlt(copy, 0, 0, size, size, screen, (int)center.X - SampleRadius, (int)center.Y - SampleRadius, 0x40CC0020));
            var row = new Color[size];
            for (var x = 0; x < size; x++)
            {
                var rgb = GetPixel(copy, x, SampleRadius);
                Assert.NotEqual(-1, rgb);
                row[x] = Color.FromRgb((byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16));
            }
            return row;
        }
        finally
        {
            SelectObject(copy, previous);
            DeleteObject(bitmap);
            DeleteDC(copy);
            ReleaseDC(0, screen);
        }
    }
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint src, int sx, int sy, int rop);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);
}
