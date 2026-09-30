using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SeriTerm.Core.Serial;

namespace SeriTerm.App.Services;

/// <summary>应用配置（持久化到 <c>%AppData%\SeriTerm\settings.json</c>）。</summary>
public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;

    /// <summary>上次使用的串口参数，启动时回填。</summary>
    public SerialSettings LastSerial { get; set; } = new();

    public double WindowWidth { get; set; } = 1280;

    public double WindowHeight { get; set; } = 800;

    public bool WindowMaximized { get; set; }

    /// <summary>日志区自动滚动（M9）。</summary>
    public bool AutoScroll { get; set; } = true;

    /// <summary>接收区十六进制显示（M2）。</summary>
    public bool HexDisplay { get; set; }

    /// <summary>接收区文本编码名（M2）。</summary>
    public string EncodingName { get; set; } = "UTF-8";
}

public interface ISettingsStore
{
    string FilePath { get; }

    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>
/// 配置文件读写。任何异常都不向上抛：配置损坏时退回默认值，绝不让程序起不来。
/// 写入采用"先写临时文件再替换"，避免掉电/崩溃留下半个文件。
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; }

    public JsonSettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SeriTerm");
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, SerializerOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // 保存失败不影响使用
        }
    }
}
