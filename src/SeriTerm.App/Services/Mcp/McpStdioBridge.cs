using System.Diagnostics;
using System.IO;
using System.Text;
using SeriTerm.Core.Mcp;

namespace SeriTerm.App.Services.Mcp;

/// <summary>
/// <c>--mcp-stdio</c> 模式：被 AI 客户端拉起来时走这条路——本进程只当"桥"，
/// 用标准输入输出讲 MCP，真正的串口操作转发给已经在跑的界面进程（命名管道）。
///
/// 为什么不做成第二个 exe：SeriTerm 一直以"单文件绿色版"为卖点，
/// 多带一个 exe 就等于每次发布都要多解释一句"这个也得一起下载"。
/// 同一个 exe 换个参数就是一个不同的模式，用户配置里也只需要一条路径。
/// </summary>
internal static class McpStdioBridge
{
    /// <summary>AI 客户端配置里要带的参数。</summary>
    public const string Argument = "--mcp-stdio";

    public static async Task<int> RunAsync()
    {
        // 不用 Console.In/Out：它们的编码取决于控制台代码页，
        // 而这里的两端（AI 客户端、管道）都按 UTF-8 说话，显式包一层才稳。
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));

        await using var backend = new McpPipeClient();

        var server = new McpStdioServer(backend, input, output, AppInfo.Version)
        {
            EnsureBackendAvailable = LaunchInterfaceAsync,
        };

        try
        {
            return await server.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await output.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 客户端已经关掉管道了，写不出去也无所谓
            }

            output.Dispose();
            input.Dispose();
        }
    }

    /// <summary>
    /// 界面进程没在跑时把它拉起来。用户的心智是"我在 AI 客户端里让它读串口"，
    /// 这时候再要求他先去开始菜单点一下 SeriTerm 是多余的。
    /// </summary>
    private static Task LaunchInterfaceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var exePath = Environment.ProcessPath;

            if (string.IsNullOrEmpty(exePath))
            {
                return Task.CompletedTask;
            }

            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            });
        }
        catch (Exception)
        {
            // 拉不起来就算了：上层会以"界面没运行"的结果告诉 AI，用户自己会去开
        }

        return Task.CompletedTask;
    }
}
