namespace Beutl.Extensions.Voice.Models;

// A value snapshot shared by presets, the editor, and generated elements.
public sealed record VoiceSettings
{
    public const string CoreEngineId = "voicevox-core";

    public string EngineId { get; init; } = CoreEngineId;
    public string EngineName { get; init; } = "VOICEVOX Core";
    public string EngineUuid { get; init; } = "";
    public string EngineVersion { get; init; } = "";
    public string SpeakerUuid { get; init; } = "";
    public string SpeakerName { get; init; } = "";
    public long StyleId { get; init; }
    public string StyleName { get; init; } = "";
    public string ModelVersion { get; init; } = "";
    public double SpeedScale { get; init; } = 1;
    public double PitchScale { get; init; }
    public double IntonationScale { get; init; } = 1;
    public double VolumeScale { get; init; } = 1;
    public double PrePhonemeLength { get; init; } = 0.1;
    public double PostPhonemeLength { get; init; } = 0.1;
    public double? TempoDynamicsScale { get; init; }

    public bool UsesSameVoice(VoiceSettings other) =>
        EngineId == other.EngineId && SpeakerUuid == other.SpeakerUuid && StyleId == other.StyleId;

    public void ApplyTo(AudioQueryModel query, VoiceEngineCapabilities? capabilities = null)
    {
        if (capabilities?.AdjustSpeed != false) query.SpeedScale = SpeedScale;
        if (capabilities?.AdjustPitch != false) query.PitchScale = PitchScale;
        if (capabilities?.AdjustIntonation != false) query.IntonationScale = IntonationScale;
        if (capabilities?.AdjustVolume != false) query.VolumeScale = VolumeScale;
        query.PrePhonemeLength = PrePhonemeLength;
        query.PostPhonemeLength = PostPhonemeLength;
        if (capabilities?.TempoDynamics == true && TempoDynamicsScale.HasValue)
            query.TempoDynamicsScale = TempoDynamicsScale;
    }

    public string Describe() =>
        $"{EngineName} {EngineVersion} / {SpeakerName} / {StyleName}\n" +
        $"話速 {SpeedScale:G}・音高 {PitchScale:G}・{(TempoDynamicsScale.HasValue ? "感情" : "抑揚")} {IntonationScale:G}・音量 {VolumeScale:G}\n" +
        $"前無音 {PrePhonemeLength:G} 秒・後無音 {PostPhonemeLength:G} 秒" +
        (TempoDynamicsScale.HasValue ? $"・緩急 {TempoDynamicsScale:G}" : "");

    public string DescribeDifferences(VoiceSettings current)
    {
        var differences = new List<string>();
        if (!UsesSameVoice(current)) differences.Add($"声: {SpeakerName} / {StyleName} → {current.SpeakerName} / {current.StyleName}");
        if (ModelVersion != current.ModelVersion) differences.Add($"モデル: {ModelVersion} → {current.ModelVersion}");
        if (EngineId != current.EngineId) differences.Add($"エンジン: {EngineName} → {current.EngineName}");
        Add("話速", SpeedScale, current.SpeedScale);
        Add("音高", PitchScale, current.PitchScale);
        Add("抑揚", IntonationScale, current.IntonationScale);
        Add("音量", VolumeScale, current.VolumeScale);
        Add("前無音", PrePhonemeLength, current.PrePhonemeLength);
        Add("後無音", PostPhonemeLength, current.PostPhonemeLength);
        if (TempoDynamicsScale != current.TempoDynamicsScale)
            differences.Add($"緩急: {TempoDynamicsScale:G} → {current.TempoDynamicsScale:G}");
        return differences.Count == 0 ? "現在の音声設定と一致しています。" : string.Join("\n", differences);

        void Add(string name, double saved, double value)
        {
            if (saved != value) differences.Add($"{name}: {saved:G} → {value:G}");
        }
    }
}
