using System.Text.Json.Serialization;

namespace Beutl.Extensions.Voice.Models;

/// <summary>
/// 音声合成用のクエリ
/// </summary>
public class AudioQuery
{
    /// <summary>
    /// アクセント句のリスト
    /// </summary>
    [JsonPropertyName("accent_phrases")]
    public AccentPhrase[] AccentPhrases { get; set; } = [];

    /// <summary>
    /// 全体の話速
    /// </summary>
    [JsonPropertyName("speedScale")]
    public float SpeedScale { get; set; } = 1.0f;

    /// <summary>
    /// 全体の音高
    /// </summary>
    [JsonPropertyName("pitchScale")]
    public float PitchScale { get; set; } = 0.0f;

    /// <summary>
    /// 全体の抑揚
    /// </summary>
    [JsonPropertyName("intonationScale")]
    public float IntonationScale { get; set; } = 1.0f;

    /// <summary>
    /// 全体の音量
    /// </summary>
    [JsonPropertyName("volumeScale")]
    public float VolumeScale { get; set; } = 1.0f;

    /// <summary>
    /// 音声の前の無音時間（秒）
    /// </summary>
    [JsonPropertyName("prePhonemeLength")]
    public float PrePhonemeLength { get; set; } = 0.1f;

    /// <summary>
    /// 音声の後の無音時間（秒）
    /// </summary>
    [JsonPropertyName("postPhonemeLength")]
    public float PostPhonemeLength { get; set; } = 0.1f;

    /// <summary>
    /// 音声データの出力サンプリングレート
    /// </summary>
    [JsonPropertyName("outputSamplingRate")]
    public int OutputSamplingRate { get; set; } = 24000;

    /// <summary>
    /// 音声データをステレオ出力するか否か
    /// </summary>
    [JsonPropertyName("outputStereo")]
    public bool OutputStereo { get; set; } = false;

    /// <summary>
    /// [読み取り専用] AquesTalk風記法
    /// </summary>
    [JsonPropertyName("kana")]
    public string? Kana { get; set; }
}
