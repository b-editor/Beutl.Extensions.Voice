using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Beutl.Collections;
using Beutl.Editor;
using Beutl.Editor.Services;
using Beutl.Extensibility;
using Beutl.ProjectSystem;
using FluentAvalonia.Styling;
using Reactive.Bindings;

[assembly: AvaloniaTestApplication(typeof(Beutl.Extensions.Voice.Tests.TestApp))]
// ReactiveProperty keeps a UI scheduler; keep the dispatcher alive across test cases.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace Beutl.Extensions.Voice.Tests;

public class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentAvaloniaTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Beutl.Controls/"))
        {
            Source = new Uri("avares://Beutl.Controls/Styles.axaml")
        });
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal sealed class TestEditor : IEditorContext, IEditorClock, IEditorSelection
{
    public TestEditor(string scenePath)
    {
        Scene.Uri = new Uri(scenePath);
        History = new HistoryManager(Sequence);
    }

    public Scene Scene { get; } = new();
    public OperationSequenceGenerator Sequence { get; } = new();
    public HistoryManager History { get; }
    public CoreObject Object => Scene;
    public EditorExtension Extension => null!;
    public IReactiveProperty<bool> IsEnabled { get; } = new ReactiveProperty<bool>(true);
    public ReactiveProperty<MediaTime> Time { get; } = new(MediaTime.Zero);
    public IReadOnlyReactiveProperty<MediaTime> CurrentTime => Time;
    public IReadOnlyReactiveProperty<MediaTime> MaximumTime => Time;
    public ReactiveProperty<CoreObject?> Selection { get; } = new();
    public IReadOnlyReactiveProperty<CoreObject?> SelectedObject => Selection;
    public IReadOnlyReactiveProperty<int?> SelectedLayerNumber { get; } = new ReactiveProperty<int?>();
    public ICoreList<Element> SelectedElements { get; } = new CoreList<Element>();
    public void Select(CoreObject? obj, bool notifyIfSelected = false) => Selection.Value = obj;
    public void Seek(MediaTime time) => Time.Value = time;
    public T? FindToolTab<T>(Func<T, bool> condition) where T : IToolContext => default;
    public T? FindToolTab<T>() where T : IToolContext => default;
    public bool OpenToolTab(IToolContext item) => false;
    public void CloseToolTab(IToolContext item) { }
    public object? GetService(Type type) => type == typeof(Scene) ? Scene : type == typeof(HistoryManager) ? History
        : type == typeof(IEditorClock) || type == typeof(IEditorSelection) ? this : null;
    public void Dispose() => History.Dispose();
}
