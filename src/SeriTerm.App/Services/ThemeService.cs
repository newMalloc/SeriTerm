using System.Windows;
using Microsoft.Win32;

namespace SeriTerm.App.Services;

public enum ThemeMode
{
    /// <summary>深色。</summary>
    Dark,

    /// <summary>浅色。</summary>
    Light,

    /// <summary>跟随系统（应用时解析为深/浅；v1 不监听系统主题实时变化）。</summary>
    System,
}

public interface IThemeService
{
    ThemeMode Current { get; }

    /// <summary>当前实际生效的是否为深色（<see cref="ThemeMode.System"/> 会被解析）。</summary>
    bool IsDarkEffective { get; }

    /// <summary>
    /// 当前生效的主题字典。背景模糊需要从它里面取"原始不透明颜色"来生成半透明表面，
    /// 不能走 Application.Resources（那里可能已经被半透明覆盖字典盖住了）。
    /// </summary>
    ResourceDictionary? ActiveTheme { get; }

    event EventHandler<ThemeMode>? ThemeChanged;

    void Apply(ThemeMode mode);
}

/// <summary>
/// 主题服务：整体替换 Application 资源里的主题字典。
/// Shared.xaml 中的样式全部使用 DynamicResource，因此切换是即时的、无需重启。
/// </summary>
public sealed class ThemeService : IThemeService
{
    private const string DarkFileName = "Dark.xaml";
    private const string LightFileName = "Light.xaml";

    public ThemeMode Current { get; private set; } = ThemeMode.Dark;

    public bool IsDarkEffective { get; private set; } = true;

    public ResourceDictionary? ActiveTheme { get; private set; }

    public event EventHandler<ThemeMode>? ThemeChanged;

    public void Apply(ThemeMode mode)
    {
        var dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => !IsSystemUsingLightTheme(),
        };

        Current = mode;
        IsDarkEffective = dark;
        ActiveTheme = SwapThemeDictionary(dark ? DarkFileName : LightFileName) ?? ActiveTheme;

        ThemeChanged?.Invoke(this, mode);
    }

    private static bool IsSystemUsingLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ResourceDictionary? SwapThemeDictionary(string fileName)
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        var dictionaries = app.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"Themes/{fileName}", UriKind.Relative),
        };

        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is null)
            {
                continue;
            }

            if (source.EndsWith(DarkFileName, StringComparison.OrdinalIgnoreCase)
                || source.EndsWith(LightFileName, StringComparison.OrdinalIgnoreCase))
            {
                dictionaries[i] = replacement;
                return replacement;
            }
        }

        dictionaries.Add(replacement);
        return replacement;
    }
}
