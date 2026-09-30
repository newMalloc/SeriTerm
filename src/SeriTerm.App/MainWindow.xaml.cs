using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using SeriTerm.App.Services;
using SeriTerm.App.ViewModels;

namespace SeriTerm.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IThemeService _themeService;

    public MainWindow(MainViewModel viewModel, IThemeService themeService)
    {
        _viewModel = viewModel;
        _themeService = themeService;

        InitializeComponent();
        DataContext = viewModel;

        RestoreWindowPlacement();

        // 系统标题栏也要跟着主题走，否则深色界面顶着一个白色标题栏
        SourceInitialized += (_, _) => TitleBarTheme.Apply(this, _themeService.IsDarkEffective);
        _themeService.ThemeChanged += (_, _) => TitleBarTheme.Apply(this, _themeService.IsDarkEffective);

        Loaded += async (_, _) => await viewModel.InitializeAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 全局快捷键。用 PreviewKeyDown 而不是 InputBindings：
    /// InputBindings 里的 KeyBinding 不参与 DataContext 继承，绑定命令很容易失效。
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
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
