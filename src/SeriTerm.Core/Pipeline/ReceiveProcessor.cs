using System.Diagnostics;
using SeriTerm.Core.Framing;
using SeriTerm.Core.Text;

namespace SeriTerm.Core.Pipeline;

/// <summary>
/// 接收处理器：原始字节 → 断帧 → 文本/HEX 渲染 → <see cref="DisplayLine"/>。
///
/// 线程约定：本类**不是**线程安全的。串口读取线程负责喂入数据、界面定时器负责 FlushIdle，
/// 两者必须由调用方用同一个锁串行化（见 MainViewModel 中的用法）。
/// </summary>
public sealed class ReceiveProcessor
{
    private readonly List<RawFrame> _frames = [];

    /// <summary>Rx 流式解码器：跨帧保留状态，避免中文被切包后乱码。</summary>
    private StatefulTextDecoder _rxDecoder;

    /// <summary>Tx 独立解码器：发送的数据按行完整解码，不干扰 Rx 的流式状态。</summary>
    private StatefulTextDecoder _txDecoder;

    /// <summary>重刷已有行时使用的独立解码器。</summary>
    private StatefulTextDecoder _reformatDecoder;

    private IFrameSplitter _splitter;
    private long _sequence;

    public ReceiveProcessor(ReceiveOptions options)
    {
        Options = (options ?? new ReceiveOptions()).Normalize();
        _rxDecoder = new StatefulTextDecoder(Options.EncodingName);
        _txDecoder = new StatefulTextDecoder(Options.EncodingName);
        _reformatDecoder = new StatefulTextDecoder(Options.EncodingName);
        _splitter = CreateSplitter(Options);
    }

    public ReceiveOptions Options { get; private set; }

    /// <summary>当前挂起（等待断帧）的字节数。</summary>
    public int PendingByteCount => _splitter.PendingByteCount;

    /// <summary>
    /// 应用新设置。断帧方式/间隔/分隔符变化时，会先把挂起数据吐出来，避免这段数据永远不显示。
    /// </summary>
    public void ApplyOptions(ReceiveOptions options, DateTime wallClock, List<DisplayLine> output)
    {
        var normalized = (options ?? new ReceiveOptions()).Normalize();

        var framingChanged = normalized.Framing != Options.Framing
            || normalized.AutoFrameGapMilliseconds != Options.AutoFrameGapMilliseconds
            || !string.Equals(normalized.DelimiterText, Options.DelimiterText, StringComparison.Ordinal);

        if (framingChanged)
        {
            _frames.Clear();
            _splitter.FlushAll(_frames);
            Materialize(_frames, LineDirection.Rx, wallClock, Stopwatch.GetTimestamp(), output);
            _splitter = CreateSplitter(normalized);
        }

        Options = normalized;
        _rxDecoder.SetEncoding(normalized.EncodingName);
        _txDecoder.SetEncoding(normalized.EncodingName);
    }

    /// <summary>处理一块接收数据。等待断帧时可能不产出任何行。</summary>
    public void ProcessReceived(
        ReadOnlySpan<byte> data,
        long timestamp,
        DateTime wallClock,
        List<DisplayLine> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (data.IsEmpty)
        {
            return;
        }

        _frames.Clear();

        if (Options.Framing == FramingMode.None)
        {
            // 不断帧：每块数据直接作为一帧
            _frames.Add(new RawFrame(timestamp, data.ToArray()));
        }
        else
        {
            _splitter.Append(data, timestamp, _frames);
        }

        Materialize(_frames, LineDirection.Rx, wallClock, timestamp, output);
    }

    /// <summary>由定时器调用：空闲达到阈值时把最后一帧吐出来。</summary>
    public void FlushIdle(long now, DateTime wallClock, List<DisplayLine> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (Options.Framing == FramingMode.None)
        {
            return;
        }

        _frames.Clear();
        _splitter.FlushIdle(now, _frames);
        Materialize(_frames, LineDirection.Rx, wallClock, now, output);
    }

    /// <summary>记录一行发送数据（不参与断帧，立即成行）。</summary>
    public void ProcessTransmitted(ReadOnlySpan<byte> data, DateTime wallClock, List<DisplayLine> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (data.IsEmpty)
        {
            return;
        }

        var raw = data.ToArray();
        var text = Options.HexDisplay
            ? HexCodec.Format(raw)
            : Sanitize(_txDecoder.Decode(raw, flush: true));

        output.Add(new DisplayLine(++_sequence, wallClock, LineDirection.Tx, raw, text));
    }

    /// <summary>添加一行工具自身的提示（如"串口已打开"）。</summary>
    public void ProcessSystem(string message, DateTime wallClock, List<DisplayLine> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Add(new DisplayLine(++_sequence, wallClock, LineDirection.System, [], message));
    }

    /// <summary>切换 HEX/编码后重刷已有一行的显示文本（不复用流式解码状态）。</summary>
    public string Reformat(DisplayLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var payload = line.DisplayBytes;

        if (Options.HexDisplay)
        {
            return HexCodec.Format(payload);
        }

        _reformatDecoder.SetEncoding(Options.EncodingName);
        return RenderText(_reformatDecoder.Decode(payload, flush: true));
    }

    /// <summary>清空挂起数据并重置解码状态。</summary>
    public void Reset()
    {
        _splitter.Reset();
        _rxDecoder.Reset();
        _txDecoder.Reset();
        _reformatDecoder.Reset();
    }

    private static IFrameSplitter CreateSplitter(ReceiveOptions options)
    {
        if (options.Framing == FramingMode.Delimiter
            && BytePatternParser.TryParse(options.DelimiterText, out var delimiter, out _))
        {
            return new DelimiterFrameSplitter(delimiter);
        }

        // 分隔符写错时退回空闲断帧，保证有数据显示而不是静默失效
        return new GapFrameSplitter(TimeSpan.FromMilliseconds(options.AutoFrameGapMilliseconds));
    }

    /// <summary>
    /// 把帧转成显示行。<paramref name="nowTicks"/> 是"当前时刻"，
    /// 用它把帧的起始刻度换算成墙钟时间，这样空闲断帧吐出来的行也能显示正确的到达时间。
    /// </summary>
    private void Materialize(
        List<RawFrame> frames,
        LineDirection direction,
        DateTime wallClock,
        long nowTicks,
        List<DisplayLine> output)
    {
        if (frames.Count == 0)
        {
            return;
        }

        foreach (var frame in frames)
        {
            // 只渲染有效载荷：分隔符断帧时行尾的 CRLF 不算内容
            var payload = frame.DisplayBytes;

            var text = Options.HexDisplay
                ? HexCodec.Format(payload)
                : RenderText(_rxDecoder.Decode(payload, flush: false));

            var lag = nowTicks > frame.StartTimestamp
                ? TimeSpan.FromSeconds((nowTicks - frame.StartTimestamp) / (double)Stopwatch.Frequency)
                : TimeSpan.Zero;

            output.Add(new DisplayLine(
                ++_sequence,
                wallClock - lag,
                direction,
                frame.Data,
                text,
                frame.DisplayLength));
        }

        frames.Clear();
    }

    /// <summary>接收文本的显示处理：先按需过滤 ANSI，再做换行净化。</summary>
    private string RenderText(string text)
        => Sanitize(Options.StripAnsi ? AnsiFilter.Strip(text) : text);

    /// <summary>
    /// 文本模式下的显示净化：把 CR/LF 显示成空格（否则一行文本会被拆成多行、界面错乱），
    /// 并去掉尾部空白。HEX 模式不做任何处理，原始字节永远可查。
    /// </summary>
    private static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\n', ' ')
            .TrimEnd();
    }
}
