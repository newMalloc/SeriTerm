using System.Text;
using System.Text.RegularExpressions;
using SeriTerm.Core.Text;

namespace SeriTerm.Core.Mcp;

/// <summary>
/// 关键词匹配器。<see cref="McpPatternMode.Text"/> / <see cref="McpPatternMode.Regex"/>
/// 在解码后的文本上匹配，<see cref="McpPatternMode.Hex"/> 直接在原始字节上找字节串
/// —— 二进制协议里的控制字符（0D 0A、7E 7E）用文本是打不出来的。
/// </summary>
public sealed class McpPatternMatcher
{
    /// <summary>正则超时：串口数据是不可信输入，不能让一条回溯爆炸的正则把界面进程挂住。</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    private readonly McpPatternMode _mode;
    private readonly string _text;
    private readonly StringComparison _comparison;
    private readonly Regex? _regex;
    private readonly byte[] _bytes;

    private McpPatternMatcher(McpPatternMode mode, string text, StringComparison comparison, Regex? regex, byte[] bytes)
    {
        _mode = mode;
        _text = text;
        _comparison = comparison;
        _regex = regex;
        _bytes = bytes;
    }

    /// <summary>是否需要调用方先把字节解码成文本（HEX 模式可以省掉这一步）。</summary>
    public bool NeedsText => _mode is McpPatternMode.Text or McpPatternMode.Regex;

    public static bool TryCreate(McpPatternQuery query, out McpPatternMatcher? matcher, out string error)
    {
        matcher = null;
        error = string.Empty;

        if (string.IsNullOrEmpty(query.Pattern))
        {
            error = "pattern 不能为空。";
            return false;
        }

        var comparison = query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        switch (query.Mode)
        {
            case McpPatternMode.Hex:
                if (!HexCodec.TryParse(query.Pattern, out var bytes, out var hexError))
                {
                    error = $"hex 模式下的 pattern 解析失败：{hexError}";
                    return false;
                }

                matcher = new McpPatternMatcher(McpPatternMode.Hex, query.Pattern, comparison, null, bytes);
                return true;

            case McpPatternMode.Regex:
                try
                {
                    var options = RegexOptions.CultureInvariant;

                    if (!query.CaseSensitive)
                    {
                        options |= RegexOptions.IgnoreCase;
                    }

                    var regex = new Regex(query.Pattern, options, RegexTimeout);
                    matcher = new McpPatternMatcher(McpPatternMode.Regex, query.Pattern, comparison, regex, []);
                    return true;
                }
                catch (ArgumentException ex)
                {
                    error = $"正则表达式无效：{ex.Message}";
                    return false;
                }

            default:
                matcher = new McpPatternMatcher(McpPatternMode.Text, query.Pattern, comparison, null, []);
                return true;
        }
    }

    /// <summary><paramref name="text"/> 由调用方按当前编码解码得出；HEX 模式可以不传。</summary>
    public bool IsMatch(ReadOnlySpan<byte> payload, ReadOnlySpan<char> text) => _mode switch
    {
        McpPatternMode.Hex => ContainsBytes(payload, _bytes),
        McpPatternMode.Regex => TryRegex(text),
        _ => text.Contains(_text, _comparison),
    };

    private bool TryRegex(ReadOnlySpan<char> text)
    {
        try
        {
            return _regex!.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // 超时按"没匹配"处理：宁可这一轮不命中，也不能卡住整个会话
            return false;
        }
    }

    private static bool ContainsBytes(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty || haystack.Length < needle.Length)
        {
            return false;
        }

        return haystack.IndexOf(needle) >= 0;
    }

    /// <summary>把字节按指定编码解码（不净化、不过滤 ANSI）：匹配要贴近原始数据。</summary>
    public static string Decode(ReadOnlySpan<byte> payload, Encoding encoding)
        => payload.IsEmpty ? string.Empty : encoding.GetString(payload);
}
