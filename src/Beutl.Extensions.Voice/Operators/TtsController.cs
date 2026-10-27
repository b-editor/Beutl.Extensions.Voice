using System.ComponentModel.DataAnnotations;
using Beutl.Engine;
using Beutl.Extensions.Voice.Models;

namespace Beutl.Extensions.Voice.Operators;

public partial class TtsController : EngineObject
{
    public TtsController()
    {
        ScanProperties<TtsController>();
        HideProperties(Generation);
    }

    [DataType(DataType.MultilineText)]
    public IExpressionProperty<string> Text { get; } = Property.CreateExpression<string>("");

    public IProperty<VoiceGeneration?> Generation { get; } = Property.CreateValue<VoiceGeneration?>();
}
