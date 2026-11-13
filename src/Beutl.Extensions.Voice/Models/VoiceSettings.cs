using System.Text.Json.Serialization;

namespace Beutl.Extensions.Voice.Models;

// A value snapshot shared by presets, the editor, and generated elements.
public sealed record VoiceSettings
{
    public const string CoreEngineId = "voicevox-core";
    // The ID does not depend on where the app is, so presets that use it carry over between computers.
    public const string AquesTalkPlayerEngineId = "aquestalkplayer";

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

    // AquesTalkPlayer's own values, written to a temporary preset when they differ from the preset's. Without them the
    // preset speaks as the app has it.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AquesTalkPlayerVoice? AquesTalkPlayerVoice { get; init; }

    [JsonIgnore]
    public bool IsAquesTalkPlayer => EngineId == AquesTalkPlayerEngineId;

    public bool UsesSameVoice(VoiceSettings other) =>
        EngineId == other.EngineId && SpeakerUuid == other.SpeakerUuid && StyleId == other.StyleId;

    // AquesTalkPlayer has values of its own in place of the speed, pitch and volume of the other engines, which have
    // none of its values.
    public VoiceSettings WithSupportedTuning() => IsAquesTalkPlayer
        ? this with
        {
            SpeedScale = 1, PitchScale = 0, IntonationScale = 1, VolumeScale = 1, TempoDynamicsScale = null,
            AquesTalkPlayerVoice = AquesTalkPlayerVoice?.Clamped()
        }
        : AquesTalkPlayerVoice == null ? this : this with { AquesTalkPlayerVoice = null };

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
        (IsAquesTalkPlayer ? AquesTalkPlayerVoice is { } voice ? voice.Describe() + "\n" : "" :
            $"話速 {SpeedScale:G}・音高 {PitchScale:G}・{(TempoDynamicsScale.HasValue ? "感情" : "抑揚")} {IntonationScale:G}・音量 {VolumeScale:G}\n") +
        $"前無音 {PrePhonemeLength:G} 秒・後無音 {PostPhonemeLength:G} 秒" +
        (TempoDynamicsScale.HasValue && !IsAquesTalkPlayer ? $"・緩急 {TempoDynamicsScale:G}" : "");

    public string DescribeDifferences(VoiceSettings current)
    {
        var differences = new List<string>();
        if (!UsesSameVoice(current)) differences.Add($"声: {SpeakerName} / {StyleName} → {current.SpeakerName} / {current.StyleName}");
        if (ModelVersion != current.ModelVersion) differences.Add($"モデル: {ModelVersion} → {current.ModelVersion}");
        if (EngineId != current.EngineId) differences.Add($"エンジン: {EngineName} → {current.EngineName}");
        // AquesTalkPlayer voices differ in AquesTalkPlayer's own values instead of the other engines' tuning.
        bool tuning = !IsAquesTalkPlayer || !current.IsAquesTalkPlayer;
        if (!tuning && AquesTalkPlayerVoice != current.AquesTalkPlayerVoice)
            differences.Add($"AquesTalkPlayer: {DescribeVoice(AquesTalkPlayerVoice)} → {DescribeVoice(current.AquesTalkPlayerVoice)}");
        if (tuning)
        {
            Add("話速", SpeedScale, current.SpeedScale);
            Add("音高", PitchScale, current.PitchScale);
            Add("抑揚", IntonationScale, current.IntonationScale);
            Add("音量", VolumeScale, current.VolumeScale);
        }
        Add("前無音", PrePhonemeLength, current.PrePhonemeLength);
        Add("後無音", PostPhonemeLength, current.PostPhonemeLength);
        if (tuning && TempoDynamicsScale != current.TempoDynamicsScale)
            differences.Add($"緩急: {TempoDynamicsScale:G} → {current.TempoDynamicsScale:G}");
        return differences.Count == 0 ? "現在の音声設定と一致しています。" : string.Join("\n", differences);

        void Add(string name, double saved, double value)
        {
            if (saved != value) differences.Add($"{name}: {saved:G} → {value:G}");
        }

        static string DescribeVoice(AquesTalkPlayerVoice? voice) =>
            voice == null ? "プリセットのまま" : $"{voice.Engine} {voice.Voice}・{voice.Describe()}";
    }
}
