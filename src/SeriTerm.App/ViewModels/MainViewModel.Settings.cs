using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeriTerm.App.Services;

namespace SeriTerm.App.ViewModels;

public partial class MainViewModel
{
    public IReadOnlyList<Choice<ThemeMode>> ThemeOptions { get; } =
        [new(ThemeMode.System, "跟随系统"), new(ThemeMode.Light, "浅色"), new(ThemeMode.Dark, "深色")];

    public IReadOnlyList<Choice<BackdropMode>> BackgroundOptions { get; } =
        [new(BackdropMode.Solid, "纯色"), new(BackdropMode.Desktop, "磨砂玻璃"), new(BackdropMode.Image, "自定义图片")];

    public IReadOnlyList<Choice<string>> LogFontOptions { get; } =
    [
        new("Consolas, Microsoft YaHei UI", "Consolas（默认）"),
        new("Cascadia Mono, Consolas, Microsoft YaHei UI", "Cascadia Mono"),
        new("Courier New, Microsoft YaHei UI", "Courier New"),
    ];

    public IReadOnlyList<Choice<int>> LogBufferOptions { get; } =
        [new(16, "16 MiB"), new(32, "32 MiB"), new(64, "64 MiB"), new(128, "128 MiB"), new(256, "256 MiB")];

    [ObservableProperty] private ThemeMode _selectedTheme = ThemeMode.Dark;
    [ObservableProperty] private BackdropMode _backgroundMode;
    [ObservableProperty] private int _glassTransparency = AppSettings.DefaultGlassTransparency;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(GlassBlurLabel))]
    private int _glassBlurLevel = AppSettings.StrongGlassBlur;
    public string GlassBlurLabel => GlassBlurLevel switch { 0 => "清晰", 1 => "轻度", _ => "强" };
    public int GlassBlurStep => DesktopBackdrop.UsesSystemAcrylic ? 2 : 1;
    [ObservableProperty] private string _backgroundImagePath = string.Empty;
    [ObservableProperty] private string _backgroundImageLabel = "未选择图片";
    [ObservableProperty] private double _backgroundImageOpacity = 0.8;
    [ObservableProperty] private string _logFontFamily = "Consolas, Microsoft YaHei UI";
    [ObservableProperty] private int _logBufferMiB = 64;
    [ObservableProperty] private bool _showLogLineNumbers;
    [ObservableProperty] private bool _keepDisplayAwake;

    [RelayCommand]
    private void ResetGlassSettings()
    {
        GlassTransparency = AppSettings.DefaultGlassTransparency;
        GlassBlurLevel = AppSettings.StrongGlassBlur;
    }

    partial void OnSelectedThemeChanged(ThemeMode value)
    {
        if (_themeService.Current != value)
            _themeService.Apply(value);
    }

    partial void OnLogBufferMiBChanged(int value)
    {
        Log.SetByteLimit(value * 1024L * 1024);
        UpdateBufferStatus();
    }
}
