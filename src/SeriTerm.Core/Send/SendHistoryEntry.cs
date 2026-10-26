namespace SeriTerm.Core.Send;

/// <summary>A reusable send draft; selecting it never transmits data.</summary>
public sealed record SendHistoryEntry(string Text, bool Hex, LineEnding LineEnding, string EncodingName)
{
    public string Label
    {
        get
        {
            var preview = Text.Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 60) preview = preview[..60] + "…";
            return $"{(Hex ? "HEX" : "文本")} · {preview} · {SendPayloadBuilder.LineEndingText(LineEnding)}";
        }
    }

    public override string ToString() => Label;
}
