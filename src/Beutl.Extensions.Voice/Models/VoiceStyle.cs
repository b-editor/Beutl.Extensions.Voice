using System.Text.Json.Serialization;

namespace Beutl.Extensions.Voice.Models;

public class VoiceStyle
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}
