using VoicevoxCoreSharp.Core;

namespace Beutl.Extensions.Voice.Models;

public record VoiceSet(VoiceModel Model, VoiceMetadata[] Metadata);

public record VoiceFlattenSet(VoiceModel Model, VoiceMetadata Metadata);