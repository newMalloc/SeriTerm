using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeriTerm.App.Services;

namespace SeriTerm.App;

/// <summary>
/// 「关于」窗口：版本号、提交、运行时、许可与 GitHub 链接。
/// 数据全部来自 <see cref="AppInfo"/>（它读的是程序集属性，不是写死的字符串）。
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>图标显示尺寸（DIP）。</summary>
    private const double IconSize = 64;

    private readonly IThemeService _themeService;

    public AboutWindow(IThemeService themeService)
    {
        _themeService = themeService;

        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            if (Owner is MainWindow main)
                Resources.MergedDictionaries.Add(main.Resources);
            TitleBarTheme.Apply(this, _themeService.IsDarkEffective);
        };

        VersionText.Text = AppInfo.Version;
        RuntimeText.Text = AppInfo.RuntimeDescription;
        LicenseText.Text = $"{AppInfo.LicenseName} · {AppInfo.CopyrightHolder}";
        ThirdPartyText.Text = AppInfo.ThirdPartyNotice;

        // 提交号来自 SourceLink 注入的信息版本；用 dotnet build 直接编译时可能没有，
        // 那时整行（标签也一起）收起来，而不是留个空行或"—"
        if (AppInfo.CommitHash is { } commit)
        {
            CommitText.Text = AppInfo.ShortCommitHash!;
            CommitText.ToolTip = $"完整提交号：{commit}";
        }
        else
        {
            CommitLabel.Visibility = Visibility.Collapsed;
            CommitText.Visibility = Visibility.Collapsed;
        }

        // 网址只在 AppInfo 里写一份，这里的悬停提示与按钮动作都从它取
        RepositoryButton.ToolTip = AppInfo.RepositoryUrl;
        ReleasesButton.ToolTip = AppInfo.ReleasesUrl;

        Loaded += (_, _) => UpdateIconFrame();

        // 窗口没活多久，但主题服务是单例，不摘钩子就会一直攥着一个死窗口
        _themeService.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => _themeService.ThemeChanged -= OnThemeChanged;
    }

    /// <summary>窗口被拖到另一块不同 DPI 的屏幕上时，重新挑一次图标帧。</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateIconFrame();
    }

    /// <summary>
    /// Esc 关窗。原来靠那颗「关闭」按钮的 IsCancel，按钮去掉之后必须自己接——
    /// 否则这个窗口只剩标题栏的 ✕ 一条退路，键盘用户被关在里面。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnThemeChanged(object? sender, ThemeMode mode) => TitleBarTheme.Apply(this, _themeService.IsDarkEffective);

    /// <summary>
    /// 从 ICO 里挑一帧来显示。
    ///
    /// 不能直接用 <c>Source="Assets/seriterm.ico"</c> 的默认帧：WPF 解 ICO 时给的默认帧是
    /// 第 0 帧，也就是 16×16 那帧（图标帧按尺寸升序排列），把它拉到 64 DIP 显示会明显发糊。
    /// 这里按"当前 DPI 下真正需要多少物理像素"选最接近的一帧；打平时取大的那帧
    /// （缩小比放大清晰）。150% 缩放下 64 DIP = 96 px，因此 64 与 128 打平 → 选 128。
    /// </summary>
    private void UpdateIconFrame()
    {
        if (AppIcon.Source is not BitmapFrame frame || frame.Decoder is not { } decoder)
        {
            return;
        }

        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var targetPixels = IconSize * (dpiScale > 0 ? dpiScale : 1.0);

        var best = decoder.Frames
            .OrderBy(f => Math.Abs(f.PixelWidth - targetPixels))
            .ThenByDescending(f => f.PixelWidth)
            .First();

        // 换帧不会改变解码器本身，所以重复调用（Load + DpiChanged）是安全的
        AppIcon.Source = best;
    }

    private void OnOpenRepositoryClick(object sender, RoutedEventArgs e) => OpenUrl(AppInfo.RepositoryUrl);

    private void OnOpenReleasesClick(object sender, RoutedEventArgs e) => OpenUrl(AppInfo.ReleasesUrl);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 交给系统默认浏览器打开。打开失败（没装浏览器、被策略拦下）时把网址直接显示出来，
    /// 用户还能手动复制——总比点了没反应强。
    /// </summary>
    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus($"打开失败：{ex.Message}（{url}）", failed: true);
        }
    }

    private void ShowStatus(string message, bool failed)
    {
        StatusHint.Text = message;

        // 用 SetResourceReference 而不是直接赋值：主题字典是整体替换的，
        // 拿 FindResource 抓到的画刷实例在切主题之后就不再是当前主题的那一个了
        StatusHint.SetResourceReference(
            ForegroundProperty,
            failed ? "ErrorBrush" : "SubtleForegroundBrush");

        StatusHint.Visibility = Visibility.Visible;
    }
}
