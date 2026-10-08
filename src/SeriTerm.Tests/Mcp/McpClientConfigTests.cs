using System.Text.Json;
using System.Text.Json.Nodes;
using SeriTerm.Core.Mcp;

namespace SeriTerm.Tests.Mcp;

/// <summary>
/// 钉住 <see cref="McpProtocol"/> 那两组共享 JSON 选项"能直接喂给 <see cref="JsonNode.ToJsonString(JsonSerializerOptions)"/>"
/// 这一契约。
///
/// 为什么单独测这个：界面上「复制配置」是拿 <see cref="JsonObject"/> 拼出配置片段再转成文本的。
/// 而 <c>ToJsonString</c> 会把传入的 options **标记为只读**，只读时若 <c>TypeInfoResolver</c>
/// 仍为空，运行时直接抛 <see cref="InvalidOperationException"/>
/// （"must specify a TypeInfoResolver setting before being marked as read-only"）。
/// 线上曾在界面那侧当场 new 了一个只设 <c>WriteIndented</c> 的实例，点「复制配置」必弹错误框，
/// 而当时 354 个测试全绿——整条链路一条测试都没有。
///
/// 注意本文件的覆盖边界：测试项目是 <c>net8.0</c>，引用不了 WPF 的 <c>SeriTerm.App</c>，
/// 所以这里**测不到界面那侧的具体用法**，只能保证共享选项本身合格、
/// 以及"漏配解析器确实会崩"这个事实有据可查（见最后一个用例）。
/// </summary>
public class McpClientConfigTests
{
    /// <summary>复刻界面上拼配置片段的结构（命令 + <c>--mcp-stdio</c>）。</summary>
    private static JsonObject BuildConfig() => new()
    {
        ["mcpServers"] = new JsonObject
        {
            ["seriterm"] = new JsonObject
            {
                ["command"] = @"C:\Program Files\SeriTerm\SeriTerm.exe",
                ["args"] = new JsonArray { "--mcp-stdio" },
            },
        },
    };

    [Fact]
    public void 缩进选项必须带解析器且能序列化JsonNode()
    {
        // 生产代码里就是这一句：漏了 TypeInfoResolver 就会在这里抛
        var json = BuildConfig().ToJsonString(McpProtocol.PrettyJson);

        var parsed = JsonNode.Parse(json)!;
        var seriterm = parsed["mcpServers"]!["seriterm"]!;

        Assert.Equal(@"C:\Program Files\SeriTerm\SeriTerm.exe", (string?)seriterm["command"]);
        Assert.Equal("--mcp-stdio", (string?)seriterm["args"]![0]);

        // 给人粘贴用的片段，缩进过
        Assert.Contains('\n', json);
    }

    [Fact]
    public void 缩进选项必须是缩进且camelCase的()
    {
        // 界面那侧依赖这两点来决定"直接复用共享选项、不另造实例"
        Assert.True(McpProtocol.PrettyJson.WriteIndented);
        Assert.NotNull(McpProtocol.PrettyJson.TypeInfoResolver);
    }

    [Fact]
    public void 线路选项也必须能序列化JsonNode()
    {
        var json = BuildConfig().ToJsonString(McpProtocol.WireJson);

        Assert.Contains("\"mcpServers\"", json);
        Assert.DoesNotContain('\n', json);
    }

    [Fact]
    public void 带解析器的选项在同一实例上可反复序列化()
    {
        // options 第一次用后即被标记为只读：反复调用不能因此失败
        for (var i = 0; i < 3; i++)
        {
            Assert.Contains("--mcp-stdio", BuildConfig().ToJsonString(McpProtocol.PrettyJson));
        }
    }

    /// <summary>
    /// 把"漏配解析器就会崩"钉死，防止有人把上面几条测试当成空转：
    /// 这里主动复现修复前那个当场 new 出来的选项实例，确认它确实抛异常。
    /// </summary>
    [Fact]
    public void 缺少TypeInfoResolver的选项序列化JsonNode时确实会抛异常()
    {
        var broken = new JsonSerializerOptions { WriteIndented = true };

        var error = Assert.Throws<InvalidOperationException>(
            () => BuildConfig().ToJsonString(broken));

        Assert.Contains("TypeInfoResolver", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 这条只用于把"触发条件"记录清楚，避免后来人误判：
    /// 该异常来自 System.Text.Json 8，换到 .NET 10 的运行时上同样写法不再抛
    /// （本机实测：pwsh 7.6.6 / .NET 10 上全部形状都不抛）。所以验证必须跑在 net8.0 上，
    /// 不能拿桌面上的 pwsh 去验。这里断言当前测试进程**确实**是 .NET 8，
    /// 一旦将来把测试项目升到更高 TFM，这条会立刻提醒"该结论需要重新实测"。
    /// </summary>
    [Fact]
    public void 本组结论只对NET8成立()
    {
        var major = Environment.Version.Major;

        Assert.True(
            major == 8,
            $"本组关于 TypeInfoResolver 的结论是在 .NET 8 上实测的，当前测试进程是 .NET {major}。" +
            "请在该运行时上重新确认“缺少解析器是否仍会抛异常”，再决定这些测试怎么改。");
    }
}
