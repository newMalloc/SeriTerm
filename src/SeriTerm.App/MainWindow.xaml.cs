using System.ComponentModel;
using System.Windows;
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
