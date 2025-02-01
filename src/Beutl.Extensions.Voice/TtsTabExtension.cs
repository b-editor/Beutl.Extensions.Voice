using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using FluentAvalonia.UI.Controls;

namespace Beutl.Extensions.Voice;

[Export]
public class TtsTabExtension : ToolTabExtension
{
    public override string Name => "Text-to-Speech";

    public override string DisplayName => "テキスト読み上げ";
    
    public override string Header => "テキスト読み上げ";

    public override bool CanMultiple => true;

    public override IconSource GetIcon()
    {
        return new SymbolIconSource
        {
            Symbol = Symbol.Speaker2
        };
    }

    public override bool TryCreateContent(IEditorContext editorContext, [NotNullWhen(true)] out Control? control)
    {
        control = new TtsTabView();
        return true;
    }

    public override bool TryCreateContext(IEditorContext editorContext, [NotNullWhen(true)] out IToolContext? context)
    {
        context = new TtsTabViewModel(this);
        return true;
    }

}