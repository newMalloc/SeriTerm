using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SeriTerm.Core.Framing;
using SeriTerm.Core.Mcp;
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

    public string SendDraft { get; set; } = string.Empty;

    public List<SendHistoryEntry> SendHistory { get; set; } = [];

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

    /// <summary>查找关键字的收藏（左栏「查找收藏」），按添加顺序保存。</summary>
    public List<string> SearchFavorites { get; set; } = [];

    /// <summary>显示时间戳列。</summary>
    public bool ShowTimestamp { get; set; } = true;

    /// <summary>日志区自动换行。</summary>
    public bool LineWrap { get; set; } = true;

    /// <summary>日志区字号。</summary>
    public double LogFontSize { get; set; } = 13;

    /// <summary>窗口背景亚克力模糊（透出被模糊的桌面壁纸）。</summary>
    public bool BlurBackground { get; set; }

    /// <summary>AI（MCP）接入总开关。默认开：只读档下没有写入能力，开着才方便用户直接接入。</summary>
    public bool McpEnabled { get; set; } = true;

    /// <summary>AI 权限档位。<b>默认只读</b>：要发送数据必须由用户在界面上显式切换。</summary>
    public McpPermission McpPermission { get; set; } = McpPermission.ReadOnly;

    public AppSettings Normalize()
    {
        LastSerial ??= new SerialSettings();
        if (LastSerial.BaudRate <= 0) LastSerial = LastSerial with { BaudRate = 115200 };
        EncodingName ??= "UTF-8";
        DelimiterText ??= "\\r\\n";
        SendDraft ??= string.Empty;
        if (SendDraft.Length > 65536) SendDraft = SendDraft[..65536];
        SendHistory = (SendHistory ?? []).Where(h => h is not null && h.Text is not null && h.Text.Length <= 8192)
            .Distinct().Take(30).ToList();
        Presets = (Presets ?? []).Where(p => p is not null && p.Serial is not null && !string.IsNullOrWhiteSpace(p.Name)).ToList();
        SearchFavorites ??= [];
        LogFontSize = double.IsFinite(LogFontSize) ? Math.Clamp(LogFontSize, 9, 24) : 13;
        WindowWidth = double.IsFinite(WindowWidth) ? Math.Clamp(WindowWidth, 920, 7680) : 1280;
        WindowHeight = double.IsFinite(WindowHeight) ? Math.Clamp(WindowHeight, 600, 4320) : 800;
        AutoFrameGapMilliseconds = Math.Clamp(AutoFrameGapMilliseconds, 1, 2000);
        if (!double.IsFinite(TimedSendIntervalSeconds) || TimedSendIntervalSeconds < 0.05 || TimedSendIntervalSeconds > 86400)
            TimedSendIntervalSeconds = 1;
        if (!Enum.IsDefined(Theme)) Theme = ThemeMode.Dark;
        if (!Enum.IsDefined(McpPermission)) McpPermission = McpPermission.ReadOnly;
        return this;
    }
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

    public JsonSettingsStore() : this(File.Exists(Path.Combine(AppContext.BaseDirectory, "SeriTerm.portable"))
        ? Path.Combine(AppContext.BaseDirectory, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SeriTerm")) { }

    public JsonSettingsStore(string directory)
    {
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
            return (JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings()).Normalize();
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
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalize(), SerializerOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // 保存失败不影响使用
        }
    }
}
