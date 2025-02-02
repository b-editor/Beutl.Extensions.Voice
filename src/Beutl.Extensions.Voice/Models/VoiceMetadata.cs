using System.Text.Json.Serialization;

namespace Beutl.Extensions.Voice.Models;

public class VoiceMetadata
{
    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; }

    [JsonPropertyName("speaker_uuid")]
    public string SpeakerUuid { get; init; }

    [JsonPropertyName("styles")]
    public VoiceStyle[] Styles { get; init; }
}