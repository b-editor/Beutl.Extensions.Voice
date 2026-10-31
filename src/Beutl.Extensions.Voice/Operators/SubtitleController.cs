using System.ComponentModel.DataAnnotations;
using Beutl.Engine;
using Beutl.Extensions.Voice.Models;

namespace Beutl.Extensions.Voice.Operators;

[Display(Name = "字幕連携")]
public sealed class SubtitleController : EngineObject
{
    public SubtitleController()
    {
        ScanProperties<SubtitleController>();
        HideProperties(SourceId, TextBindings);
    }

    // ObjectRegenerator remaps the link when speech and captions are copied together.
    public IProperty<Guid> SourceId { get; } = Property.CreateValue(Guid.Empty);
    public IProperty<SubtitleTextBinding[]> TextBindings { get; } = Property.CreateValue<SubtitleTextBinding[]>([]);
}
