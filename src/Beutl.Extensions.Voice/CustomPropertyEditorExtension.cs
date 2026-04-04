using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.PropertyAdapters;

namespace Beutl.Extensions.Voice;

[Export]
public class CustomPropertyEditorExtension : PropertyEditorExtension
{
    public override IEnumerable<IPropertyAdapter> MatchProperty(IReadOnlyList<IPropertyAdapter> properties)
    {
        return properties.Where(i =>
            i is EnginePropertyAdapter<string> { Object: TtsController, Property.Name: nameof(TtsController.Text) });
    }

    public override bool TryCreateContext(
        IReadOnlyList<IPropertyAdapter> properties,
        [NotNullWhen(true)] out IPropertyEditorContext? context)
    {
        if (base.TryCreateContext(properties, out var innerContext))
        {
            context = new TtsControllerViewModel(innerContext, this);
            return true;
        }
        else
        {
            context = null;
            return false;
        }
    }

    public override bool TryCreateControl(IPropertyEditorContext context, [NotNullWhen(true)] out Control? control)
    {
        if (context is TtsControllerViewModel ttsControllerViewModel &&
            base.TryCreateControl(ttsControllerViewModel.Inner, out var innerControl))
        {
            control = new TtsControllerView
            {
                Inner =
                {
                    Content = innerControl
                }
            };
            return true;
        }
        else
        {
            control = null;
            return false;
        }
    }
}