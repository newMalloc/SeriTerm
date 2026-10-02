using System.Windows;
using System.Windows.Media;

namespace SeriTerm.App.Services;

/// <summary>
/// 背景模糊生效时，把主题里的"表面"画刷换成同色半透明版本，
/// 这样 DWM 画在窗口后面的模糊桌面才能透出来。
///
/// 做法是往 <see cref="Application.Resources"/> 末尾再合并一个覆盖字典：
/// WPF 查找合并字典是"后加入的优先"，所以覆盖字典里的同名键会盖住主题字典，
/// 而所有引用这些键的地方本来就是 DynamicResource，换掉即生效，不用改任何样式。
///
/// 注意：取原始颜色必须从"当前主题字典"里取，不能从 Application.Resources 查——
/// 覆盖字典已经在里面了，查出来会是半透明版本，反复刷新会把 alpha 越乘越深。
/// </summary>
internal static class SurfaceTranslucency
{
    /// <summary>参与半透明的表面画刷及各自的 alpha。文字主要落在日志区与面板上，所以这两处别太透。</summary>
    private static readonly (string Key, byte Alpha)[] Surfaces =
    [
        ("WindowBackgroundBrush", 0xA8),
        ("PanelBackgroundBrush", 0xCC),
        ("LogBackgroundBrush", 0xA0),
    ];

    private static ResourceDictionary? _overlay;

    /// <summary>
    /// 打开/关闭半透明表面。<paramref name="theme"/> 必须是当前主题字典（可为 null）。
    /// </summary>
    public static void SetEnabled(ResourceDictionary? theme, bool enabled)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        RemoveOverlay(app);

        if (!enabled || theme is null)
        {
            return;
        }

        var overlay = new ResourceDictionary();

        foreach (var (key, alpha) in Surfaces)
        {
            if (theme[key] is SolidColorBrush source)
            {
                overlay[key] = Translucent(source, alpha);
            }
        }

        app.Resources.MergedDictionaries.Add(overlay);
        _overlay = overlay;
    }

    /// <summary>主题切换后重新取色（覆盖字典里的颜色是写死的，不会自己跟着主题走）。</summary>
    public static void Refresh(ResourceDictionary? theme)
        => SetEnabled(theme, _overlay is not null);

    private static void RemoveOverlay(Application app)
    {
        if (_overlay is null)
        {
            return;
        }

        app.Resources.MergedDictionaries.Remove(_overlay);
        _overlay = null;
    }

    private static SolidColorBrush Translucent(SolidColorBrush source, byte alpha)
    {
        var color = source.Color;
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
