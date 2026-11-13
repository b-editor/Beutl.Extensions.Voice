using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Beutl.Collections;
using Beutl.Editor;
using Beutl.Editor.Services;
using Beutl.Extensibility;
using Beutl.Media.Decoding;
using Beutl.Media.Wave;
using Beutl.ProjectSystem;
using Beutl.Serialization;
using FluentAvalonia.Styling;
using Microsoft.Extensions.Logging.Abstractions;
using Reactive.Bindings;

[assembly: AvaloniaTestApplication(typeof(Beutl.Extensions.Voice.Tests.TestApp))]
// ReactiveProperty keeps a UI scheduler; keep the dispatcher alive across test cases.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace Beutl.Extensions.Voice.Tests;

public class TestApp : Application
{
    public override void Initialize()
    {
        DecoderRegistry.Register(new WaveDecoderInfo());
        // The desktop host registers bundled engine fonts and fallbacks during startup.
        // Reproduce the Japanese fallback when rendering captions in this headless host.
        // The Beutl.Controls package no longer embeds the UI fonts, so this subset holds the glyphs the caption test draws;
        // see TestData/NotoSansJP.LICENSE.
        using (var font = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", "NotoSansJP-Regular.subset.ttf")))
            Beutl.Media.FontManager.Instance.AddFont(font);
        typeof(Beutl.Media.FontManager).GetMethod("SetFallbackFonts",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(Beutl.Media.FontManager.Instance, [new[] { new Beutl.Media.FontFamily("Noto Sans JP") }, null]);
        Styles.Add(new FluentAvaloniaTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Beutl.Controls/"))
        {
            Source = new Uri("avares://Beutl.Controls/Styles.axaml")
        });
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal static class TestTemplates
{
    // A template as the library loads it from its file, with paths relative to the file as a packaged template has them.
    public static ObjectTemplateItem Save(string directory, ICoreSerializable source, string name)
    {
        var item = ObjectTemplateItem.CreateFromInstance(source, name);
        string path = Path.Combine(directory, name + ".json");
        var json = ObjectTemplateItem.ToJson(item);
        json[nameof(ObjectTemplateItem.Json)] = CoreSerializer.SerializeToJsonObject(source,
            new CoreSerializerOptions { BaseUri = new Uri(path) });
        File.WriteAllText(path, json.ToJsonString());
        return ObjectTemplateItem.FromJson(JsonNode.Parse(File.ReadAllText(path))!, name, path, NullLogger.Instance)!;
    }
}

internal sealed class TestEditor : IEditorContext, IEditorClock, IEditorSelection
{
    private bool _disposed;

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
    // The tool tabs open in this editor.
    public List<IToolContext> ToolTabs { get; } = [];
    public T? FindToolTab<T>(Func<T, bool> condition) where T : IToolContext => ToolTabs.OfType<T>().FirstOrDefault(condition);
    public T? FindToolTab<T>() where T : IToolContext => ToolTabs.OfType<T>().FirstOrDefault();
    public bool OpenToolTab(IToolContext item) => false;
    public void CloseToolTab(IToolContext item) { }
    public object? GetService(Type type) => _disposed ? null : type == typeof(Scene) ? Scene : type == typeof(HistoryManager) ? History
        : type == typeof(IEditorClock) || type == typeof(IEditorSelection) ? this : null;
    public void Dispose()
    {
        _disposed = true;
        History.Dispose();
    }
}
