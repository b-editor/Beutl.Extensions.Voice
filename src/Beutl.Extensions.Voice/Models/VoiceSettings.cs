namespace Beutl.Extensions.Voice.Models;

// A value snapshot shared by presets, the editor, and generated elements.
public sealed record VoiceSettings
{
    public const string CoreEngineId = "voicevox-core";

    public string EngineId { get; init; } = CoreEngineId;
    public string SpeakerUuid { get; init; } = "";
    public string SpeakerName { get; init; } = "";
    public uint StyleId { get; init; }
    public string StyleName { get; init; } = "";
    public string ModelVersion { get; init; } = "";
    public double SpeedScale { get; init; } = 1;
    public double PitchScale { get; init; }
    public double IntonationScale { get; init; } = 1;
    public double VolumeScale { get; init; } = 1;
    public double PrePhonemeLength { get; init; } = 0.1;
    public double PostPhonemeLength { get; init; } = 0.1;

    public bool UsesSameVoice(VoiceSettings other) =>
        EngineId == other.EngineId && SpeakerUuid == other.SpeakerUuid && StyleId == other.StyleId;

    public void ApplyTo(AudioQueryModel query)
    {
        query.SpeedScale = SpeedScale;
        query.PitchScale = PitchScale;
        query.IntonationScale = IntonationScale;
        query.VolumeScale = VolumeScale;
        query.PrePhonemeLength = PrePhonemeLength;
        query.PostPhonemeLength = PostPhonemeLength;
    }

    public string Describe() =>
        $"{(EngineId == CoreEngineId ? "VOICEVOX Core" : EngineId)} / {SpeakerName} / {StyleName}\n" +
        $"話速 {SpeedScale:G}・音高 {PitchScale:G}・抑揚 {IntonationScale:G}・音量 {VolumeScale:G}\n" +
        $"前無音 {PrePhonemeLength:G} 秒・後無音 {PostPhonemeLength:G} 秒";

    public string DescribeDifferences(VoiceSettings current)
    {
        var differences = new List<string>();
        if (!UsesSameVoice(current)) differences.Add($"声: {SpeakerName} / {StyleName} → {current.SpeakerName} / {current.StyleName}");
        if (ModelVersion != current.ModelVersion) differences.Add($"モデル: {ModelVersion} → {current.ModelVersion}");
        Add("話速", SpeedScale, current.SpeedScale);
        Add("音高", PitchScale, current.PitchScale);
        Add("抑揚", IntonationScale, current.IntonationScale);
        Add("音量", VolumeScale, current.VolumeScale);
        Add("前無音", PrePhonemeLength, current.PrePhonemeLength);
        Add("後無音", PostPhonemeLength, current.PostPhonemeLength);
        return differences.Count == 0 ? "現在の設定と一致しています。" : string.Join("\n", differences);

        void Add(string name, double saved, double value)
        {
            if (saved != value) differences.Add($"{name}: {saved:G} → {value:G}");
        }
    }
}
