using VoicevoxCoreSharp.Core;

namespace Beutl.Extensions.Voice.Models;

public record VoiceSet(VoiceModelFile Model, VoiceMetadata[] Metadata);

public record VoiceFlattenSet(VoiceModelFile Model, VoiceMetadata Metadata);