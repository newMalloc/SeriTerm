using System.Reflection;
using System.Text;

namespace SeriTerm.Launcher;

/// <summary>
/// 启动器的诊断输出。
///
/// 平时它什么都不做——用户双击 exe 只应该看到程序起来，不该看到黑框或日志文件。
/// 只有显式带 <c>--diagnose</c>、或者启动过程中出事（下载失败、安装失败、解包失败）时，
/// 才会把过程写进 <c>%TEMP%\seriterm-launcher-diag.txt</c>，方便让用户把文件发回来定位。
/// </summary>
internal static class Diagnostics
{
    private static readonly List<string> Notes = new();

    public static void Note(string message) => Notes.Add($"{DateTime.Now:HH:mm:ss} {message}");

    public static string DefaultReportPath =>
        Path.Combine(Path.GetTempPath(), "seriterm-launcher-diag.txt");

    /// <summary>写好诊断报告并返回路径；写不进去就返回 null（不能因为写日志失败而影响启动）。</summary>
    public static string? WriteReport(string? path = null)
    {
        var target = string.IsNullOrWhiteSpace(path) ? DefaultReportPath : path!;

        try
        {
            var text = BuildReport();

            var directory = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(target, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            return target;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string BuildReport()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var builder = new StringBuilder();

        builder.AppendLine("SeriTerm 启动器诊断报告");
        builder.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"启动器版本：{assembly.GetName().Version}");
        builder.AppendLine($"启动器路径：{Environment.ProcessPath}");
        builder.AppendLine($"系统：{Environment.OSVersion.VersionString}（{(Environment.Is64BitOperatingSystem ? "64" : "32")} 位）");
        builder.AppendLine();

        builder.AppendLine("== .NET 桌面运行时探测 ==");
        builder.AppendLine($"框架：{RuntimeProbe.FrameworkName}");
        builder.AppendLine($"要求：8.x（找不到 8.x 时允许往上跨一个大版本，主程序 runtimeconfig 为 rollForward=Major）");

        var probe = RuntimeProbe.Find();
        builder.AppendLine($"结果：{(probe.Satisfied ? "已安装" : "未安装")}");
        builder.AppendLine($"版本：{probe.Version ?? "-"}");
        builder.AppendLine($"来源：{probe.Detail}");
        builder.AppendLine();

        builder.AppendLine("== 内置程序体 ==");
        var resource = assembly.GetManifestResourceStream("SeriTerm.payload.br");

        if (resource is null)
        {
            builder.AppendLine("未内置（开发构建）");
        }
        else
        {
            using (resource)
            {
                builder.AppendLine($"压缩后：{resource.Length:N0} 字节");
            }

            var payload = PayloadBundle.EnsureExtracted();
            builder.AppendLine($"解包路径：{payload.ExePath ?? payload.Error}");
        }

        builder.AppendLine();

        if (Notes.Count > 0)
        {
            builder.AppendLine("== 过程记录 ==");
            foreach (var note in Notes)
            {
                builder.AppendLine(note);
            }
        }

        return builder.ToString();
    }
}
