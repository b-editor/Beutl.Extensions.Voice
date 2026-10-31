namespace Beutl.Extensions.Voice.Models;

// A snapshot keeps generated captions independent of later edits to the template or voice preset.
public sealed record VoiceSubtitleTemplate(string Name, string ElementJson);

public sealed record SubtitleTextBinding(Guid TextId, string Pattern);
