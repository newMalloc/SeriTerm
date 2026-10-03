using Microsoft.Win32;

namespace SeriTerm.Launcher;

/// <summary>探测结果。<see cref="Detail"/> 只用于诊断输出，不弹给用户看。</summary>
internal sealed record RuntimeProbeResult(bool Satisfied, string? Version, string Detail)
{
    public static RuntimeProbeResult Missing(string detail) => new(false, null, detail);
}

/// <summary>
/// 查目标机器上有没有能跑得动主程序的 .NET 桌面运行时（Microsoft.WindowsDesktop.App）。
///
/// 判定标准与主程序 <c>SeriTerm.runtimeconfig.json</c> 里的 <c>rollForward: Major</c> 一致：
/// ・有 8.x（含 8.0.x 任意补丁）→ 满足，宿主会挑最低的那个 8.x；
/// ・一个 8.x 都没有，但有更高大版本（9.x/10.x…）→ 也满足，宿主会往上跨一个大版本；
/// ・只有 6.x/7.x，或压根没装 → 不满足，需要装 8.x。
///
/// 探测顺序（先便宜后贵，任一命中即返回）：
///   1. 环境变量 SERITERM_DOTNET_ROOT —— 用户/便携场景明确指定了运行时目录，此时只认它；
///   2. DOTNET_ROOT_X64 / DOTNET_ROOT —— 官方宿主自己的覆盖变量，尊重它；
///   3. 注册表 HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\… —— 官方记录安装版本的地方；
///   4. %ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\<版本> —— 最笨但最不会漏。
///
/// 注意这里只是"先看一眼、省得白弹一次安装提示"：真正决定用哪套运行时的是主程序自己的宿主，
/// 所以这里宁可宽松（说"有"）也不要过严——真没有的话宿主会自己报错，而漏判只会让用户白装一次。
/// </summary>
internal static class RuntimeProbe
{
    internal const string FrameworkName = "Microsoft.WindowsDesktop.App";

    private const int MinimumMajor = 8;

    private static readonly string[] RegistryKeys =
    {
        // .NET 安装器把版本登记在这里；实测 x64 版本也会落在 WOW6432Node 视图里，两处都查
        @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\" + FrameworkName,
        @"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\" + FrameworkName,
    };

    public static RuntimeProbeResult Find()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("SERITERM_DOTNET_ROOT");

        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            var scanned = ScanDotnetRoot(overrideRoot!);

            return scanned is null
                ? RuntimeProbeResult.Missing($"SERITERM_DOTNET_ROOT={overrideRoot} 下没找到 {FrameworkName} 的 8.x")
                : new RuntimeProbeResult(true, scanned, $"SERITERM_DOTNET_ROOT={overrideRoot}");
        }

        foreach (var name in new[] { "DOTNET_ROOT_X64", "DOTNET_ROOT" })
        {
            var root = Environment.GetEnvironmentVariable(name);

            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var scanned = ScanDotnetRoot(root!);

            if (scanned is not null)
            {
                return new RuntimeProbeResult(true, scanned, $"{name}={root}");
            }
        }

        var fromRegistry = ReadRegistryVersions();

        if (fromRegistry.Count > 0)
        {
            var best = PickBest(fromRegistry);

            if (best is not null)
            {
                return new RuntimeProbeResult(true, best, "HKLM sharedfx 注册表");
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var fromDisk = ScanSharedFolder(Path.Combine(programFiles, "dotnet"));

        if (fromDisk.Count > 0)
        {
            var best = PickBest(fromDisk);

            if (best is not null)
            {
                return new RuntimeProbeResult(true, best, $@"{programFiles}\dotnet\shared");
            }
        }

        return RuntimeProbeResult.Missing(
            $"注册表与 {programFiles}\\dotnet\\shared 下都没有 {FrameworkName} 的 {MinimumMajor}.x 及以上版本");
    }

    /// <summary>扫 <c>&lt;root&gt;\shared\Microsoft.WindowsDesktop.App\&lt;版本&gt;</c>；目录不存在返回 null。</summary>
    private static string? ScanDotnetRoot(string root)
    {
        var versions = ScanSharedFolder(root);

        return versions.Count == 0 ? null : PickBest(versions);
    }

    private static List<Version> ScanSharedFolder(string dotnetRoot)
    {
        var result = new List<Version>();

        try
        {
            var shared = Path.Combine(dotnetRoot, "shared", FrameworkName);

            if (!Directory.Exists(shared))
            {
                return result;
            }

            foreach (var dir in Directory.EnumerateDirectories(shared))
            {
                if (Version.TryParse(Path.GetFileName(dir), out var version))
                {
                    result.Add(version);
                }
            }
        }
        catch (Exception)
        {
            // 探测失败按"没找到"处理：宁可多问用户一次，也不要在这里抛异常把启动流程打断
        }

        return result;
    }

    private static List<Version> ReadRegistryVersions()
    {
        var result = new List<Version>();

        foreach (var path in RegistryKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);

                if (key is null)
                {
                    continue;
                }

                foreach (var name in key.GetValueNames())
                {
                    // 值名就是版本号（值本身是安装目录），个别环境会带尾随反斜杠
                    if (Version.TryParse(name.Trim().TrimEnd('\\'), out var version))
                    {
                        result.Add(version);
                    }
                }
            }
            catch (Exception)
            {
                // 同上：读注册表失败不是致命错误
            }
        }

        return result;
    }

    /// <summary>在候选里挑"宿主会挑中的那一个"：优先最低的 8.x；没有 8.x 就挑最低的更高大版本。</summary>
    private static string? PickBest(List<Version> versions)
    {
        var eligible = versions.Where(v => v.Major >= MinimumMajor).ToList();

        if (eligible.Count == 0)
        {
            return null;
        }

        var sameMajor = eligible.Where(v => v.Major == MinimumMajor).ToList();

        var chosen = sameMajor.Count > 0 ? sameMajor.Min() : eligible.Min();

        return chosen!.ToString();
    }
}
