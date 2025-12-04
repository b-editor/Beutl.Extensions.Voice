using System.Text.Json.Serialization;

namespace Beutl.Extensions.Voice.Models;

/// <summary>
/// モーラ（音声の最小単位）
/// </summary>
public class Mora
{
    /// <summary>
    /// 文字
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    /// <summary>
    /// 子音の音素
    /// </summary>
    [JsonPropertyName("consonant")]
    public string? Consonant { get; set; }

    /// <summary>
    /// 子音の音長（秒）
    /// </summary>
    [JsonPropertyName("consonant_length")]
    public float? ConsonantLength { get; set; }

    /// <summary>
    /// 母音の音素
    /// </summary>
    [JsonPropertyName("vowel")]
    public string Vowel { get; set; } = "";

    /// <summary>
    /// 母音の音長（秒）
    /// </summary>
    [JsonPropertyName("vowel_length")]
    public float VowelLength { get; set; }

    /// <summary>
    /// 音高（Hz）
    /// </summary>
    [JsonPropertyName("pitch")]
    public float Pitch { get; set; }
}
