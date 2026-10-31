using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Beutl.Audio;
using Beutl.Editor.Observers;
using Beutl.Editor.Services;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.Graphics;
using Beutl.Graphics.Backend;
using Beutl.Graphics.Rendering;
using Beutl.Graphics.Transformation;
using Beutl.Media;
using Beutl.ProjectSystem;
using Beutl.Serialization;
using TextBlock = Beutl.Graphics.Shapes.TextBlock;

namespace Beutl.Extensions.Voice.Tests;

public class SubtitleTests
{
    private string _directory = null!;
    private string? _previousHome;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "voice-subtitles-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _previousHome = Environment.GetEnvironmentVariable("BEUTL_HOME");
        Environment.SetEnvironmentVariable("BEUTL_HOME", _directory);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("BEUTL_HOME", _previousHome);
        Directory.Delete(_directory, true);
    }

    private TestEditor Editor() => new(Path.Combine(_directory, "scene.bescene"));
    private static MediaTime At(double seconds) => MediaTime.FromSeconds(seconds);
    private static VoicePreset Preset() => new()
    {
        Name = "話者の字幕", Settings = new()
        {
            SpeakerUuid = "speaker", SpeakerName = "話者", StyleId = 1, StyleName = "通常", ModelVersion = "1"
        }
    };

    private static void Configure(TtsTabViewModel model)
    {
        var voice = new VoiceMetadata
        {
            Name = "話者", SpeakerUuid = "speaker", Version = "1", Styles = [new VoiceStyle { Id = 1, Name = "通常" }]
        };
        model.Voice.Value = [voice];
        model.SelectedVoice.Value = voice;
        model.Text.Value = "こんにちは。";
        model.IsEnabled.Value = true;
        model.IsVoiceVoxInstalled.Value = true;
    }

    private static Task<VoiceSynthesisResult> Synthesize(string text, VoiceSettings settings)
    {
        var query = new AudioQueryModel();
        settings.ApplyTo(query);
        return Task.FromResult(new VoiceSynthesisResult(HttpVoiceEngineTests.EngineServer.Wave(), query,
            JsonSerializer.Serialize(query), settings));
    }

    private static Element Template(Scene scene)
    {
        var element = new Element { Name = { Value = "話者名付き字幕" }, Start = { Value = At(30) }, Length = { Value = At(1) } };
        element.AddObject(new TextBlock
        {
            Text = { Value = "「{text}」" }, Size = { Value = 36 },
            Transform = { Value = new TranslateTransform { Y = { Value = 80 } } }
        });
        element.AddObject(new TextBlock { Text = { Value = "固定の話者名" }, Size = { Value = 20 } });
        element.AddObject(new SourceSound());
        scene.AddChild(element, ElementOverlapHandling.Allow);
        return element;
    }

    private static Element Caption(Scene scene) => scene.EnumerateAllChildren<SubtitleController>().Single().FindHierarchicalParent<Element>()!;
    private static TextBlock Body(Element caption)
    {
        var binding = caption.Objects.OfType<SubtitleController>().Single().TextBindings.Value.Single();
        return caption.EnumerateAllChildren<TextBlock>().Single(x => x.Id.Value == binding.TextId);
    }

    [AvaloniaTest]
    public async Task Preset_and_tab_restore_an_independent_template_and_generate_with_atomic_undo()
    {
        using var editor = Editor();
        editor.Scene.FrameSize.Value = new(640, 360);
        var template = Template(editor.Scene);
        using var model = new TtsTabViewModel(new(), editor, (text, settings, _) => Synthesize(text, settings));
        Configure(model);
        model.Subtitles.SelectedId = template.Id.Value;
        model.PresetName.Value = "字幕付き";
        model.AddPreset();
        var preset = VoicePresetStore.Load(VoicePresetStore.GlobalPath).Single();
        Assert.That(preset.Subtitle, Is.Not.Null, model.PresetMessage.Value);
        Assert.That(model.PresetDetails.Value, Does.Not.Contain("字幕の変更:"));
        template.Objects.OfType<TextBlock>().First().Size.Value = 90;
        var state = new JsonObject();
        model.WriteToJson(state);
        model.Subtitles.Template = null;
        model.ReadFromJson(state);
        Assert.That(model.Subtitles.Template, Is.EqualTo(preset.Subtitle));
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        editor.Time.Value = At(2);
        await model.Generate();
        var caption = Caption(editor.Scene);
        var speech = editor.Scene.EnumerateAllChildren<TtsController>().Single().FindHierarchicalParent<Element>()!;
        Assert.That(caption.Start.Value, Is.EqualTo(speech.Start.Value));
        Assert.That(caption.Length.Value, Is.EqualTo(speech.Length.Value));
        Assert.That(caption.ZIndex.Value, Is.GreaterThan(speech.ZIndex.Value));
        Assert.That(caption.EnumerateAllChildren<Sound>(), Is.Empty);
        Assert.That(Body(caption).Text.Value, Is.EqualTo("「こんにちは。」"));
        Assert.That(Body(caption).Size.Value, Is.EqualTo(36));
        Assert.That(((TranslateTransform)Body(caption).Transform.Value!).Y.Value, Is.EqualTo(80));
        Assert.That(caption.EnumerateAllChildren<TextBlock>().Select(x => x.Text.Value), Does.Contain("固定の話者名"));
        Assert.That(VoiceSubtitles.Capture(caption).ElementJson, Does.Contain("{text}"));
        Assert.That(template.Objects.OfType<TextBlock>().First().Text.Value, Is.EqualTo("「{text}」"));
        Assert.That(editor.History.UndoCount, Is.EqualTo(1));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Is.EqualTo(new[] { template }));
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(3));
    }

    [AvaloniaTest]
    public async Task Script_uses_each_speakers_template_on_one_free_unlocked_layer()
    {
        using var editor = Editor();
        editor.Scene.FrameSize.Value = new(640, 360);
        var first = Preset() with { Subtitle = VoiceSubtitles.Capture(Template(editor.Scene)) };
        var second = Preset() with { Subtitle = VoiceSubtitles.Default(editor.Scene) };
        editor.Scene.Layers.Add(new TimelineLayer { ZIndex = { Value = 2 }, IsLocked = { Value = true } });
        using var model = new ScriptImportViewModel(editor, Synthesize);
        model.RawText.Value = "案内役: こんにちは。\nゲスト: よろしく。\n案内役: 始めましょう。";
        model.ImportText();
        model.Speakers[0].SelectedPreset.Value = first;
        model.Speakers[1].SelectedPreset.Value = second;
        model.IsVoiceVoxInstalled.Value = true;
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        await model.Generate();
        var voices = editor.Scene.EnumerateAllChildren<TtsController>().ToArray();
        Assert.That(voices, Has.Length.EqualTo(3), model.Message.Value);
        var links = editor.Scene.EnumerateAllChildren<SubtitleController>().ToArray();
        Assert.That(links, Has.Length.EqualTo(3));
        foreach (var link in links)
        {
            var voice = voices.Single(x => x.Id.Value == link.SourceId.Value);
            var speech = voice.FindHierarchicalParent<Element>()!;
            var caption = link.FindHierarchicalParent<Element>()!;
            Assert.That(caption.Range, Is.EqualTo(speech.Range));
            Assert.That(editor.Scene.IsElementLocked(caption), Is.False);
            string text = voice.Generation.Value!.Text;
            Assert.That(Body(caption).Text.Value, Is.EqualTo(text == "よろしく。" ? text : $"「{text}」"));
        }
        Assert.That(links.Select(x => x.FindHierarchicalParent<Element>()!.ZIndex.Value).Distinct().Count(), Is.EqualTo(1));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(1));
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(7));
    }

    [AvaloniaTest]
    public async Task Regeneration_updates_only_caption_text_with_audio_and_preserves_it_on_failure()
    {
        using var server = new HttpVoiceEngineTests.EngineServer();
        using var editor = Editor();
        using var tab = new TtsTabViewModel(new(), editor);
        Assert.That(await tab.SaveConnection("Test", server.Url), Is.True);
        tab.SelectedVoice.Value = tab.Voice.Value.Single();
        tab.SelectedStyle.Value = tab.SelectedVoice.Value.Styles.First();
        tab.Text.Value = "最初のセリフ。";
        tab.Subtitles.SelectedId = Guid.Empty;
        await tab.Generate();
        var controller = editor.Scene.EnumerateAllChildren<TtsController>().Single();
        var sound = controller.FindHierarchicalParent<Element>()!.Objects.OfType<SourceSound>().Single();
        var caption = Caption(editor.Scene);
        var body = Body(caption);
        body.Size.Value = 57;
        caption.Start.Value = At(5);
        var previous = sound.Source.Value;
        using var model = TtsControllerTests.CreateModel(editor, controller);
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        controller.Text.Value = "更新したセリフ。";
        editor.History.Commit();
        await model.Regenerate();
        Assert.That(body.Text.Value, Is.EqualTo("更新したセリフ。"), model.Message.Value);
        Assert.That(body.Size.Value, Is.EqualTo(57));
        Assert.That(caption.Start.Value, Is.EqualTo(At(5)));
        Assert.That(sound.Source.Value, Is.Not.SameAs(previous));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(body.Text.Value, Is.EqualTo("最初のセリフ。"));
        Assert.That(sound.Source.Value!.Uri, Is.EqualTo(previous!.Uri));
        Assert.That(editor.History.Redo(), Is.True);
        previous = sound.Source.Value;
        server.FailSynthesis = true;
        controller.Text.Value = "合成に失敗するセリフ。";
        await model.Regenerate();
        Assert.That(body.Text.Value, Is.EqualTo("更新したセリフ。"));
        Assert.That(sound.Source.Value, Is.SameAs(previous));
        server.FailSynthesis = false;
        caption.IsLocked.Value = true;
        await model.Regenerate();
        Assert.That(model.Message.Value, Does.Contain("ロック"));
        Assert.That(sound.Source.Value, Is.SameAs(previous));
        controller.Text.Value = "更新したセリフ。";
        model.SpeedScale.Value = 1.2;
        await model.Regenerate();
        Assert.That(sound.Source.Value, Is.Not.SameAs(previous), model.Message.Value);
        Assert.That(body.Text.Value, Is.EqualTo("更新したセリフ。"));
        caption.IsLocked.Value = false;
        previous = sound.Source.Value;
        controller.Text.Value = "処理中に編集するセリフ。";
        server.PauseAt = "/tts/synthesis";
        var regenerate = model.Regenerate();
        await server.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        body.Text.Value = "字幕を手動で編集";
        server.Resume.TrySetResult();
        await regenerate;
        Assert.That(body.Text.Value, Is.EqualTo("字幕を手動で編集"));
        Assert.That(sound.Source.Value, Is.SameAs(previous));
        Assert.That(model.Message.Value, Does.Contain("生成中に字幕"));
    }

    [AvaloniaTest]
    public async Task Reload_and_pair_duplication_keep_links_independent_from_the_original()
    {
        using var editor = Editor();
        using var tab = new TtsTabViewModel(new(), editor, (text, settings, _) => Synthesize(text, settings));
        Configure(tab);
        tab.Subtitles.SelectedId = Guid.Empty;
        await tab.Generate();
        using var reopened = new TestEditor(Path.Combine(_directory, "reopened.bescene"));
        foreach (var element in editor.Scene.Children)
            reopened.Scene.AddChild((Element)CoreSerializer.DeserializeFromJsonObject(
                CoreSerializer.SerializeToJsonObject(element), typeof(Element)), ElementOverlapHandling.Allow);
        var original = reopened.Scene.EnumerateAllChildren<TtsController>().Single();
        var caption = Caption(reopened.Scene);
        var speech = original.FindHierarchicalParent<Element>()!;
        ObjectRegenerator.Regenerate(new[] { speech, caption }, out Element[] copies);
        foreach (var copy in copies) reopened.Scene.AddChild(copy, ElementOverlapHandling.Allow);
        var duplicate = copies[0].Objects.OfType<TtsController>().Single();
        var link = copies[1].Objects.OfType<SubtitleController>().Single();
        Assert.That(link.SourceId.Value, Is.EqualTo(duplicate.Id.Value));
        VoiceSubtitles.Update(duplicate, VoiceSubtitles.PrepareUpdate(duplicate, "複製の字幕"));
        Assert.That(Body(copies[1]).Text.Value, Is.EqualTo("複製の字幕"));
        Assert.That(Body(caption).Text.Value, Is.EqualTo("こんにちは。"));
        VoiceSubtitles.Update(original, VoiceSubtitles.PrepareUpdate(original, "元の字幕"));
        Assert.That(Body(caption).Text.Value, Is.EqualTo("元の字幕"));
        Assert.That(Body(copies[1]).Text.Value, Is.EqualTo("複製の字幕"));
        ObjectRegenerator.Regenerate(speech, out Element soundOnly);
        reopened.Scene.AddChild(soundOnly, ElementOverlapHandling.Allow);
        Assert.That(VoiceSubtitles.PrepareUpdate(soundOnly.Objects.OfType<TtsController>().Single(), "音声だけ複製"), Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(320)]
    [TestCase(460)]
    public async Task Subtitle_controls_remain_visible_at_narrow_widths(int width)
    {
        using var editor = Editor();
        Template(editor.Scene);
        using var model = new TtsTabViewModel(new(), editor);
        Configure(model);
        model.Subtitles.SelectedId = Guid.Empty;
        var view = new TtsTabView { DataContext = model };
        var window = new Window { Width = width, Height = 600, Content = view, RequestedThemeVariant = width == 320 ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show();
            var subtitles = view.FindControl<SubtitleSelectionView>("GenerationSubtitles")!;
            var expander = subtitles.FindControl<Expander>("SubtitleExpander")!;
            expander.IsExpanded = true;
            await Settle();
            expander.BringIntoView();
            await Settle();
            var selector = subtitles.FindControl<ComboBox>("SubtitleTemplateSelector")!;
            Assert.That(selector.Bounds.Width, Is.GreaterThan(150));
            var bottom = selector.TranslatePoint(new Avalonia.Point(selector.Bounds.Width, selector.Bounds.Height), window)!.Value;
            Assert.That(bottom.X, Is.LessThanOrEqualTo(window.ClientSize.Width));
            Assert.That(bottom.Y, Is.LessThanOrEqualTo(window.ClientSize.Height));
            Assert.That(model.Subtitles.Choices.Value.Select(x => x.Name), Does.Contain("話者名付き字幕"));
            if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is { } directory)
            {
                Directory.CreateDirectory(directory);
                using var frame = window.CaptureRenderedFrame()!;
                frame.Save(Path.Combine(directory, $"subtitle-controls-{width}.png"), new PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Standard_subtitle_renders_at_the_bottom_only_during_its_speech()
    {
        if (!RenderThread.Dispatcher.Invoke(() => GraphicsContextFactory.GetOrCreateShared() is not null))
            Assert.Ignore("No graphics context is available on this host.");
        using var editor = Editor();
        editor.Scene.FrameSize.Value = new(640, 360);
        using var tab = new TtsTabViewModel(new(), editor, (text, settings, _) => Synthesize(text, settings));
        Configure(tab);
        editor.Time.Value = At(2);
        tab.Text.Value = "字幕のテスト";
        tab.Subtitles.SelectedId = Guid.Empty;
        await tab.Generate();
        Assert.That(editor.Scene.EnumerateAllChildren<SubtitleController>().Count(), Is.EqualTo(1), tab.Message.Value);
        RenderThread.Dispatcher.Invoke(() =>
        {
            using var renderer = new SceneRenderer(editor.Scene, RenderIntent.Delivery);
            foreach (var seconds in new[] { 1.9, 2.1, 2.3 })
            {
                renderer.Render(renderer.Compositor.EvaluateGraphics(At(seconds)));
                using var snapshot = renderer.Snapshot();
                using var frame = snapshot.Convert(BitmapColorType.Bgra8888, BitmapAlphaType.Premul, BitmapColorSpace.Srgb);
                int count = 0, top = 360, bottom = 0;
                for (int y = 0; y < 360; y++)
                {
                    var row = frame.GetRow(y);
                    for (int x = 0; x < 640; x++)
                        if (row[x * 4 + 3] > 0) { count++; top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
                }
                if (seconds == 2.1)
                {
                    Assert.That(count, Is.GreaterThan(100));
                    Assert.That(top, Is.GreaterThan(180));
                    Assert.That(bottom, Is.LessThan(359));
                    if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is { } directory)
                    {
                        Directory.CreateDirectory(directory);
                        frame.Save(Path.Combine(directory, "standard-subtitle.png"), EncodedImageFormat.Png);
                    }
                }
                else Assert.That(count, Is.Zero, $"No subtitle should be visible at {seconds}s.");
            }
        });
    }

    private static async Task Settle()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(150);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
    }
}
