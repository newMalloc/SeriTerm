using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SeriTerm.App.Services;
using SeriTerm.App.ViewModels;

namespace SeriTerm.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IThemeService _themeService;

    /// <summary>模糊过的桌面壁纸；生成成功后一直复用，为 null 表示背景模糊不可用。</summary>
    private ImageSource? _backdropImage;

    private bool _backdropUpdateQueued;

    private IntPtr _savedImeContext;
    private bool _imeDetached;

    public MainWindow(MainViewModel viewModel, IThemeService themeService)
    {
        _viewModel = viewModel;
        _themeService = themeService;

        InitializeComponent();
        DataContext = viewModel;

        RestoreWindowPlacement();

        // 系统标题栏也要跟着主题走，否则深色界面顶着一个白色标题栏。
        // 说明：Windows 10 上这个 DWM 属性会被接受但并不生效（实测返回 S_OK、读回 1，
        // 标题栏仍是白条），真正让顶部跟随主题的是本窗口自绘的标题栏；
        // 保留这次调用是为了 Windows 11 上的边框/系统菜单也跟着走。
        SourceInitialized += (_, _) =>
        {
            TitleBarTheme.Apply(this, _themeService.IsDarkEffective);
            ApplyTerminalInputMethod(_viewModel.TerminalMode);
            HookWindowMessages(new WindowInteropHelper(this).Handle);
            ApplyBackdrop();
        };

        _themeService.ThemeChanged += (_, _) =>
        {
            TitleBarTheme.Apply(this, _themeService.IsDarkEffective);
            ApplyBackdrop();
        };

        // 终端模式下要关掉输入法，见 ApplyTerminalInputMethod 的说明
        viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.TerminalMode):
                    ApplyTerminalInputMethod(viewModel.TerminalMode);
                    break;

                case nameof(MainViewModel.BlurBackground):
                    ApplyBackdrop();
                    break;
            }
        };

        // WPF 在窗口获得焦点时会重新关联输入法上下文，所以激活/加载后要再摘一次
        Activated += (_, _) => ReapplyImeState();
        Loaded += (_, _) => ReapplyImeState();

        // 背景按屏幕坐标对齐，窗口一动就要重新取壁纸里对应的那一块
        LocationChanged += (_, _) => QueueBackdropViewboxUpdate();
        SizeChanged += (_, _) => QueueBackdropViewboxUpdate();
        StateChanged += (_, _) => QueueBackdropViewboxUpdate();

        ApplyTerminalInputMethod(viewModel.TerminalMode);

        Loaded += async (_, _) => await viewModel.InitializeAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 全局快捷键与终端模式按键路由。用 PreviewKeyDown 而不是 InputBindings：
    /// InputBindings 里的 KeyBinding 不参与 DataContext 继承，绑定命令很容易失效。
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var key = ResolveKey(e);

        // 终端模式：键盘直接进串口；焦点在输入框里时不抢键（那里是正常编辑）
        if (_viewModel.TerminalMode && !IsTextInputFocused() && TryHandleTerminalKey(key, e, control))
        {
            return;
        }

        switch (key)
        {
            case Key.F when control:
                LogViewControl.FocusSearch();
                e.Handled = true;
                return;

            case Key.K when control:
                _viewModel.ClearLogCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.S when control:
                _viewModel.SaveLogCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.T when control && shift:
                Topmost = !Topmost;
                e.Handled = true;
                return;

            case Key.Escape when _viewModel.SearchVisible:
                _viewModel.CloseSearchCommand.Execute(null);
                e.Handled = true;
                return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// 取真实按键。装了中文输入法时，WPF 会把普通按键报成 <see cref="Key.ImeProcessed"/>
    /// （真实按键在 <see cref="KeyEventArgs.ImeProcessedKey"/> 里），
    /// 不还原的话终端模式的回车、退格、Ctrl+C 全都会被漏掉。
    /// </summary>
    private static Key ResolveKey(KeyEventArgs e) => e.Key switch
    {
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.System => e.SystemKey,
        _ => e.Key,
    };

    /// <summary>普通字符走 TextInput 更可靠：能正确处理输入法、Shift 与键盘布局。</summary>
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);

        if (!_viewModel.TerminalMode || IsTextInputFocused() || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        _ = _viewModel.TerminalInputAsync(e.Text);
        e.Handled = true;
    }

    private bool TryHandleTerminalKey(Key key, KeyEventArgs e, bool control)
    {
        switch (key)
        {
            case Key.Enter:
                _ = _viewModel.TerminalEnterAsync();
                e.Handled = true;
                return true;

            case Key.Back:
                _ = _viewModel.TerminalBackspaceAsync();
                e.Handled = true;
                return true;

            case Key.C when control:
                // 焦点在日志列表里时不抢：那时 Ctrl+C 的意思是"复制选中的日志行"
                if (LogViewControl.IsLogListFocused)
                {
                    return false;
                }

                _ = _viewModel.TerminalControlAsync('C');
                e.Handled = true;
                return true;

            case Key.V when control:
                if (!Clipboard.ContainsText())
                {
                    return false;
                }

                _ = _viewModel.TerminalPasteAsync(Clipboard.GetText());
                e.Handled = true;
                return true;

            default:
                // 方向键等留给日志区自己处理
                return false;
        }
    }

    /// <summary>
    /// 终端模式下关闭输入法：否则按键会先进系统输入法的组合过程，
    /// 回车/退格/Ctrl+C 会被输入法吃掉（WPF 只报 Key.ImeProcessed），终端根本收不到。
    /// 这里做两件事：WPF 层面禁用输入法，以及把窗口的输入法上下文摘掉。
    /// </summary>
    private void ApplyTerminalInputMethod(bool terminalMode)
    {
        InputMethod.SetIsInputMethodEnabled(this, !terminalMode);
        InputMethod.SetPreferredImeState(this, terminalMode ? InputMethodState.Off : InputMethodState.On);

        if (InputMethod.Current is { } current)
        {
            current.ImeState = terminalMode ? InputMethodState.Off : InputMethodState.On;
        }

        if (terminalMode)
        {
            DetachIme();
        }
        else
        {
            RestoreIme();
        }
    }

    private void DetachIme()
    {
        var handle = new WindowInteropHelper(this).Handle;

        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (!_imeDetached)
        {
            _savedImeContext = ImeControl.Detach(handle);
            _imeDetached = true;
            return;
        }

        ImeControl.Detach(handle);
    }

    /// <summary>窗口重新激活后 WPF 可能又把输入法上下文装回去了，这里强制再摘一次。</summary>
    private void ReapplyImeState()
    {
        if (_viewModel.TerminalMode)
        {
            _imeDetached = false;
            DetachIme();
        }
    }

    private void RestoreIme()
    {
        if (!_imeDetached)
        {
            return;
        }

        ImeControl.Restore(new WindowInteropHelper(this).Handle, _savedImeContext);
        _savedImeContext = IntPtr.Zero;
        _imeDetached = false;
    }

    private static bool IsTextInputFocused()
        => Keyboard.FocusedElement is TextBox or ComboBox or PasswordBox;

    // ---------- 波特率"自定义输入" ----------

    /// <summary>
    /// 波特率下拉框最后一项是「自定义输入…」，它不是波特率，而是"切到手动输入"的开关。
    /// 选中后把输入框清空、光标交还给用户，直接键入任意波特率即可。
    /// 清空必须排到 Input 优先级再执行：ComboBox 会在本次选择处理中把选中项的文本
    /// 同步进输入框，立刻清会被它覆盖回「自定义输入…」。
    /// </summary>
    private void OnBaudRateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BaudRateCombo.SelectedItem is not string item
            || !string.Equals(item, MainViewModel.CustomBaudRateItem, StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            BaudRateCombo.Text = string.Empty;

            // 光标直接落到内层输入框，省得用户再点一下
            if (BaudRateCombo.Template?.FindName("PART_EditableTextBox", BaudRateCombo) is TextBox box)
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }
            else
            {
                BaudRateCombo.Focus();
            }
        });
    }

    // ---------- 自绘标题栏 ----------

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---------- 背景模糊 ----------

    private void HookWindowMessages(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        HwndSource.FromHwnd(handle)?.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            // 自绘标题栏的窗口（WindowStyle=None + GlassFrameThickness=0）客户区就是整个窗口，
            // WPF 却按"无边框全屏"处理最大化，客户区会盖住任务栏；这里改成按显示器工作区。
            case WmGetMinMaxInfo:
                ApplyWorkAreaLimits(hwnd, lParam);
                handled = true;
                break;

            // 换壁纸后重新取一次（系统只在设置变化时广播，不频繁）
            case WmSettingChange when IsDesktopSettingChange(lParam):
                ReloadBackdrop();
                break;
        }

        return IntPtr.Zero;
    }

    private static void ApplyWorkAreaLimits(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        limits.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        limits.MaxSize.X = info.Work.Right - info.Work.Left;
        limits.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        limits.MaxTrackSize = limits.MaxSize;
        Marshal.StructureToPtr(limits, lParam, false);
    }

    private static bool IsDesktopSettingChange(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
        {
            return true;
        }

        var area = Marshal.PtrToStringAuto(lParam);
        return string.IsNullOrEmpty(area)
            || area.Equals("Desktop", StringComparison.OrdinalIgnoreCase)
            || area.Equals("Environment", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 按当前设置与主题重新决定背景：模糊背景真正可用时才把表面画刷换成半透明，
    /// 否则保持纯色（半透明却没有背景层时，用户看到的是窗口后面的黑/桌面，比纯色更难看）。
    ///
    /// 这里直接读 <see cref="MainViewModel.BlurBackground"/> 而不是等属性通知——
    /// 默认值与配置值都为 true 时通知不会触发（和自动重连踩过的是同一个坑）。
    /// </summary>
    private void ApplyBackdrop()
    {
        _backdropImage ??= DesktopBackdrop.Create();

        var live = _viewModel.BlurBackground && _backdropImage is not null;

        if (live)
        {
            BackdropBrush.ImageSource = _backdropImage;
            UpdateBackdropViewbox();
        }
        else
        {
            BackdropBrush.ImageSource = null;
        }

        var visibility = live ? Visibility.Visible : Visibility.Collapsed;
        BackdropLayer.Visibility = visibility;
        BackdropTint.Visibility = visibility;

        SurfaceTranslucency.SetEnabled(_themeService.ActiveTheme, live);
    }

    private void ReloadBackdrop()
    {
        _backdropImage = null;
        ApplyBackdrop();
    }

    /// <summary>
    /// 把窗口在屏幕上的位置换算成壁纸里对应的那一块（而不是把整张壁纸压进窗口），
    /// 这样窗口移动/缩放时背景始终与桌面重合。
    ///
    /// 用 GetWindowRect 而不是 Window.Left/ActualWidth：最大化过程中 WPF 的这几个属性
    /// 会有一段时间是旧值，算出来的 Viewbox 会超出图像范围，右侧/底部露出一条没有背景的竖带。
    /// </summary>
    private void UpdateBackdropViewbox()
    {
        if (BackdropLayer.Visibility != Visibility.Visible)
        {
            return;
        }

        var screenWidth = SystemParameters.VirtualScreenWidth;
        var screenHeight = SystemParameters.VirtualScreenHeight;
        var handle = new WindowInteropHelper(this).Handle;

        if (screenWidth <= 0 || screenHeight <= 0 || handle == IntPtr.Zero
            || !GetWindowRect(handle, out var rect))
        {
            return;
        }

        // GetWindowRect 是物理像素，Viewbox 用 DIP
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;

        var x0 = Math.Clamp(((rect.Left / scale) - virtualLeft) / screenWidth, 0, 1);
        var y0 = Math.Clamp(((rect.Top / scale) - virtualTop) / screenHeight, 0, 1);
        var x1 = Math.Clamp(((rect.Right / scale) - virtualLeft) / screenWidth, 0, 1);
        var y1 = Math.Clamp(((rect.Bottom / scale) - virtualTop) / screenHeight, 0, 1);

        BackdropBrush.Viewbox = new Rect(
            x0,
            y0,
            Math.Max(0.01, x1 - x0),
            Math.Max(0.01, y1 - y0));
    }

    /// <summary>窗口移动/缩放/最大化时合并成一次更新，避免拖动过程中反复重算。</summary>
    private void QueueBackdropViewboxUpdate()
    {
        if (_backdropUpdateQueued)
        {
            return;
        }

        _backdropUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _backdropUpdateQueued = false;

            // 关窗过程中排队的回调仍会被执行，此时窗口已不是 Loaded、
            // 取 HWND 会抛异常（而且异常处理里再去取已释放的 DI 容器会二次抛出，
            // 直接把"退出"变成崩溃弹窗），所以这里必须先确认窗口还活着。
            if (IsLoaded && !Dispatcher.HasShutdownStarted)
            {
                UpdateBackdropViewbox();
            }
        });
    }

    private const int WmSettingChange = 0x001A;
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private void RestoreWindowPlacement()
    {
        var settings = _viewModel.Settings;

        if (settings.WindowWidth >= 640)
        {
            Width = settings.WindowWidth;
        }

        if (settings.WindowHeight >= 420)
        {
            Height = settings.WindowHeight;
        }

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 记录窗口尺寸供下次启动恢复（最大化时用 RestoreBounds 记住还原后的尺寸）
        var settings = _viewModel.Settings;
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        settings.WindowMaximized = WindowState == WindowState.Maximized;

        base.OnClosing(e);
    }
}
