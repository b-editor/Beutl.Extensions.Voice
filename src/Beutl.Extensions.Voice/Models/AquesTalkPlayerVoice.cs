using System.Text.Json.Serialization;

namespace Beutl.Extensions.Voice.Models;

// What an AquesTalkPlayer preset sets: the voice type and the values of its sliders, in the app's own units.
public sealed record AquesTalkPlayerVoice
{
    // AquesTalk1, AquesTalk2 or AquesTalk10.
    public string Engine { get; init; } = "";

    // The voice type of the engine, such as f1, aq_yukkuri or F1E.
    public string Voice { get; init; } = "";

    // 棒読み: every phrase reads flat.
    public bool FlatReading { get; init; }

    // 話速, 50 to 300.
    public int Speed { get; init; } = 100;

    // 音量, 0 to 300; above 100 the voice distorts.
    public int Volume { get; init; } = 100;

    // 高さ, 20 to 200, AquesTalk10 only.
    public int Height { get; init; } = 100;

    // アクセント, 0 to 200, AquesTalk10 only.
    public int Accent { get; init; } = 100;

    // 声質, 0 to 200, AquesTalk10 only.
    public int Quality { get; init; } = 100;

    // 音程, 50 to 200: the sample rate is scaled, so the speed changes with it.
    public int FrequencyScale { get; init; } = 100;

    [JsonIgnore]
    public bool IsAquesTalk10 => Engine == "AquesTalk10";

    // The voice type as the style of its speaker, such as "AquesTalk1 f1".
    [JsonIgnore]
    public string StyleName => $"{Engine} {Voice}".Trim();

    // Values from a project or an MCP client can name anything, but only a voice type of the app may be written to its
    // preset file, where the app looks the voice up and the Windows version separates columns and rows.
    [JsonIgnore]
    public bool HasValidVoiceType => Engine is "AquesTalk1" or "AquesTalk2" or "AquesTalk10"
        && Voice.Length is > 0 and <= 32 && Voice.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public AquesTalkPlayerVoice Clamped() => this with
    {
        Speed = Math.Clamp(Speed, 50, 300), Volume = Math.Clamp(Volume, 0, 300), Height = Math.Clamp(Height, 20, 200),
        Accent = Math.Clamp(Accent, 0, 200), Quality = Math.Clamp(Quality, 0, 200), FrequencyScale = Math.Clamp(FrequencyScale, 50, 200)
    };

    // The values; the voice type is shown where the style is.
    public string Describe() =>
        $"話速 {Speed}・音量 {Volume}" +
        (IsAquesTalk10 ? $"・高さ {Height}・アクセント {Accent}・声質 {Quality}" : "") +
        $"・音程 {FrequencyScale}" + (FlatReading ? "・棒読み" : "");
}
