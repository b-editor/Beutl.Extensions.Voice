using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Beutl.Editor.Observers;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.ProjectSystem;

namespace Beutl.Extensions.Voice.Tests;

public partial class PsdLipSyncTests
{
    private static VoicePreset GenerationPreset() => new()
    {
        Name = "読み上げの声", Settings = new()
        {
            SpeakerUuid = "speaker", SpeakerName = "話者", StyleId = 1, StyleName = "通常", ModelVersion = "1"
        }
    };

    private static void ConfigureSpeech(TtsTabViewModel model, VoicePreset preset)
    {
        var voice = new VoiceMetadata
        {
            Name = preset.Settings.SpeakerName, SpeakerUuid = preset.Settings.SpeakerUuid, Version = "1",
            Styles = [new VoiceStyle { Id = 1, Name = "通常" }]
        };
        model.Voice.Value = [voice];
        model.SelectedVoice.Value = voice;
        model.Text.Value = "こんにちは。";
        model.IsEnabled.Value = true;
        model.IsVoiceVoxInstalled.Value = true;
    }

    private Task<VoiceSynthesisResult> Synthesize(string text, VoiceSettings settings)
    {
        var query = new AudioQueryModel();
        settings.ApplyTo(query);
        return Task.FromResult(new VoiceSynthesisResult(File.ReadAllBytes(Wave().Uri.LocalPath), query,
            JsonSerializer.Serialize(query), settings));
    }

    private static void ConfigureScript(ScriptImportViewModel model, VoicePreset preset)
    {
        model.RawText.Value = "案内役: こんにちは。\nゲスト: よろしくお願いします。\n案内役: 始めましょう。";
        model.ImportText();
        foreach (var speaker in model.Speakers) speaker.SelectedPreset.Value = preset;
        model.StartSeconds.Value = 1;
        model.GapSeconds.Value = 0.5;
        model.IsVoiceVoxInstalled.Value = true;
    }

    [AvaloniaTest]
    public async Task Speech_generation_links_each_new_clip_and_undo_restores_the_character_inputs()
    {
        using var editor = Editor();
        var target = Character(editor.Scene);
        target.LipSyncOffset.Value = At(0.35);
        using var model = new TtsTabViewModel(new(), editor, (text, settings, _) => Synthesize(text, settings));
        ConfigureSpeech(model, GenerationPreset());
        model.LipSync.SelectedId = target.Id.Value;
        var state = new JsonObject();
        model.WriteToJson(state);
        using var restored = new TtsTabViewModel(new(), editor, (text, settings, _) => Synthesize(text, settings));
        restored.Voice.Value = model.Voice.Value;
        restored.ReadFromJson(state);
        await Settle();
        Assert.That(restored.LipSync.SelectedId, Is.EqualTo(target.Id.Value));
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        editor.Time.Value = At(1);
        await restored.Generate();
        editor.Time.Value = At(3.5);
        await restored.Generate();
        Assert.That(editor.Scene.EnumerateAllChildren<TtsController>().Count(), Is.EqualTo(2), restored.Message.Value);
        Assert.That(editor.Scene.EnumerateAllChildren<TtsController>().All(x => x.LipSyncGroupId.Value == PsdLipSync.GroupOf(target)), Is.True);
        using (var render = new RenderProbe(editor.Scene))
        {
            render.Mouth(1.5, true);
            render.Mouth(3.25, false);
            render.Mouth(4, true);
        }
        Assert.That(editor.History.UndoCount, Is.EqualTo(2));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(PsdLipSync.GroupOf(target), Is.Not.EqualTo(Guid.Empty));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.EnumerateAllChildren<TtsController>(), Is.Empty);
        Assert.That(target.LipSyncAudio.Expression, Is.Null);
        Assert.That(target.LipSyncOffset.Expression, Is.Null);
        Assert.That(target.LipSyncOffset.Value, Is.EqualTo(At(0.35)));
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(editor.History.Redo(), Is.True);
        target.FindHierarchicalParent<Element>()!.IsLocked.Value = true;
        editor.Time.Value = At(6);
        await restored.Generate();
        Assert.That(editor.Scene.EnumerateAllChildren<TtsController>().Count(), Is.EqualTo(3),
            "An existing link can accept another voice without editing the locked character. " + restored.Message.Value);
    }

    [AvaloniaTest]
    public async Task Script_generation_restores_per_speaker_targets_by_id_and_links_the_batch_in_one_undo()
    {
        using var editor = Editor();
        var first = Character(editor.Scene);
        var second = Character(editor.Scene, 3); // Same name, different object.
        var preset = GenerationPreset();
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
        using var model = new ScriptImportViewModel(editor, Synthesize);
        ConfigureScript(model, preset);
        model.Speakers[0].LipSync.SelectedId = first.Id.Value;
        model.Speakers[1].LipSync.SelectedId = second.Id.Value;
        var state = new JsonObject();
        model.WriteToJson(state);
        var secondElement = second.FindHierarchicalParent<Element>()!;
        editor.Scene.RemoveChild(secondElement);
        using var restored = new ScriptImportViewModel(editor, Synthesize);
        restored.ReadFromJson(state);
        restored.Speakers[1].LipSync.Refresh();
        Assert.That(restored.Speakers[1].LipSync.SelectedId, Is.EqualTo(second.Id.Value));
        await restored.Generate();
        Assert.That(editor.Scene.EnumerateAllChildren<TtsController>(), Is.Empty);
        Assert.That(restored.Message.Value, Does.Contain("立ち絵が見つかりません"));
        restored.WriteToJson(state);
        restored.ReadFromJson(state);
        editor.Scene.AddChild(secondElement);
        restored.Speakers[1].LipSync.Refresh();
        restored.ImportText();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        await restored.Generate();
        var controllers = editor.Scene.EnumerateAllChildren<TtsController>().ToArray();
        Assert.That(controllers, Has.Length.EqualTo(3), restored.Message.Value);
        Assert.That(controllers.Select(x => x.LipSyncGroupId.Value),
            Is.EqualTo(new[] { PsdLipSync.GroupOf(first), PsdLipSync.GroupOf(second), PsdLipSync.GroupOf(first) }));
        Assert.That(PsdLipSync.GroupOf(first), Is.Not.EqualTo(PsdLipSync.GroupOf(second)).And.Not.EqualTo(Guid.Empty));
        Assert.That(editor.History.UndoCount, Is.EqualTo(1));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(2));
        Assert.That(first.LipSyncAudio.Expression, Is.Null);
        Assert.That(second.LipSyncAudio.Expression, Is.Null);
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(first.LipSyncAudio.Evaluate(new EvaluationContext(At(1.5))), Is.Not.Null);
        Assert.That(second.LipSyncAudio.Evaluate(new EvaluationContext(At(1.5))), Is.Null);
        Assert.That(second.LipSyncAudio.Evaluate(new EvaluationContext(At(4))), Is.Not.Null);
        restored.Speakers[1].LipSync.SelectedId = null;
        restored.WriteToJson(state);
        restored.ReadFromJson(state);
        Assert.That(restored.Speakers[1].LipSync.SelectedId, Is.Null, "An explicit no-link choice survives restoration.");
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Speech_generation_rechecks_a_removed_or_locked_target_and_can_retry(bool remove)
    {
        using var editor = Editor();
        var target = Character(editor.Scene);
        var element = target.FindHierarchicalParent<Element>()!;
        bool changeTarget = true;
        using var model = new TtsTabViewModel(new(), editor, (text, settings, _) =>
        {
            if (changeTarget)
            {
                if (remove) editor.Scene.RemoveChild(element);
                else element.IsLocked.Value = true;
            }
            return Synthesize(text, settings);
        });
        ConfigureSpeech(model, GenerationPreset());
        model.LipSync.SelectedId = target.Id.Value;
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        await model.Generate();
        Assert.That(model.Message.Value, Does.Contain(remove ? "見つかりません" : "ロック"));
        Assert.That(editor.Scene.EnumerateAllChildren<TtsController>(), Is.Empty);
        Assert.That(target.LipSyncAudio.Expression, Is.Null);
        Assert.That(Directory.GetFiles(Path.Combine(_directory, "resources", "tts")), Is.Empty);
        Assert.That(model.LipSync.SelectedId, Is.EqualTo(target.Id.Value));
        Assert.That(editor.History.UndoCount, Is.EqualTo(1), "The user's target edit remains, but failed placement does not.");
        changeTarget = false;
        if (remove) editor.Scene.AddChild(element);
        else element.IsLocked.Value = false;
        await model.Generate();
        Assert.That(editor.Scene.EnumerateAllChildren<TtsController>().Single().LipSyncGroupId.Value,
            Is.EqualTo(PsdLipSync.GroupOf(target)).And.Not.EqualTo(Guid.Empty));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Failed_batch_placement_rolls_back_audio_and_preceding_character_links(bool overlap)
    {
        using var editor = Editor();
        var first = Character(editor.Scene);
        var second = Character(editor.Scene, 3);
        var secondElement = second.FindHierarchicalParent<Element>()!;
        var obstacle = new Element { Start = { Value = At(3.5) }, Length = { Value = At(1) }, ZIndex = { Value = 1 } };
        int calls = 0;
        var preset = GenerationPreset();
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
        using var model = new ScriptImportViewModel(editor, (text, settings) =>
        {
            if (++calls == 2)
            {
                if (overlap) editor.Scene.AddChild(obstacle);
                else secondElement.IsLocked.Value = true;
            }
            return Synthesize(text, settings);
        });
        ConfigureScript(model, preset);
        model.Speakers[0].LipSync.SelectedId = first.Id.Value;
        model.Speakers[1].LipSync.SelectedId = second.Id.Value;
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        await model.Generate();
        Assert.That(model.Message.Value, Does.Contain("配置できません"));
        Assert.That(editor.Scene.EnumerateAllChildren<TtsController>(), Is.Empty);
        Assert.That(first.LipSyncAudio.Expression, Is.Null);
        Assert.That(first.LipSyncOffset.Expression, Is.Null);
        Assert.That(second.LipSyncAudio.Expression, Is.Null);
        Assert.That(Directory.GetFiles(Path.Combine(_directory, "resources", "tts")), Is.Empty);
        if (overlap) editor.Scene.RemoveChild(obstacle);
        else secondElement.IsLocked.Value = false;
        await model.Generate();
        Assert.That(editor.Scene.EnumerateAllChildren<TtsController>().Count(), Is.EqualTo(3), model.Message.Value);
    }

    [AvaloniaTest]
    [TestCase(320, false)]
    [TestCase(460, true)]
    public async Task Generation_views_select_targets_and_keep_actions_visible(int width, bool light)
    {
        using var editor = Editor();
        var first = Character(editor.Scene);
        var second = Character(editor.Scene, 3);
        var preset = GenerationPreset();
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
        using var model = new TtsTabViewModel(new(), editor, (text, settings, _) => Synthesize(text, settings));
        ConfigureSpeech(model, preset);
        ConfigureScript(model.Script, preset);
        var view = new TtsTabView { DataContext = model };
        var window = new Window { Width = width, Height = 600, Content = view,
            RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        try
        {
            window.Show();
            await Settle();
            var picker = view.FindControl<LipSyncSelectionView>("GenerationLipSync")!;
            var combo = picker.FindControl<ComboBox>("TargetSelector")!;
            picker.BringIntoView();
            await Settle();
            combo.IsDropDownOpen = true;
            await Settle();
            combo.SelectedItem = model.LipSync.Targets.Value.Single(x => x.Id == first.Id.Value);
            combo.IsDropDownOpen = false;
            await Settle();
            Assert.That(model.LipSync.SelectedId, Is.EqualTo(first.Id.Value));
            Capture("reading", picker, view.FindControl<Button>("AddSpeechButton")!);
            model.SelectedPage.Value = 1;
            await Settle();
            var script = view.FindControl<ScriptImportView>("ScriptPage")!;
            script.FindControl<Expander>("InputSection")!.IsExpanded = false;
            await Settle();
            var pickers = script.GetVisualDescendants().OfType<LipSyncSelectionView>().ToArray();
            Assert.That(pickers, Has.Length.EqualTo(2));
            pickers[0].FindControl<ComboBox>("TargetSelector")!.SelectedItem = model.Script.Speakers[0].LipSync.Targets.Value.Single(x => x.Id == first.Id.Value);
            pickers[1].FindControl<ComboBox>("TargetSelector")!.SelectedItem = model.Script.Speakers[1].LipSync.Targets.Value.Single(x => x.Id == second.Id.Value);
            pickers[1].BringIntoView();
            await Settle();
            Assert.That(model.Script.Speakers.Select(x => x.LipSync.SelectedId), Is.EqualTo(new[] { first.Id.Value, second.Id.Value }));
            Capture("script", pickers[1], script.FindControl<Button>("GenerateScriptButton")!);
            model.SelectedPage.Value = 0;
            await Settle();
            Assert.That(model.LipSync.SelectedId, Is.EqualTo(first.Id.Value));

            void Capture(string page, Control selector, Button generate)
            {
                var origin = selector.TranslatePoint(default, window)!.Value;
                Assert.That(origin.X, Is.GreaterThanOrEqualTo(0));
                Assert.That(origin.X + selector.Bounds.Width, Is.LessThanOrEqualTo(width));
                Assert.That(generate.TranslatePoint(default, window)!.Value.Y + generate.Bounds.Height,
                    Is.LessThanOrEqualTo(window.ClientSize.Height));
                if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is not { } directory) return;
                Directory.CreateDirectory(directory);
                using var frame = window.CaptureRenderedFrame()!;
                frame.Save(Path.Combine(directory, $"generation-lipsync-{page}-{width}.png"), new PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); }
    }
}
