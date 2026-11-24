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
    private ResourceDictionary? _surfaceResources;
    private bool _desktopBlurActive;
    private CompositionGlassLayer? _glassLayer;
    private Color _previousTargetColor;
    private bool _systemBackdropUpdateQueued;

    private IntPtr _savedImeContext;
    private bool _imeDetached;
    private bool _closing;
    private bool _shutdownCompleted;

    private async void OnPortSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: PortItem port } combo &&
            (combo.IsKeyboardFocusWithin || combo.IsDropDownOpen))
            await _viewModel.SwitchOpenPortAsync(port);
    }

    public MainWindow(MainViewModel viewModel, IThemeService themeService)
    {
        _viewModel = viewModel;
        _themeService = themeService;

        InitializeComponent();
        DataContext = viewModel;

        StateChanged += (_, _) => UpdateResizeBorder();
        RestoreWindowPlacement();
        UpdateResizeBorder();
        Topmost = viewModel.Settings.WindowTopmost;

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
            if (viewModel.KeepDisplayAwake)
                SetKeepDisplayAwake(true);
        };

        _themeService.ThemeChanged += OnThemeChanged;

        // 终端模式下要关掉输入法，见 ApplyTerminalInputMethod 的说明
        viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.TerminalMode):
                    ApplyTerminalInputMethod(viewModel.TerminalMode);
                    break;

                case nameof(MainViewModel.BackgroundMode):
                case nameof(MainViewModel.BackgroundImagePath):
                    ApplyBackdrop();
                    break;

                case nameof(MainViewModel.GlassBlurLevel):
                    if (viewModel.BackgroundMode == BackdropMode.Desktop)
                        ApplyBackdrop();
                    break;

                case nameof(MainViewModel.GlassTransparency):
                    UpdateGlassTransparency();
                    break;

                case nameof(MainViewModel.KeepDisplayAwake):
                    SetKeepDisplayAwake(viewModel.KeepDisplayAwake);
                    break;
            }
        };

        // WPF 在窗口获得焦点时会重新关联输入法上下文，所以激活/加载后要再摘一次
        Activated += (_, _) => ReapplyImeState();
        Loaded += (_, _) => ReapplyImeState();
        Deactivated += (_, _) =>
        {
            if (!_choosingBackgroundImage)
                CloseSettings(restoreFocus: false);
        };

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

        if (HandleSettingsKey(key, e, control, shift))
            return;

        // 终端模式：键盘直接进串口；焦点在输入框里时不抢键（那里是正常编辑）
        if (_viewModel.TerminalMode && !IsTextInputFocused() && TryHandleTerminalKey(key, e, control))
        {
            return;
        }

        switch (key)
        {
            case Key.Enter when control && SendTextBox.IsKeyboardFocusWithin:
                if (_viewModel.SendCommand.CanExecute(null)) _viewModel.SendCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.F5:
                if (_viewModel.ToggleOpenCommand.CanExecute(null)) _viewModel.ToggleOpenCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.F6:
                if (!e.IsRepeat && _viewModel.TogglePauseCommand.CanExecute(null)) _viewModel.TogglePauseCommand.Execute(null);
                e.Handled = true;
                return;

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
                LogViewControl.CloseSearch();
                e.Handled = true;
                return;

            // F1 = 关于。终端模式下不受影响：F1 不在 TryHandleTerminalKey 处理的键里
            case Key.F1:
                ShowAbout();
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

        if (SettingsOpen || !_viewModel.TerminalMode || IsTextInputFocused() || string.IsNullOrEmpty(e.Text))
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
                // 日志区有焦点时让编辑器处理 Ctrl+C。
                if (LogViewControl.IsLogTextFocused)
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

    private void OnBaudRateDropDownChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (BaudRateCombo.Template?.FindName("PART_EditableTextBox", BaudRateCombo) is TextBox box)
                box.Select(box.SelectionStart + box.SelectionLength, 0);
        });
    }

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

    private void UpdateResizeBorder()
    {
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome is not null)
            chrome.ResizeBorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(6);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 日志卡片的圆角裁剪。Border 只会把"自己的边框"画成圆角，子元素不跟着裁：
    /// 日志行选中/命中的底色是整行方块，会在四角的圆弧外面露出四个小方角。
    /// 卡片尺寸一变就换一块同样圆角的裁剪区，行底色就老老实实待在圆角里。
    /// </summary>
    private void OnLogCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border card && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            card.Clip = new RectangleGeometry(
                new Rect(new Point(), e.NewSize),
                card.CornerRadius.TopLeft,
                card.CornerRadius.TopLeft);
        }
    }

    /// <summary>「关于」窗口。每次点都新建：它只是一次性的小对话框，没必要缓存。</summary>
    private void OnAboutClick(object sender, RoutedEventArgs e) => ShowAbout();

    private void ShowAbout()
    {
        CloseSettings();
        new AboutWindow(_themeService) { Owner = this }.ShowDialog();
    }

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
            case WmNcLeftButtonDown when wParam.ToInt64() == HitCaption:
            case WmNcLeftButtonDoubleClick when wParam.ToInt64() == HitCaption:
                CloseSettings(restoreFocus: false);
                break;

            // 自绘标题栏的客户区覆盖整个窗口，
            // WPF 却按"无边框全屏"处理最大化，客户区会盖住任务栏；这里改成按显示器工作区。
            case WmGetMinMaxInfo:
                ApplyWorkAreaLimits(hwnd, lParam);
                handled = true;
                break;

            case WmSettingChange when _themeService.Current == ThemeMode.System &&
                string.Equals(Marshal.PtrToStringAuto(lParam), "ImmersiveColorSet", StringComparison.OrdinalIgnoreCase):
                _themeService.Apply(ThemeMode.System);
                break;

            case WmSettingChange:
            case WmDwmCompositionChanged:
                QueueSystemBackdropUpdate();
                break;
            case 0x0047: // WM_WINDOWPOSCHANGED
                _glassLayer?.SyncPosition();
                break;
        }

        return IntPtr.Zero;
    }

    private void ApplyWorkAreaLimits(IntPtr hwnd, IntPtr lParam)
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

        // 最小尺寸要在这里补回来：WPF 落实 MinWidth/MinHeight 靠的就是这条消息，
        // 而本窗口自己处理它并标记"已处理"，WPF 那一步就被整条跳过了
        // （发 WM_GETMINMAXINFO 回读 ptMinTrackSize 是 0×0）。
        // 少了它，窗口能被拖到比内容还窄，而布局依旧按 MinWidth 排，
        // 右边那一截就落到窗口外被裁掉——实测窗口 852.7 DIP 时右侧 67 DIP 全被切掉
        // （标题栏按钮、查找浮层、发送按钮都缺一块）。
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;

        limits.MinTrackSize.X = (int)Math.Min(
            limits.MaxTrackSize.X,
            Math.Ceiling((double.IsNaN(MinWidth) ? 0 : MinWidth) * scale));
        limits.MinTrackSize.Y = (int)Math.Min(
            limits.MaxTrackSize.Y,
            Math.Ceiling((double.IsNaN(MinHeight) ? 0 : MinHeight) * scale));

        Marshal.StructureToPtr(limits, lParam, false);
    }

    private void QueueSystemBackdropUpdate()
    {
        if (_systemBackdropUpdateQueued || _closing || _viewModel.BackgroundMode != BackdropMode.Desktop)
            return;

        _systemBackdropUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _systemBackdropUpdateQueued = false;
            if (!_closing && _viewModel.BackgroundMode == BackdropMode.Desktop)
                ApplyBackdrop();
        });
    }

    private void OnThemeChanged(object? sender, ThemeMode mode)
    {
        if (_closing) return;
        TitleBarTheme.Apply(this, _themeService.IsDarkEffective);
        ApplyBackdrop();
    }

    private void ApplyBackdrop()
    {
        if (_closing) return;

        var handle = new WindowInteropHelper(this).Handle;
        var source = handle == IntPtr.Zero ? null : HwndSource.FromHwnd(handle);
        var mode = _viewModel.BackgroundMode;
        var useGlass = source is not null && mode == BackdropMode.Desktop && DesktopBackdrop.IsAllowed();
        var useCustomGlass = useGlass && !DesktopBackdrop.UsesSystemAcrylic && _viewModel.GlassBlurLevel == AppSettings.StrongGlassBlur;
        if (_glassLayer is not null && (!useCustomGlass || _glassLayer.IsDark != _themeService.IsDarkEffective))
        {
            _glassLayer.Dispose();
            _glassLayer = null;
        }
        if (useCustomGlass)
            _glassLayer ??= CompositionGlassLayer.TryCreate(handle, _themeService.IsDarkEffective, UpdateGlassTransparency);
        // 模糊只作用于下方的实时背景。让 WPF 以透明底色覆盖它，避免二次读取合成结果。
        bool live = false;
        if (_glassLayer is not null)
        {
            live = DesktopBackdrop.SetMaterial(handle, 0);
            if (!live)
            {
                _glassLayer.Dispose();
                _glassLayer = null;
            }
        }
        if (!live && useGlass)
            live = DesktopBackdrop.SetMaterial(handle, _viewModel.GlassBlurLevel);
        if (!live && _desktopBlurActive && source is not null)
            DesktopBackdrop.SetMaterial(source.Handle, null);

        bool changingMaterial = _desktopBlurActive != live;
        if (changingMaterial && live && source?.CompositionTarget is { } previousTarget)
            _previousTargetColor = previousTarget.BackgroundColor;
        if (source is not null && (live || changingMaterial) && DesktopBackdrop.UsesSystemAcrylic)
        {
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            chrome.GlassFrameThickness = live && _viewModel.GlassBlurLevel > 0 ? new Thickness(-1) : new Thickness(0);
        }
        _desktopBlurActive = live;

        var image = mode == BackdropMode.Image ? GetCustomWallpaper() : null;
        BackdropBrush.ImageSource = image;
        BackdropLayer.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        ImageTint.Visibility = BackdropLayer.Visibility;
        DesktopTint.Visibility = live ? Visibility.Visible : Visibility.Collapsed;

        var effectiveMode = live || image is not null ? mode : BackdropMode.Solid;
        var surfaces = SurfaceTranslucency.Create(_themeService.ActiveTheme, effectiveMode);
        if (_surfaceResources is not null)
            Resources.MergedDictionaries.Remove(_surfaceResources);
        if (surfaces is not null)
            Resources.MergedDictionaries.Add(surfaces);
        _surfaceResources = surfaces;
        UpdateGlassTransparency();

        // 零玻璃边距下 WindowChrome 默认用系统窗口色清屏，会完全盖住 Windows 10 的实时背景。
        if ((live || changingMaterial) && source?.CompositionTarget is { } target)
            target.BackgroundColor = live ? Color.FromArgb(0, 0, 0, 0) : _previousTargetColor;
    }

    private void UpdateGlassTransparency()
    {
        if (_desktopBlurActive && _surfaceResources is not null)
            SurfaceTranslucency.SetGlassTransparency(_surfaceResources, _viewModel.GlassTransparency, _glassLayer?.ToneActive == true);
    }

    internal void SyncBackdropAfterPopupOpens()
    {
        if (_glassLayer is null) return;

        // WPF 显示子弹窗时，Windows 会调整拥有者的 Z 顺序。等弹窗就位再把合成层移回主窗口正下方。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _glassLayer?.SyncPosition());
    }

    private const int WmSettingChange = 0x001A;
    private const int WmDwmCompositionChanged = 0x031E;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int WmNcLeftButtonDoubleClick = 0x00A3;
    private const int HitCaption = 2;
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>窗口所在显示器的 DPI（Windows 10 1607+，本程序最低支持 1809）。</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

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
            Width = Math.Max(MinWidth, Math.Min(settings.WindowWidth, SystemParameters.WorkArea.Width));
        }

        if (settings.WindowHeight >= 420)
        {
            Height = Math.Max(MinHeight, Math.Min(settings.WindowHeight, SystemParameters.WorkArea.Height));
        }

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_shutdownCompleted || e.Cancel)
        {
            return;
        }

        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        CloseSettings(restoreFocus: false);
        // 即使清理同步完成，下一次 Close 也必须发生在本轮 Closing 返回之后。
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(BeginShutdown));
    }

    private async void BeginShutdown()
    {
        // 记录窗口尺寸供下次启动恢复（最大化时用 RestoreBounds 记住还原后的尺寸）
        var settings = _viewModel.Settings;
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        settings.WindowTopmost = Topmost;

        // Keep the dispatcher alive while stopping sends and flushing log queues.
        // Blocking in Application.OnExit can strand workers waiting for this thread.
        IsEnabled = false;
        try
        {
            _viewModel.PersistSettings();
            await _viewModel.DisposeAsync();
        }
        finally
        {
            _shutdownCompleted = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _glassLayer?.Dispose();
        _themeService.ThemeChanged -= OnThemeChanged;
        if (_viewModel.KeepDisplayAwake)
            SetKeepDisplayAwake(false);
        base.OnClosed(e);
    }
}
