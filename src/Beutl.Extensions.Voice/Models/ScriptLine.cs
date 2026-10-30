namespace Beutl.Extensions.Voice.Models;

public sealed record ScriptLine(string Speaker, string Text)
{
    public static ScriptLine[] Parse(string text, bool hasSpeakerNames)
    {
        var lines = new List<ScriptLine>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int separator = hasSpeakerNames ? line.IndexOfAny([':', '：']) : -1;
            lines.Add(separator < 0
                ? new("", line.Trim())
                : new(line[..separator].Trim(), line[(separator + 1)..].Trim()));
        }
        return lines.ToArray();
    }
}
