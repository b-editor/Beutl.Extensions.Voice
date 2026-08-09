using System.Text.Json.Serialization;

namespace Beutl.Extensions.Voice.Models;

public class VoiceMetadata
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("speaker_uuid")]
    public required string SpeakerUuid { get; init; }

    [JsonPropertyName("styles")]
    public required VoiceStyle[] Styles { get; init; }
}
