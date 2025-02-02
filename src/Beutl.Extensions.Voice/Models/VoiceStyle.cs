using System.Text.Json.Serialization;

namespace Beutl.Extensions.Voice.Models;

public class VoiceStyle
{
    [JsonPropertyName("id")]
    public uint Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; }
}