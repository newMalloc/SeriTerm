using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SeriTerm.Core.Framing;
using SeriTerm.Core.Presets;
using SeriTerm.Core.Send;
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

    /// <summary>断帧方式（M3）。</summary>
    public FramingMode Framing { get; set; } = FramingMode.Gap;

    /// <summary>自动断帧的空闲间隔，毫秒。</summary>
    public int AutoFrameGapMilliseconds { get; set; } = 20;

    /// <summary>分隔符断帧使用的分隔符写法。</summary>
    public string DelimiterText { get; set; } = "\\r\\n";

    /// <summary>发送时是否按十六进制解析输入（M4）。</summary>
    public bool SendHex { get; set; }

    /// <summary>发送内容末尾附加的换行符（M4）。</summary>
    public LineEnding SendLineEnding { get; set; } = LineEnding.CrLf;

    /// <summary>定时发送间隔，秒（M4）。</summary>
    public double TimedSendIntervalSeconds { get; set; } = 1.0;

    /// <summary>将接收保存到文本日志（M7）。</summary>
    public bool SaveLogToFile { get; set; }

    /// <summary>同时保存原始字节（可重放，M7）。</summary>
    public bool SaveRawLog { get; set; }

    /// <summary>日志目录；空表示用默认目录（M7）。</summary>
    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>自动重连（M6）。</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>启动时自动打开上次的串口（M6）。</summary>
    public bool AutoOpenOnStartup { get; set; }

    /// <summary>终端模式本地回显（M5）。</summary>
    public bool TerminalLocalEcho { get; set; }

    /// <summary>终端模式退格发送 0x7F 还是 0x08（M5）。</summary>
    public bool TerminalBackspaceSendsDel { get; set; } = true;

    /// <summary>配置预设（★ 收藏，M8）。</summary>
    public List<SerialPreset> Presets { get; set; } = [];

    /// <summary>显示时间戳列。</summary>
    public bool ShowTimestamp { get; set; } = true;

    /// <summary>日志区自动换行。</summary>
    public bool LineWrap { get; set; } = true;

    /// <summary>日志区字号。</summary>
    public double LogFontSize { get; set; } = 13;

    /// <summary>窗口背景亚克力模糊（透出被模糊的桌面壁纸）。</summary>
    public bool BlurBackground { get; set; } = true;
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
