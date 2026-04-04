using System.Text.Json.Serialization;

namespace Beutl.Extensions.Voice.Models;

public class AudioQueryModel
{
    [JsonPropertyName("accent_phrases")]
    public List<AccentPhraseModel> AccentPhrases { get; set; } = [];

    [JsonPropertyName("speedScale")]
    public double SpeedScale { get; set; } = 1.0;

    [JsonPropertyName("pitchScale")]
    public double PitchScale { get; set; }

    [JsonPropertyName("intonationScale")]
    public double IntonationScale { get; set; } = 1.0;

    [JsonPropertyName("volumeScale")]
    public double VolumeScale { get; set; } = 1.0;

    [JsonPropertyName("prePhonemeLength")]
    public double PrePhonemeLength { get; set; } = 0.1;

    [JsonPropertyName("postPhonemeLength")]
    public double PostPhonemeLength { get; set; } = 0.1;

    [JsonPropertyName("pauseLength")]
    public double? PauseLength { get; set; }

    [JsonPropertyName("pauseLengthScale")]
    public double PauseLengthScale { get; set; } = 1.0;

    [JsonPropertyName("outputSamplingRate")]
    public int OutputSamplingRate { get; set; } = 24000;

    [JsonPropertyName("outputStereo")]
    public bool OutputStereo { get; set; }

    [JsonPropertyName("kana")]
    public string? Kana { get; set; }
}

public class AccentPhraseModel
{
    [JsonPropertyName("moras")]
    public List<MoraModel> Moras { get; set; } = [];

    [JsonPropertyName("accent")]
    public int Accent { get; set; }

    [JsonPropertyName("pause_mora")]
    public MoraModel? PauseMora { get; set; }

    [JsonPropertyName("is_interrogative")]
    public bool IsInterrogative { get; set; }
}

public class MoraModel
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("consonant")]
    public string? Consonant { get; set; }

    [JsonPropertyName("consonant_length")]
    public double? ConsonantLength { get; set; }

    [JsonPropertyName("vowel")]
    public string Vowel { get; set; } = "";

    [JsonPropertyName("vowel_length")]
    public double VowelLength { get; set; }

    [JsonPropertyName("pitch")]
    public double Pitch { get; set; }
}
