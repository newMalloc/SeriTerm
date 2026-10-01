using System.Reflection;
using System.Runtime.InteropServices;

namespace SeriTerm.App.Services;

/// <summary>
/// 「关于」窗口要展示的程序自身信息（版本、提交、运行时、许可、仓库地址）。
///
/// 版本号不在这里写死：它取自程序集上的 <see cref="AssemblyInformationalVersionAttribute"/>，
/// 也就是 <c>Directory.Build.props</c> 里那个 <c>&lt;Version&gt;</c>（全仓库唯一一处）。
/// 好处是只改一处、发版时不会出现"界面写着一个版本、文件属性里是另一个版本"。
///
/// MSBuild 还会把当前提交拼在版本后面（形如 <c>1.2.3+0c26600…</c>，见 obj 下的 AssemblyInfo.cs），
/// 这里把两段拆开显示：版本号给人看，短提交号用来确认"手上这个 exe 到底是哪次构建"。
/// </summary>
internal static class AppInfo
{
    private const string Repository = "https://github.com/newMalloc/SeriTerm";

    public const string RepositoryUrl = Repository;

    public const string ReleasesUrl = Repository + "/releases";

    /// <summary>许可协议；与仓库根目录的 LICENSE 一致。</summary>
    public const string LicenseName = "MIT License";

    public const string CopyrightHolder = "Copyright © 2026 newMalloc";

    /// <summary>
    /// 运行时依赖及其许可（逐个查过 NuGet 包里的 .nuspec &lt;license&gt; 字段，
    /// 全部是 MIT）。列表刻意手写而不是运行时反射：反射只能看到包名，
    /// 拿不到许可，写死反而不会说谎。
    /// </summary>
    public const string ThirdPartyNotice =
        "运行时依赖 CommunityToolkit.Mvvm、Microsoft.Extensions.*、System.IO.Ports、System.Management，均为 MIT 许可。";

    private static readonly Assembly Self = typeof(AppInfo).Assembly;

    /// <summary>版本号，例如 <c>1.2.3</c>。</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>完整提交号；信息版本里没有 <c>+</c> 段时为 null。</summary>
    public static string? CommitHash { get; } = ResolveCommitHash();

    /// <summary>短提交号（7 位），没有提交信息时为 null。</summary>
    public static string? ShortCommitHash { get; } =
        CommitHash is { Length: > 7 } hash ? hash[..7] : CommitHash;

    /// <summary>例如 <c>.NET 8.0.22（x64）</c>。绿色版里能直接看出跑的是哪套运行时。</summary>
    public static string RuntimeDescription { get; } =
        $"{RuntimeInformation.FrameworkDescription}（{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}）";

    private static string ResolveVersion()
    {
        // 信息版本形如 "1.2.3+0c26600…"；"+" 之后是 SourceLink 注入的提交号
        var informational = Self
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        var core = informational?.Split('+')[0].Trim();

        if (!string.IsNullOrEmpty(core))
        {
            return core;
        }

        // 兜底：没有信息版本就用程序集版本（1.2.3.0 → 1.2.3；Build 为 -1 时只到次版本）
        var version = Self.GetName().Version;

        if (version is null)
        {
            return "未知";
        }

        return version.Build >= 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
    }

    private static string? ResolveCommitHash()
    {
        var informational = Self
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        var separator = informational?.IndexOf('+') ?? -1;

        if (informational is null || separator < 0 || separator == informational.Length - 1)
        {
            return null;
        }

        var hash = informational[(separator + 1)..].Trim();

        // 提交号都是十六进制；不是的话（有人在版本号里手写了 "+beta"）就不要冒充提交号
        return hash.Length > 0 && hash.All(Uri.IsHexDigit) ? hash : null;
    }
}
