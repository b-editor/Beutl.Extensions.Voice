namespace Beutl.Extensions.Voice.Models;

public sealed record VoicePreset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public VoiceSettings Settings { get; init; } = new();
}

public sealed record VoiceGeneration
{
    public string Text { get; init; } = "";
    public VoiceSettings Settings { get; init; } = new();
    public Guid? PresetId { get; init; }
    public string? PresetName { get; init; }
    public string AudioQueryJson { get; init; } = "";
}
