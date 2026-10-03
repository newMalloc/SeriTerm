using System.IO.Compression;
using System.Reflection;

namespace SeriTerm.Launcher;

/// <summary>解包结果：成功给出可执行文件路径，失败给出人话原因。</summary>
internal sealed record PayloadResult(string? ExePath, string? Error)
{
    public static PayloadResult Fail(string error) => new(null, error);
}

/// <summary>
/// 内置程序体的解包与缓存。
///
/// 启动器里塞的不是"压缩包+解压到临时目录"那套，而是**框架依赖单文件 exe**（约 1.7 MB）
/// 经 Brotli 压到约 0.6 MB，作为嵌入资源带在身上；运行时解到
/// <c>%LocalAppData%\SeriTerm\app\&lt;版本&gt;\SeriTerm.exe</c> 并就地启动。
///
/// 为什么不直接跑内存里的程序集：主程序是 WPF 应用，需要在磁盘上按目录解析一堆依赖，
/// 落到磁盘一次最省事、也最稳。目录带版本号，所以升级后不会和旧版本打架，
/// 同一个版本第二次启动直接复用（比对长度即可，不重复解压）。
/// </summary>
internal static class PayloadBundle
{
    private const string ResourceName = "SeriTerm.payload.br";

    internal const string PayloadFileName = "SeriTerm.exe";

    public static PayloadResult EnsureExtracted()
    {
        Assembly assembly = typeof(PayloadBundle).Assembly;

        byte[] payload;

        try
        {
            using var resource = assembly.GetManifestResourceStream(ResourceName);

            if (resource is null)
            {
                return PayloadResult.Fail(
                    "这个 exe 里没有内置程序体。\n\n" +
                    "它多半是开发时直接从源码构建出来的（没有用 tools/publish.ps1 -Bootstrapper 打包）。\n" +
                    "请改用发布产物，或运行 tools/publish.ps1 重新打包。");
            }

            using var brotli = new BrotliStream(resource, CompressionMode.Decompress);
            using var buffer = new MemoryStream((int)resource.Length * 3);
            brotli.CopyTo(buffer);
            payload = buffer.ToArray();
        }
        catch (Exception ex)
        {
            return PayloadResult.Fail($"解压内置程序体失败：{ex.Message}");
        }

        var version = assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeriTerm", "app", version);
        var target = Path.Combine(directory, PayloadFileName);

        // 同一时刻双击两个副本（或首个实例正在解包）时，别让两边写同一个文件
        using var gate = new Mutex(false, @"Local\SeriTerm.Launcher.Payload");

        try
        {
            gate.WaitOne(TimeSpan.FromSeconds(30));

            if (File.Exists(target) && new FileInfo(target).Length == payload.LongLength)
            {
                CleanupOtherVersions(directory);

                return new PayloadResult(target, null);
            }

            Directory.CreateDirectory(directory);

            // 先写临时文件再改名：中途失败/被杀进程也不会留下一个"看着像程序"的半截 exe
            var temp = target + ".tmp";

            File.WriteAllBytes(temp, payload);
            File.Move(temp, target, overwrite: true);

            CleanupOtherVersions(directory);

            return new PayloadResult(target, null);
        }
        catch (Exception ex)
        {
            return PayloadResult.Fail(
                $"把程序体写到 {directory} 失败：{ex.Message}\n\n" +
                "如果杀毒软件拦截了写入，请把它加进信任列表后重试。");
        }
        finally
        {
            try
            {
                gate.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 没抢到锁（超时）时 ReleaseMutex 会抛，忽略即可
            }
        }
    }

    /// <summary>清掉旧版本的解包目录，避免每次升级在 AppData 里趴一个 1.7 MB 的残留。</summary>
    private static void CleanupOtherVersions(string currentDirectory)
    {
        try
        {
            var appRoot = Directory.GetParent(currentDirectory)?.FullName;

            if (appRoot is null || !Directory.Exists(appRoot))
            {
                return;
            }

            foreach (var dir in Directory.EnumerateDirectories(appRoot))
            {
                if (string.Equals(dir, currentDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception)
                {
                    // 旧版本可能正在运行（文件被占用）——删不掉就留着，下次再说
                }
            }
        }
        catch (Exception)
        {
            // 清理是锦上添花，绝不影响启动
        }
    }
}
