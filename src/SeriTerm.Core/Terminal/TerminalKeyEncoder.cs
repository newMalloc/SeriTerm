using SeriTerm.Core.Send;
using SeriTerm.Core.Text;

namespace SeriTerm.Core.Terminal;

/// <summary>终端模式设置。</summary>
public sealed record TerminalOptions
{
    /// <summary>回车时发送的行尾（终端设备一般用 CRLF 或 CR）。</summary>
    public LineEnding EnterEnding { get; init; } = LineEnding.CrLf;

    /// <summary>退格发送 0x7F(DEL) 还是 0x08(BS)；有些 CLI 只认其中之一。</summary>
    public bool BackspaceSendsDel { get; init; } = true;

    public string EncodingName { get; init; } = "UTF-8";

    /// <summary>本地回显：把敲入的内容按行记入日志（设备通常也会回显，二选一）。</summary>
    public bool LocalEcho { get; init; }

    public TerminalOptions Normalize() => this with
    {
        EncodingName = string.IsNullOrWhiteSpace(EncodingName) ? "UTF-8" : EncodingName,
    };
}

/// <summary>
/// 终端模式的按键 → 字节编码。抽成纯函数便于单测：终端交互的坑基本都在这些小细节上。
/// </summary>
public static class TerminalKeyEncoder
{
    /// <summary>要发送的普通字符（可能是一个汉字，按编码会变成多个字节）。</summary>
    public static byte[] EncodeText(string text, string encodingName)
        => string.IsNullOrEmpty(text) ? [] : StatefulTextDecoder.Resolve(encodingName).GetBytes(text);

    /// <summary>回车：按设置附加行尾。</summary>
    public static byte[] EncodeEnter(LineEnding ending) => SendPayloadBuilder.GetLineEndingBytes(ending);

    /// <summary>退格：0x7F(DEL) 或 0x08(BS)。</summary>
    public static byte[] EncodeBackspace(bool sendDel) => sendDel ? [0x7F] : [0x08];

    /// <summary>Ctrl + 字母：0x01–0x1A（例如 Ctrl+C → 0x03）。</summary>
    public static byte[] EncodeControl(char letter)
    {
        var upper = char.ToUpperInvariant(letter);

        return upper is >= 'A' and <= 'Z' ? [(byte)(upper - 'A' + 1)] : [];
    }

    /// <summary>是否可以当作普通输入发送（排除控制字符，其余含中文都算）。</summary>
    public static bool IsPrintable(char c) => !char.IsControl(c);

    /// <summary>方向键等 CSI 序列（终端模式下透传给设备）。</summary>
    public static byte[] EncodeArrow(TerminalArrow arrow) => arrow switch
    {
        TerminalArrow.Up => "\u001b[A"u8.ToArray(),
        TerminalArrow.Down => "\u001b[B"u8.ToArray(),
        TerminalArrow.Right => "\u001b[C"u8.ToArray(),
        TerminalArrow.Left => "\u001b[D"u8.ToArray(),
        _ => [],
    };
}

public enum TerminalArrow
{
    Up,
    Down,
    Left,
    Right,
}
