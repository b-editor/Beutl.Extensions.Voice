using System.ComponentModel.DataAnnotations;
using Beutl.Engine;
using Beutl.Extensions.Voice.Models;

namespace Beutl.Extensions.Voice.Operators;

public partial class TtsController : EngineObject
{
    public TtsController()
    {
        ScanProperties<TtsController>();
        HideProperties(Generation, AppliedPreset, Draft, LipSyncGroupId);
    }

    [DataType(DataType.MultilineText)]
    public IExpressionProperty<string> Text { get; } = Property.CreateExpression<string>("");

    public IProperty<VoiceGeneration?> Generation { get; } = Property.CreateValue<VoiceGeneration?>();

    public IProperty<VoicePreset?> AppliedPreset { get; } = Property.CreateValue<VoicePreset?>();

    // Pending edits use the same immutable snapshot format as a completed generation.
    public IProperty<VoiceGeneration?> Draft { get; } = Property.CreateValue<VoiceGeneration?>();

    // A shared binding survives splitting either the speech or its character element.
    public IProperty<Guid> LipSyncGroupId { get; } = Property.CreateValue(Guid.Empty);
}
