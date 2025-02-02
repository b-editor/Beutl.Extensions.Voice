using System.Text.Json.Nodes;
using Beutl.Extensibility;

namespace Beutl.Extensions.Voice.ViewModels;

public class TtsControllerViewModel : IPropertyEditorContext
{
    public TtsControllerViewModel(IPropertyEditorContext inner, PropertyEditorExtension extension)
    {
        Inner = inner;
        Extension = extension;
    }

    public IPropertyEditorContext Inner { get; }

    public PropertyEditorExtension Extension { get; }
    
    public void Dispose()
    {
        Inner.Dispose();
    }

    public void WriteToJson(JsonObject json)
    {
    }

    public void ReadFromJson(JsonObject json)
    {
    }

    public void Accept(IPropertyEditorContextVisitor visitor)
    {
        Inner.Accept(visitor);
    }
}