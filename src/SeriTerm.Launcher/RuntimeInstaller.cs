using System.Diagnostics;

namespace SeriTerm.Launcher;

internal enum RuntimeInstallOutcome
{
    /// <summary>运行时已就绪（刚装完，或本来就在）。</summary>
    Installed,

    /// <summary>用户选择不装 / 中途取消。</summary>
    Declined,

    /// <summary>下载、校验或安装失败。</summary>
    Failed,
}

/// <summary>
/// "帮用户把运行时装上"这一段。
///
/// 只用微软官方渠道：<c>https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe</c>
/// 是官方短链，会 302 到 builds.dotnet.microsoft.com 上当季最新的 8.0.x 安装包（实测 8.0.31、56 MB）。
/// 换句话说我们不自己托管、不自己打安装包，装的也永远是微软签名的官方安装程序。
///
/// 下载完先验 Authenticode 签名，签名无效就不执行——这一步是"从网上下载并执行"这种行为的最低底线：
/// 万一短链被劫持或中间人替换了文件，签名的信任链就断了，我们宁可报错也不能运行它。
///
/// 安装用 <c>/install /passive /norestart</c>：passive 会显示官方安装界面自带的进度条（不弹交互），
/// 权限由安装包自己申请（会弹一次 UAC），启动器本身不申请管理员权限。
/// </summary>
internal static class RuntimeInstaller
{
    private const string InstallerUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";

    private const string DownloadPageUrl = "https://dotnet.microsoft.com/download/dotnet/8.0";

    private const string InstallerFileName = "windowsdesktop-runtime-8.0-win-x64.exe";

    /// <param name="prompt">true 时先问用户；--install-runtime 场景下由调用方决定。</param>
    public static RuntimeInstallOutcome Install(bool prompt)
    {
        // 下载进度对话框（IProgressDialog）要求当前线程是 STA 且初始化过 COM。
        // 只有这条路径用得上 COM，所以在这里就地初始化/反初始化。
        var comReady = NativeMethods.InitializeCom();

        try
        {
            return InstallCore(prompt);
        }
        finally
        {
            if (comReady)
            {
                NativeMethods.ShutdownCom();
            }
        }
    }

    private static RuntimeInstallOutcome InstallCore(bool prompt)
    {
        if (prompt && !AskUser())
        {
            return RuntimeInstallOutcome.Declined;
        }

        var directory = Path.Combine(Path.GetTempPath(), "seriterm-runtime-bootstrap");
        var installer = Path.Combine(directory, InstallerFileName);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            return Fail($"创建下载目录失败：{ex.Message}", $"目录：{directory}");
        }

        if (!HttpDownloader.Download(
                InstallerUrl,
                installer,
                "SeriTerm · 准备运行环境",
                "正在下载 .NET 8 桌面运行时（微软官方，约 56 MB）",
                out var downloadError))
        {
            if (downloadError == HttpDownloader.Cancelled)
            {
                return RuntimeInstallOutcome.Declined;
            }

            return Fail(
                "下载 .NET 桌面运行时失败。",
                $"{downloadError}\n\n你可以联网后重试，或到官网手动下载安装：\n{DownloadPageUrl}");
        }

        if (!NativeMethods.HasValidAuthenticodeSignature(installer))
        {
            return Fail(
                "下载到的安装包没有通过数字签名校验，出于安全考虑不会运行它。",
                $"文件：{installer}\n\n请到官网手动下载安装：\n{DownloadPageUrl}");
        }

        if (!RunInstaller(installer, out var exitCode))
        {
            return Fail(
                "启动安装程序失败。",
                $"请到官网手动下载安装：\n{DownloadPageUrl}");
        }

        // 官方安装包的退出码约定：0 成功；3010/1641 成功但需要重启；1602 用户取消
        switch (exitCode)
        {
            case 1602:
                return RuntimeInstallOutcome.Declined;
            case 0:
            case 3010:
            case 1641:
                break;
            default:
                return Fail(
                    $"安装程序返回了失败代码 {exitCode}。",
                    $"你也可以到官网手动下载安装：\n{DownloadPageUrl}");
        }

        DeleteQuietly(installer);

        // 装完再看一眼；探测说"还是没找到"也照样往下走——安装包自报成功时，
        // 让主程序的宿主自己决定能不能跑，比我们在这里拦下来更不容易误伤。
        var probe = RuntimeProbe.Find();

        if (!probe.Satisfied)
        {
            Diagnostics.Note($"安装程序返回 {exitCode}，但重新探测仍未命中：{probe.Detail}");
        }

        return RuntimeInstallOutcome.Installed;
    }

    private static bool AskUser()
    {
        const string text =
            "SeriTerm 需要微软的 .NET 8 桌面运行时，这台机器上还没装。\n\n" +
            "是否现在自动下载并安装？\n" +
            "  · 安装包来自微软官方站点，约 56 MB\n" +
            "  · 安装需要管理员权限，会弹一次用户账户控制（UAC）\n" +
            "  · 装好后 SeriTerm 会自动继续启动\n\n" +
            "选择「否」将退出；你也可以自己装好运行时后再打开 SeriTerm。";

        var answer = NativeMethods.ShowMessage(
            text,
            "SeriTerm · 需要 .NET 桌面运行时",
            NativeMethods.MB_YESNO | NativeMethods.MB_ICONQUESTION);

        return answer == NativeMethods.IDYES;
    }

    private static bool RunInstaller(string installer, out int exitCode)
    {
        exitCode = -1;

        try
        {
            // UseShellExecute=true：让资源管理器那套机制去处理"需要提权"，
            // 该弹 UAC 就弹 UAC，不需要提权时也不会平白无故要管理员权限。
            var startInfo = new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "/install /passive /norestart",
                UseShellExecute = true,
            };

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            exitCode = process.ExitCode;

            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Note($"启动安装程序异常：{ex.Message}");
            return false;
        }
    }

    private static RuntimeInstallOutcome Fail(string headline, string detail)
    {
        Diagnostics.Note($"安装运行时失败：{headline} {detail.Replace('\n', ' ')}");

        var answer = NativeMethods.ShowMessage(
            $"{headline}\n\n{detail}\n\n现在打开下载页面？",
            "SeriTerm · 安装 .NET 桌面运行时失败",
            NativeMethods.MB_YESNO | NativeMethods.MB_ICONERROR);

        if (answer == NativeMethods.IDYES)
        {
            OpenDownloadPage();
        }

        return RuntimeInstallOutcome.Failed;
    }

    internal static void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = DownloadPageUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Diagnostics.Note($"打开下载页面失败：{ex.Message}");
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
