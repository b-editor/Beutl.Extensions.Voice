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
using Beutl.Engine.Expressions;
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
using Beutl.Media.Source;
using Beutl.ProjectSystem;
using Beutl.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
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
        element.AddObject(new Beutl.Graphics.Shapes.RectShape { Width = { Value = 320 }, Height = { Value = 80 } });
        element.AddObject(new SourceImage());
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

    private ObjectTemplateItem SavedTemplate(ICoreSerializable source, string name)
    {
        var item = ObjectTemplateItem.CreateFromInstance(source, name);
        string path = Path.Combine(_directory, name + ".json");
        var json = ObjectTemplateItem.ToJson(item);
        json[nameof(ObjectTemplateItem.Json)] = CoreSerializer.SerializeToJsonObject(source,
            new CoreSerializerOptions { BaseUri = new Uri(path) });
        File.WriteAllText(path, json.ToJsonString());
        return ObjectTemplateItem.FromJson(JsonNode.Parse(File.ReadAllText(path))!, name, path, NullLogger.Instance)!;
    }

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
        string imagePath = Path.Combine(_directory, "caption-image.png");
        using (var image = new Beutl.Media.Bitmap(2, 2)) image.Save(imagePath, EncodedImageFormat.Png);
        var source = new ImageSource();
        source.ReadFrom(new Uri(imagePath));
        template.Objects.OfType<SourceImage>().Single().Source.Value = source;
        var saved = SavedTemplate(template, "話者名付き字幕");
        Assert.That(saved.Json["Objects"]!.AsArray().OfType<JsonObject>().Single(x => x["Source"]?.ToString() == "caption-image.png"), Is.Not.Null);
        using var model = new TtsTabViewModel(new(), editor, (text, settings, _) => Synthesize(text, settings), () => [saved]);
        Configure(model);
        model.Subtitles.SelectedId = saved.Id;
        var libraryState = new JsonObject();
        model.WriteToJson(libraryState);
        model.Subtitles.SelectedId = null;
        model.ReadFromJson(libraryState);
        Assert.That(model.Subtitles.SelectedId, Is.EqualTo(saved.Id));
        Assert.That(libraryState["SubtitleTemplateId"]!.GetValue<string>(), Is.EqualTo(saved.Id.ToString()));
        Assert.That(libraryState.ContainsKey("SubtitleElementId"), Is.False);
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
        Assert.That(caption.Objects.OfType<Beutl.Graphics.Shapes.RectShape>().Single().Width.Value, Is.EqualTo(320));
        Assert.That(caption.Objects.OfType<SourceImage>().Single().Source.Value!.Uri, Is.EqualTo(new Uri(imagePath)));
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
        using var other = new TestEditor(Path.Combine(_directory, "other-project", "scene.bescene"));
        using var otherTab = new TtsTabViewModel(new(), other, (text, settings, _) => Synthesize(text, settings));
        Configure(otherTab);
        otherTab.Subtitles.Template = preset.Subtitle;
        await otherTab.Generate();
        Assert.That(Caption(other.Scene).Objects.OfType<SourceImage>().Single().Source.Value!.Uri,
            Is.EqualTo(new Uri(imagePath)), otherTab.Message.Value);
    }

    [AvaloniaTest]
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task Invalid_templates_fail_before_synthesis_and_can_be_replaced(bool script, bool missingText)
    {
        using var editor = Editor();
        int calls = 0;
        Task<VoiceSynthesisResult> Generate(string text, VoiceSettings settings)
        {
            calls++;
            return Synthesize(text, settings);
        }
        var invalid = new VoiceSubtitleTemplate("無効な字幕", missingText
            ? CoreSerializer.SerializeToJsonString(new Element()) : "{invalid");
        var valid = VoiceSubtitles.Default(editor.Scene);
        using var tab = new TtsTabViewModel(new(), editor, (text, settings, _) => Generate(text, settings));
        Configure(tab);
        tab.Subtitles.Template = invalid;
        using var batch = new ScriptImportViewModel(editor, Generate);
        batch.RawText.Value = "案内役: 最初のセリフ。\nゲスト: 最後のセリフ。";
        batch.ImportText();
        batch.Speakers[0].SelectedPreset.Value = Preset() with { Subtitle = valid };
        batch.Speakers[1].SelectedPreset.Value = Preset() with { Subtitle = invalid };
        batch.IsVoiceVoxInstalled.Value = true;
        await Run();
        Assert.That(calls, Is.Zero, "Every assigned template must load before the first synthesis request.");
        Assert.That(editor.Scene.Children, Is.Empty);
        Assert.That(Directory.GetFiles(_directory, "*.wav", SearchOption.AllDirectories), Is.Empty);
        tab.Subtitles.Template = valid;
        batch.Speakers[1].SelectedPreset.Value = Preset() with { Subtitle = valid };
        await Run();
        Assert.That(calls, Is.EqualTo(script ? 2 : 1), script ? batch.Message.Value : tab.Message.Value);
        Assert.That(editor.Scene.EnumerateAllChildren<SubtitleController>().Count(), Is.EqualTo(calls));

        Task Run() => script ? batch.Generate() : tab.Generate();
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Expression_controlled_captions_do_not_silently_keep_old_text_on_regeneration(bool duringSynthesis)
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
        var expression = new StringExpression<string?>("\"式による字幕\"");
        var previous = sound.Source.Value;
        int calls = server.SynthesisCount;
        using var model = TtsControllerTests.CreateModel(editor, controller);
        controller.Text.Value = "更新したセリフ。";
        if (duringSynthesis)
        {
            server.PauseAt = "/tts/synthesis";
            var regenerate = model.Regenerate();
            await server.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            body.Text.Expression = expression;
            server.Resume.TrySetResult();
            await regenerate;
            server.PauseAt = null;
        }
        else
        {
            body.Text.Expression = expression;
            await model.Regenerate();
        }
        Assert.That(body.Text.Evaluate(new EvaluationContext(At(0))), Is.EqualTo("式による字幕"));
        Assert.That(sound.Source.Value, Is.SameAs(previous), "Audio must not change while its linked subtitle cannot display the new text.");
        Assert.That(body.Text.Value, Is.EqualTo("最初のセリフ。"));
        Assert.That(body.Text.Expression, Is.SameAs(expression));
        Assert.That(server.SynthesisCount, Is.EqualTo(calls + (duringSynthesis ? 1 : 0)));
        Assert.That(model.Message.Value, Does.Contain("式"));

        // Tuning does not rewrite the caption, so an expression is still allowed.
        controller.Text.Value = "最初のセリフ。";
        model.SpeedScale.Value = 1.2;
        await model.Regenerate();
        Assert.That(sound.Source.Value, Is.Not.SameAs(previous), model.Message.Value);
        Assert.That(body.Text.Expression, Is.SameAs(expression));
        previous = sound.Source.Value;
        var link = caption.Objects.OfType<SubtitleController>().Single();
        link.IsEnabled.Value = false;
        controller.Text.Value = "更新したセリフ。";
        await model.Regenerate();
        Assert.That(sound.Source.Value, Is.Not.SameAs(previous), model.Message.Value);
        Assert.That(body.Text.Expression, Is.SameAs(expression));
        link.IsEnabled.Value = true;
        body.Text.Expression = null;
        await model.Regenerate();
        Assert.That(body.Text.Value, Is.EqualTo(controller.Text.Value), model.Message.Value);
    }

    [AvaloniaTest]
    [TestCase(false, true, false)]
    [TestCase(true, true, false)]
    [TestCase(true, false, false)]
    [TestCase(false, true, true)]
    [TestCase(false, false, true)]
    public async Task Script_keeps_subtitles_below_their_audio_layers(bool separateLayers, bool guestSubtitle, bool locked)
    {
        using var editor = Editor();
        editor.Scene.FrameSize.Value = new(640, 360);
        var first = Preset() with { Subtitle = VoiceSubtitles.Capture(Template(editor.Scene)) };
        var second = Preset() with { Subtitle = guestSubtitle ? VoiceSubtitles.Default(editor.Scene) : null };
        if (locked) editor.Scene.Layers.Add(new TimelineLayer { ZIndex = { Value = 0 }, IsLocked = { Value = true } });
        using var model = new ScriptImportViewModel(editor, Synthesize);
        model.FirstLayer.Value = 0;
        model.SeparateLayers.Value = separateLayers;
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
        int captionCount = guestSubtitle ? 3 : 2;
        Assert.That(links, Has.Length.EqualTo(captionCount));
        foreach (var link in links)
        {
            var voice = voices.Single(x => x.Id.Value == link.SourceId.Value);
            var speech = voice.FindHierarchicalParent<Element>()!;
            var caption = link.FindHierarchicalParent<Element>()!;
            Assert.That(caption.Range, Is.EqualTo(speech.Range));
            Assert.That(editor.Scene.IsElementLocked(caption), Is.False);
            Assert.That(editor.Scene.IsElementLocked(speech), Is.False);
            Assert.That(caption.ZIndex.Value, Is.EqualTo(speech.ZIndex.Value + 1));
            string text = voice.Generation.Value!.Text;
            Assert.That(Body(caption).Text.Value, Is.EqualTo(text == "よろしく。" ? text : $"「{text}」"));
        }
        Assert.That(links.Select(x => x.FindHierarchicalParent<Element>()!.ZIndex.Value).Distinct().Count(),
            Is.EqualTo(separateLayers && guestSubtitle ? 2 : 1));
        if (separateLayers)
            Assert.That(voices.Select(x => x.FindHierarchicalParent<Element>()!.ZIndex.Value), Is.EqualTo(new[] { 0, 2, 0 }));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(1));
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(4 + captionCount));
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
    [TestCase("copy")]
    [TestCase("stale-copy")]
    [TestCase("enable")]
    [TestCase("remove")]
    [TestCase("unlink")]
    [TestCase("unrelated")]
    [TestCase("tuning")]
    public async Task Regeneration_checks_new_caption_targets_without_blocking_unaffected_edits(string change)
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
        var speech = controller.FindHierarchicalParent<Element>()!;
        var sound = speech.Objects.OfType<SourceSound>().Single();
        var caption = Caption(editor.Scene);
        var link = caption.Objects.OfType<SubtitleController>().Single();
        var previous = sound.Source.Value;
        ObjectRegenerator.Regenerate(caption, out Element copy);
        Assert.That(copy.Objects.OfType<SubtitleController>().Single().SourceId.Value, Is.EqualTo(controller.Id.Value));
        using var model = TtsControllerTests.CreateModel(editor, controller);
        controller.Text.Value = change == "tuning" ? tab.Text.Value : "更新したセリフ。";
        model.SpeedScale.Value = 1.2;
        if (change == "stale-copy") Body(caption).Text.Value = controller.Text.Value;
        if (change == "enable") link.IsEnabled.Value = false;
        server.PauseAt = "/tts/synthesis";
        var regenerate = model.Regenerate();
        await server.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        switch (change)
        {
            case "copy":
            case "stale-copy":
            case "tuning":
                editor.Scene.AddChild(copy, ElementOverlapHandling.Allow);
                break;
            case "enable":
                link.IsEnabled.Value = true;
                break;
            case "remove":
                editor.Scene.RemoveChild(caption);
                break;
            case "unlink":
                link.IsEnabled.Value = false;
                break;
            case "unrelated":
                ObjectRegenerator.Regenerate(new[] { speech, caption }, out Element[] pair);
                foreach (var element in pair) editor.Scene.AddChild(element, ElementOverlapHandling.Allow);
                copy = pair[1];
                break;
        }
        server.Resume.TrySetResult();
        await regenerate;
        server.PauseAt = null;
        if (change is "copy" or "stale-copy" or "enable")
        {
            Assert.That(sound.Source.Value, Is.SameAs(previous), model.Message.Value);
            Assert.That(Body(caption).Text.Value, Is.EqualTo(change == "stale-copy" ? controller.Text.Value : tab.Text.Value));
            Assert.That(Body(copy).Text.Value, Is.EqualTo(tab.Text.Value));
            Assert.That(model.Message.Value, Does.Contain("生成中").And.Contain("字幕"));
            Assert.That(Directory.GetFiles(_directory, "*.wav", SearchOption.AllDirectories), Has.Length.EqualTo(1));
            await model.Regenerate();
        }
        Assert.That(sound.Source.Value, Is.Not.SameAs(previous), model.Message.Value);
        foreach (var target in editor.Scene.EnumerateAllChildren<SubtitleController>()
                     .Where(x => x.IsEnabled.Value && x.SourceId.Value == controller.Id.Value))
            Assert.That(Body(target.FindHierarchicalParent<Element>()!).Text.Value, Is.EqualTo(controller.Text.Value));
        if (change == "unlink") Assert.That(Body(caption).Text.Value, Is.EqualTo(tab.Text.Value));
        if (change == "unrelated") Assert.That(Body(copy).Text.Value, Is.EqualTo(tab.Text.Value));
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
        Assert.That(VoiceSubtitles.PrepareUpdate(soundOnly.Objects.OfType<TtsController>().Single(), "音声だけ複製").Targets, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(320)]
    [TestCase(460)]
    public async Task Subtitle_controls_remain_visible_at_narrow_widths(int width)
    {
        using var editor = Editor();
        var savedTemplates = new List<ObjectTemplateItem>();
        using var model = new TtsTabViewModel(new(), editor, (text, settings, _) => Synthesize(text, settings), () => savedTemplates);
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
            // Templates saved after opening the tab appear on the next dropdown open.
            var template = Template(editor.Scene);
            var text = template.Objects.OfType<TextBlock>().First();
            template.Objects.Remove(text);
            var group = new DrawableGroup();
            group.Children.Add(text);
            template.AddObject(group);
            var plain = new Element { Name = { Value = "テキスト要素" } };
            plain.AddObject(new TextBlock { Text = { Value = "字幕に使うテキスト" } });
            editor.Scene.AddChild(plain, ElementOverlapHandling.Allow);
            var background = new Element { Name = { Value = "背景のみ" } };
            background.AddObject(new Beutl.Graphics.Shapes.RectShape());
            editor.Scene.AddChild(background, ElementOverlapHandling.Allow);
            var savedElement = SavedTemplate(template, "話者名付き字幕");
            var savedText = SavedTemplate(plain.Objects.OfType<TextBlock>().Single(), "保存したテキスト");
            var savedGroup = SavedTemplate(group, "保存したグループ");
            var savedBackground = SavedTemplate(background, "背景だけの要素");
            var savedShape = SavedTemplate(background.Objects.Single(), "背景だけの図形");
            var ambiguous = new Element();
            ambiguous.AddObject(new TextBlock { Text = { Value = "本文" } });
            ambiguous.AddObject(new TextBlock { Text = { Value = "話者名" } });
            var savedAmbiguous = SavedTemplate(ambiguous, "本文の指定がない字幕");
            var generatedBody = ambiguous.Objects.OfType<TextBlock>().First();
            ambiguous.AddObject(new SubtitleController
            {
                TextBindings = { Value = [new(generatedBody.Id.Value, "「{text}」")] }
            });
            var savedCaption = SavedTemplate(ambiguous, "生成した字幕");
            savedTemplates.AddRange([savedElement, savedText, savedGroup, savedBackground, savedShape, savedAmbiguous, savedCaption]);
            selector.IsDropDownOpen = true;
            await Settle();
            Assert.That(selector.Items.OfType<SubtitleChoice>().Where(x => x.Id != null && x.Id != Guid.Empty).Select(x => x.Id),
                Is.EquivalentTo(new Guid?[] { savedElement.Id, savedText.Id, savedGroup.Id, savedCaption.Id }),
                "Only usable saved templates are offered, including generated captions whose saved bindings identify the text.");
            selector.SelectedItem = selector.Items.OfType<SubtitleChoice>().Single(x => x.Id == savedCaption.Id);
            Assert.That(model.Subtitles.CaptureTemplate()!.ElementJson, Does.Contain("{text}"));
            selector.SelectedItem = selector.Items.OfType<SubtitleChoice>().Single(x => x.Id == savedText.Id);
            var textTemplate = model.Subtitles.CaptureTemplate()!;
            Assert.That(textTemplate.Name, Is.EqualTo(savedText.Name.Value));
            var textElement = (Element)CoreSerializer.DeserializeFromJsonObject(JsonNode.Parse(textTemplate.ElementJson)!.AsObject(), typeof(Element));
            Assert.That(textElement.EnumerateAllChildren<TextBlock>().Single().Text.Value, Is.EqualTo("字幕に使うテキスト"));
            selector.SelectedItem = selector.Items.OfType<SubtitleChoice>().Single(x => x.Id == savedElement.Id);
            selector.IsDropDownOpen = false;
            await Settle();
            Assert.That(model.Subtitles.SelectedId, Is.EqualTo(savedElement.Id));
            Assert.That(model.Subtitles.CaptureTemplate(), Is.Not.Null);
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
            selector.IsDropDownOpen = true;
            await Settle();
            Assert.That(((SubtitleChoice)selector.SelectedItem!).Id, Is.EqualTo(savedElement.Id));
            selector.IsDropDownOpen = false;
            var state = new JsonObject();
            model.WriteToJson(state);
            // An old scene-element selection must not be reintroduced as a library choice.
            state.Remove("SubtitleTemplateId");
            state["SubtitleElementId"] = template.Id.Value.ToString();
            model.ReadFromJson(state);
            Assert.That(model.Subtitles.SelectedId, Is.Null);
            Assert.That(model.Subtitles.Choices.Value.Select(x => x.Id), Does.Not.Contain(template.Id.Value));
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
