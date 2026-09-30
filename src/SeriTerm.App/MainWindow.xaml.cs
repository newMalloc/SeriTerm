using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using SeriTerm.App.Services;
using SeriTerm.App.ViewModels;

namespace SeriTerm.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IThemeService _themeService;

    private IntPtr _savedImeContext;
    private bool _imeDetached;

    public MainWindow(MainViewModel viewModel, IThemeService themeService)
    {
        _viewModel = viewModel;
        _themeService = themeService;

        InitializeComponent();
        DataContext = viewModel;

        RestoreWindowPlacement();

        // 系统标题栏也要跟着主题走，否则深色界面顶着一个白色标题栏
        SourceInitialized += (_, _) =>
        {
            TitleBarTheme.Apply(this, _themeService.IsDarkEffective);
            ApplyTerminalInputMethod(_viewModel.TerminalMode);
        };

        _themeService.ThemeChanged += (_, _) => TitleBarTheme.Apply(this, _themeService.IsDarkEffective);

        // 终端模式下要关掉输入法，见 ApplyTerminalInputMethod 的说明
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TerminalMode))
            {
                ApplyTerminalInputMethod(viewModel.TerminalMode);
            }
        };

        // WPF 在窗口获得焦点时会重新关联输入法上下文，所以激活/加载后要再摘一次
        Activated += (_, _) => ReapplyImeState();
        Loaded += (_, _) => ReapplyImeState();
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
