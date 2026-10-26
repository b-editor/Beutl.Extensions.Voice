using Beutl.Extensibility;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.PropertyAdapters;

namespace Beutl.Extensions.Voice;

[Export]
public class CustomPropertyEditorExtension : PropertyEditorExtension
{
    protected override void RegisterEditors(PropertyEditorRegistry registry)
    {
        registry.Register<string, TtsControllerViewModel>(
            property => registry.TryCreateDefault(property, out var innerContext)
                ? new TtsControllerViewModel(innerContext, this)
                : null!,
            context => registry.TryCreateControl(context.Inner, out var innerControl)
                ? new TtsControllerView
                {
                    Inner =
                    {
                        Content = innerControl
                    }
                }
                : null!,
            new PropertyEditorOptions
            {
                Match = property => property is EnginePropertyAdapter<string>
                    { Object: TtsController, Property.Name: nameof(TtsController.Text) }
            });
    }
}
