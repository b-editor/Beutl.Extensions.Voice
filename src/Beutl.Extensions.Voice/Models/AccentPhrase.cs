using System.Text.Json.Serialization;

namespace Beutl.Extensions.Voice.Models;

/// <summary>
/// アクセント句
/// </summary>
public class AccentPhrase
{
    /// <summary>
    /// モーラのリスト
    /// </summary>
    [JsonPropertyName("moras")]
    public Mora[] Moras { get; set; } = [];

    /// <summary>
    /// アクセント位置（1から始まる）
    /// </summary>
    [JsonPropertyName("accent")]
    public int Accent { get; set; }

    /// <summary>
    /// 疑問文かどうか
    /// </summary>
    [JsonPropertyName("is_interrogative")]
    public bool IsInterrogative { get; set; }

    /// <summary>
    /// 後ろに無音を付けるか
    /// </summary>
    [JsonPropertyName("pause_mora")]
    public Mora? PauseMora { get; set; }
}
