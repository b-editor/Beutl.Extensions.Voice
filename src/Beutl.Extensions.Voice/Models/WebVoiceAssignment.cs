namespace Beutl.Extensions.Voice.Models;

// The character and caption used for speech that a site's voice made, remembered across projects.
// A voice the page could not name is stored with an empty VoiceId.
public sealed record WebVoiceAssignment
{
    public string SiteId { get; init; } = "";
    public string VoiceId { get; init; } = "";
    public string VoiceName { get; init; } = "";
    public VoiceLipSyncPreset? LipSync { get; init; }
    public VoiceSubtitleTemplate? Subtitle { get; init; }

    public bool IsFor(string siteId, string voiceId) => SiteId == siteId && VoiceId == voiceId;
}
