using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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

        ApplyOpaqueSurface();

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

    private void OnThemeChanged(object? sender, ThemeMode mode) => ApplyOpaqueSurface();

    /// <summary>
    /// 窗口表面用主题里的**原始不透明**画刷。
    ///
    /// 主窗口开着"模糊背景"时，<see cref="SurfaceTranslucency"/> 会往 Application.Resources 末尾
    /// 合并一个覆盖字典，把 <c>WindowBackgroundBrush</c>（alpha 0xA8）等换成半透明版，好让主窗口
    /// 后面那层模糊壁纸透出来。主窗口自己画了壁纸层，所以没问题；本窗口是纯色窗口，
    /// 66% 的浅灰叠在黑色窗口底上会变成灰蒙蒙的 #A1A1A1（实测），副标题这类次要文字的
    /// 对比度会从设计值 4.85:1 掉到 2.06:1。所以这里绕开 Application.Resources，
    /// 直接从主题字典取原色——SurfaceTranslucency 的注释里也写着"取原始颜色必须从当前主题字典里取"。
    ///
    /// 注意取到的是**画刷实例**，不能再 SetResourceReference(key)：那样又会走一遍资源查找，
    /// 命中的还是那份半透明覆盖字典。
    /// </summary>
    private void ApplyOpaqueSurface()
    {
        SetOpaqueSurface(this, Control.BackgroundProperty, "WindowBackgroundBrush");
        SetOpaqueSurface(TitleBar, Border.BackgroundProperty, "TitleBarBackgroundBrush");
    }

    private void SetOpaqueSurface(FrameworkElement target, DependencyProperty property, string key)
    {
        if (_themeService.ActiveTheme?[key] is Brush opaque)
        {
            target.SetValue(property, opaque);
            return;
        }

        // 拿不到主题字典时退回动态资源：至少还能跟随主题，代价是会跟着半透明
        target.SetResourceReference(property, key);
    }

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
