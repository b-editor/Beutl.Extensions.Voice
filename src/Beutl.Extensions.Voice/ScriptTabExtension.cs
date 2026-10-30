using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;

namespace Beutl.Extensions.Voice;

[Export]
public sealed class ScriptTabExtension : ToolTabExtension
{
    public override string Name => "Script-to-Speech";
    public override string DisplayName => "台本読み込み";
    public override string Header => DisplayName;
    public override bool CanMultiple => true;

    public override bool TryCreateContent(IEditorContext editorContext, [NotNullWhen(true)] out Control? control)
    {
        control = new ScriptImportView();
        return true;
    }

    public override bool TryCreateContext(IEditorContext editorContext, [NotNullWhen(true)] out IToolContext? context)
    {
        context = new ScriptImportViewModel(this, editorContext);
        return true;
    }
}
