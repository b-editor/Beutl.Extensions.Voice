using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Beutl.Extensions.Voice.Models;

public sealed record VoiceEngineConnection
{
    public static readonly VoiceEngineConnection Core = new() { Id = VoiceSettings.CoreEngineId, Name = "VOICEVOX Core" };

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string EngineUuid { get; init; } = "";
    public string Version { get; init; } = "";
    public VoiceEngineCapabilities Capabilities { get; init; } = new();
    public VoiceMetadata[] Voices { get; init; } = [];

    [JsonIgnore]
    public bool IsCore => Id == VoiceSettings.CoreEngineId;
}

public sealed record VoiceEngineCapabilities
{
    public const string AivisSpeechUuid = "1b4a5014-d9fd-11ee-b97d-83c170a68ed3";

    public bool AdjustSpeed { get; init; } = true;
    public bool AdjustPitch { get; init; } = true;
    public bool AdjustIntonation { get; init; } = true;
    public bool AdjustVolume { get; init; } = true;
    public bool EditAccent { get; init; } = true;
    public bool TempoDynamics { get; init; }

    [JsonIgnore]
    public string IntonationLabel => TempoDynamics ? "感情" : "抑揚";

    internal static VoiceEngineCapabilities FromManifest(JsonObject? manifest)
    {
        var features = manifest?["supported_features"] as JsonObject;
        bool aivis = string.Equals((string?)manifest?["uuid"], AivisSpeechUuid, StringComparison.OrdinalIgnoreCase);
        return new()
        {
            AdjustSpeed = Supports("adjust_speed_scale"), AdjustPitch = Supports("adjust_pitch_scale"),
            AdjustIntonation = Supports("adjust_intonation_scale"), AdjustVolume = Supports("adjust_volume_scale"),
            EditAccent = !aivis && Supports("adjust_mora_pitch"), TempoDynamics = aivis
        };

        // Older VOICEVOX-compatible engines predate engine_manifest; their standard controls still work.
        bool Supports(string name) => features?[name] is JsonValue value && value.TryGetValue<bool>(out bool supported)
            ? supported : features?[name] is JsonObject feature ? (bool?)feature["value"] ?? true : true;
    }
}
