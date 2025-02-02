using Beutl.Extensibility;
using Reactive.Bindings;

namespace Beutl.ViewModels;

public class EditViewModel : IEditorContext
{
    public EditorExtension Extension => throw null!;

    public string EdittingFile => throw null!;

    public IReactiveProperty<bool> IsEnabled => throw null!;

    public IKnownEditorCommands? Commands => throw null!;

    public ReactivePropertySlim<TimeSpan> CurrentTime => throw null!;

    public void Dispose()
    {
        throw null!;
    }

    public object? GetService(Type serviceType)
    {
        throw null!;
    }

    public T? FindToolTab<T>(Func<T, bool> condition) where T : IToolContext
    {
        throw null!;
    }

    public T? FindToolTab<T>() where T : IToolContext
    {
        throw null!;
    }

    public bool OpenToolTab(IToolContext item)
    {
        throw null!;
    }

    public void CloseToolTab(IToolContext item)
    {
        throw null!;
    }
}