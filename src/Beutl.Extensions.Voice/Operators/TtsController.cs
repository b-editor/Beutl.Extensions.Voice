using System.ComponentModel.DataAnnotations;
using Beutl.Engine;

namespace Beutl.Extensions.Voice.Operators;

public partial class TtsController : EngineObject
{
    public TtsController()
    {
        ScanProperties<TtsController>();
    }

    [DataType(DataType.MultilineText)]
    public IProperty<string> Text { get; } = Property.Create<string>("");
}
