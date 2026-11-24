using System.Windows;
using System.Windows.Media;

namespace SeriTerm.App.Services;

/// <summary>为当前窗口及菜单提供背景材质和交互状态画刷。</summary>
internal static class SurfaceTranslucency
{
    public static ResourceDictionary? Create(ResourceDictionary? theme, BackdropMode mode)
    {
        if (mode == BackdropMode.Solid || theme is null)
            return null;

        if (mode == BackdropMode.Desktop)
        {
            return new ResourceDictionary
            {
                ["WindowBackgroundBrush"] = Brushes.Transparent,
                ["UseDesktopGlass"] = true,
                ["DesktopTintBrush"] = ((SolidColorBrush)theme["DesktopTintBrush"]).Clone(),
                ["GlassFlyoutBrush"] = ((SolidColorBrush)theme["GlassFlyoutBrush"]).Clone(),
                ["ControlHoverBrush"] = theme["GlassHoverBrush"],
                ["ControlPressedBrush"] = theme["GlassPressedBrush"],
                ["SegmentTrackBrush"] = theme["GlassTrackBrush"],
                ["SegmentHoverBrush"] = theme["GlassPressedBrush"],
                ["SegmentSelectionBrush"] = theme["GlassControlBrush"],
                ["PrimaryActionBrush"] = theme["GlassAccentBrush"],
                ["PanelBackgroundBrush"] = theme["GlassPanelBrush"],
                ["LogBackgroundBrush"] = theme["GlassLogBrush"],
                ["PanelBorderBrush"] = theme["GlassEdgeBrush"],
                ["ControlBackgroundBrush"] = theme["GlassControlBrush"],
                ["ControlBorderBrush"] = theme["GlassControlEdgeBrush"],
                ["ForegroundBrush"] = theme["GlassTextBrush"],
                ["RxForegroundBrush"] = theme["GlassTextBrush"],
                ["SubtleForegroundBrush"] = theme["GlassSecondaryTextBrush"],
            };
        }

        return new ResourceDictionary
        {
            ["WindowBackgroundBrush"] = WithAlpha("WindowBackgroundBrush", 0xA8),
            ["PanelBackgroundBrush"] = WithAlpha("PanelBackgroundBrush", 0xCC),
            ["LogBackgroundBrush"] = WithAlpha("LogBackgroundBrush", 0xA0),
        };

        SolidColorBrush WithAlpha(string key, byte alpha)
        {
            var color = ((SolidColorBrush)theme[key]).Color;
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }
    }

    public static void SetGlassTransparency(ResourceDictionary surfaces, int percent, bool toneActive)
    {
        var fraction = Math.Clamp(percent, 0, 100) / 100d;
        // 色调处理已经压住明暗反差，较低的滑杆值仍可透出背景色。
        var colorTransmission = toneActive ? 4 * fraction / (1 + 3 * fraction) : fraction;
        ((SolidColorBrush)surfaces["DesktopTintBrush"]).Opacity = 1 - colorTransmission;
        // 弹层下方可能有高反差日志，即使窗口调到最透也给菜单文字保留底色。
        ((SolidColorBrush)surfaces["GlassFlyoutBrush"]).Opacity = 1 - 0.34 * fraction;
    }
}
