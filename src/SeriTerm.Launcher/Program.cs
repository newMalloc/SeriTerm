using System.Diagnostics;
using System.Reflection;

namespace SeriTerm.Launcher;

/// <summary>
/// SeriTerm 的小体积启动器。
///
/// 它解决的是"自包含单文件 exe 有 67 MB，其中 99.5% 是 .NET 运行时"这个问题：
/// ・exe 本身用 NativeAOT 编译，是纯原生代码（约 2 MB），**不依赖任何运行时**，所以"机器上什么都没装"时它也能跑；
/// ・真正的程序体（框架依赖单文件 exe，约 1.7 MB）Brotli 压缩后嵌在它肚子里；
/// ・启动时先看机器上有没有能用的 .NET 桌面运行时：有就直接解包运行，
///   没有就征得同意后自动下载并安装微软官方运行时，装完继续启动。
///
/// 命令行（普通用户用不到，出问题时排障用）：
///   SeriTerm.exe --diagnose [--diagnose-out 路径]   把探测结果写成报告，不启动程序
///   SeriTerm.exe --install-runtime                  只装运行时（不问、不启动程序）
/// 其余参数原样传给主程序。
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitRuntimeMissing = 2;   // 缺运行时且用户选择不装
    private const int ExitInstallFailed = 4;    // 下载/校验/安装运行时失败
    private const int ExitPayloadMissing = 5;   // 内置程序体缺失或解包失败

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (HasSwitch(args, "--diagnose"))
            {
                return RunDiagnose(args);
            }

            if (HasSwitch(args, "--install-runtime"))
            {
                return RunInstallOnly();
            }

            return RunApplication(args);
        }
        catch (Exception ex)
        {
            Diagnostics.Note($"未处理异常：{ex}");

            var report = Diagnostics.WriteReport();

            NativeMethods.ShowMessage(
                $"SeriTerm 启动器出错了：\n\n{ex.Message}\n\n诊断报告：{report ?? "（写入失败）"}",
                "SeriTerm",
                NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);

            return 1;
        }
    }

    private static int RunApplication(string[] args)
    {
        var probe = RuntimeProbe.Find();
        Diagnostics.Note($"运行时探测：{probe.Detail}");

        if (!probe.Satisfied)
        {
            switch (RuntimeInstaller.Install(prompt: true))
            {
                case RuntimeInstallOutcome.Declined:
                    NativeMethods.ShowMessage(
                        "SeriTerm 需要微软的 .NET 8 桌面运行时才能启动。\n\n" +
                        "装好之后重新打开本程序即可，只需要装这一次。",
                        "SeriTerm",
                        NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);

                    return ExitRuntimeMissing;

                case RuntimeInstallOutcome.Failed:
                    Diagnostics.WriteReport();
                    return ExitInstallFailed;
            }
        }

        var payload = PayloadBundle.EnsureExtracted();

        if (payload.ExePath is null)
        {
            Diagnostics.Note($"程序体解包失败：{payload.Error}");

            var report = Diagnostics.WriteReport();

            NativeMethods.ShowMessage(
                $"{payload.Error}\n\n诊断报告：{report ?? "（写入失败）"}",
                "SeriTerm",
                NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);

            return ExitPayloadMissing;
        }

        Launch(payload.ExePath, args, wait: IsMcpStdio(args));

        return ExitOk;
    }

    /// <summary>
    /// 是不是在替 AI 客户端当 MCP server（<c>--mcp-stdio</c>）。
    /// 这种模式下启动器**不能**把主程序拉起来就退出：MCP 客户端盯着的是它自己拉起的那个进程，
    /// 看到"server 立刻退出"就会判定连接断开。所以得留下来陪着，直到客户端关掉管道。
    /// </summary>
    private static bool IsMcpStdio(string[] args) =>
        args.Any(a => string.Equals(a, "--mcp-stdio", StringComparison.OrdinalIgnoreCase));

    /// <summary>把主程序拉起来。一般刻意不等它结束：启动器使命已完成，早点退出，任务管理器里只留一个 SeriTerm。</summary>
    private static void Launch(string exePath, string[] args, bool wait = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
        };

        foreach (var argument in StripOwnSwitches(args))
        {
            startInfo.ArgumentList.Add(argument);
        }

        // 如果运行时是按 SERITERM_DOTNET_ROOT 找到的，得告诉主程序的宿主去哪儿找同一套运行时
        var overrideRoot = Environment.GetEnvironmentVariable("SERITERM_DOTNET_ROOT");

        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            startInfo.Environment["DOTNET_ROOT_X64"] = overrideRoot;
            startInfo.Environment["DOTNET_ROOT"] = overrideRoot;
        }

        var child = Process.Start(startInfo);

        if (!wait || child is null)
        {
            return;
        }

        // 标准输入输出没有被重定向，子进程直接继承启动器的那一套句柄，
        // 也就是 AI 客户端的管道——这里只负责"别先死"。
        child.WaitForExit();
    }

    private static int RunDiagnose(string[] args)
    {
        var outputPath = ValueOf(args, "--diagnose-out");

        var report = Diagnostics.WriteReport(outputPath);
        var probe = RuntimeProbe.Find();

        return probe.Satisfied && report is not null ? ExitOk : ExitRuntimeMissing;
    }

    private static int RunInstallOnly()
    {
        var probe = RuntimeProbe.Find();

        if (probe.Satisfied)
        {
            Diagnostics.Note("运行时已存在，无需安装");
            Diagnostics.WriteReport();

            return ExitOk;
        }

        return RuntimeInstaller.Install(prompt: false) switch
        {
            RuntimeInstallOutcome.Installed => ExitOk,
            RuntimeInstallOutcome.Declined => ExitRuntimeMissing,
            _ => ExitInstallFailed,
        };
    }

    private static bool HasSwitch(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? ValueOf(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>去掉启动器自己的开关，剩下的原样交给主程序。</summary>
    private static IEnumerable<string> StripOwnSwitches(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--diagnose-out", StringComparison.OrdinalIgnoreCase))
            {
                i++;   // 连它的值一起跳过
                continue;
            }

            if (string.Equals(args[i], "--diagnose", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--install-runtime", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return args[i];
        }
    }
}
