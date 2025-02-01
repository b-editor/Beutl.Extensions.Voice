using System.Text.Json.Nodes;
using Beutl.Extensibility;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public class TtsTabViewModel : IToolContext
{
    public TtsTabViewModel(TtsTabExtension extension)
    {
        Extension = extension;
    }

    public ToolTabExtension Extension { get; }

    public IReactiveProperty<bool> IsSelected { get; } = new ReactiveProperty<bool>();

    public IReactiveProperty<ToolTabExtension.TabPlacement> Placement { get; } =
        new ReactiveProperty<ToolTabExtension.TabPlacement>(ToolTabExtension.TabPlacement.RightLowerTop);

    public IReactiveProperty<ToolTabExtension.TabDisplayMode> DisplayMode { get; }
        = new ReactiveProperty<ToolTabExtension.TabDisplayMode>(ToolTabExtension.TabDisplayMode.Docked);

    public string Header { get; } = "テキスト読み上げ";

    public void Dispose()
    {
    }

    public void WriteToJson(JsonObject json)
    {
    }

    public void ReadFromJson(JsonObject json)
    {
    }

    public object? GetService(Type serviceType)
    {
        return null;
    }
}