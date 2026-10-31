using System.Text.Json.Nodes;
using Avalonia.Headless.NUnit;
using Beutl.Editor.Observers;
using Beutl.Editor.Services;
using Beutl.Engine;
using Beutl.Extensions.PsdTachie.Graphics;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Graphics.Transformation;
using Beutl.ProjectSystem;
using Beutl.Serialization;

namespace Beutl.Extensions.Voice.Tests;

public partial class PsdLipSyncTests
{
    [AvaloniaTest]
    public void Presets_store_independent_drawable_settings_without_the_source_speech_link()
    {
        using var editor = Editor();
        var target = Character(editor.Scene);
        target.Transform.Value = new TranslateTransform { X = { Value = 35 }, Y = { Value = -20 } };
        target.OpenThreshold.Value = -18;
        target.LipSyncOffset.Value = At(0.4);
        var speech = Speech(editor.Scene, 1);
        PsdLipSync.Connect(speech.Controller, target);
        using var model = new TtsTabViewModel(new(), editor);
        ConfigureSpeech(model, GenerationPreset());
        model.PresetName.Value = "立ち絵付き";
        model.LipSync.SelectedId = target.Id.Value;
        model.AddPreset();
        var preset = VoicePresetStore.Load(VoicePresetStore.GlobalPath).Single();
        Assert.That(preset.LipSync, Is.Not.Null, model.PresetMessage.Value);
        Assert.That(model.LipSync.Preset, Is.EqualTo(preset.LipSync));
        var copy = (PsdTachieDrawable)CoreSerializer.DeserializeFromJsonObject(
            JsonNode.Parse(preset.LipSync!.ObjectJson)!.AsObject(), typeof(EngineObject));
        Assert.That(copy.Id.Value, Is.Not.EqualTo(target.Id.Value));
        Assert.That(copy.Transform.Value!.Id.Value, Is.Not.EqualTo(target.Transform.Value.Id.Value));
        Assert.That(((TranslateTransform)copy.Transform.Value).X.Value, Is.EqualTo(35));
        Assert.That(((TranslateTransform)copy.Transform.Value).Y.Value, Is.EqualTo(-20));
        Assert.That(copy.MouthOpen.Value, Is.EqualTo(target.MouthOpen.Value));
        Assert.That(copy.EyeOpen.Value, Is.EqualTo(target.EyeOpen.Value));
        Assert.That(copy.Layers.Value, Is.EqualTo(target.Layers.Value));
        Assert.That(copy.OpenThreshold.Value, Is.EqualTo(-18));
        Assert.That(copy.LipSyncOffset.Value, Is.EqualTo(At(0.4)));
        Assert.That(copy.LipSyncAudio.Expression, Is.Null);
        Assert.That(copy.LipSyncOffset.Expression, Is.Null);
        Assert.That(PsdLipSync.GroupOf(target), Is.EqualTo(speech.Controller.LipSyncGroupId.Value).And.Not.EqualTo(Guid.Empty));

        target.Layers.Value = "変更したパーツ";
        model.DuplicatePreset();
        var duplicate = model.SelectedPreset.Value!;
        Assert.That(duplicate.LipSync, Is.EqualTo(preset.LipSync));
        model.PresetName.Value = "別の立ち絵設定";
        model.RenamePreset();
        Assert.That(model.SelectedPreset.Value!.LipSync, Is.EqualTo(preset.LipSync));
        model.LipSync.SelectedId = target.Id.Value;
        model.UpdatePreset();
        Assert.That(model.SelectedPreset.Value!.LipSync!.ObjectJson, Does.Contain("変更したパーツ"));
        Assert.That(VoicePresetStore.Load(VoicePresetStore.GlobalPath).Single(x => x.Id == preset.Id), Is.EqualTo(preset));
        Assert.That(copy.Layers.Value, Is.EqualTo("-非表示"));
        model.SelectedPreset.Value = preset;
        model.ApplyPreset();
        Assert.That(model.LipSync.Preset, Is.EqualTo(preset.LipSync));
        model.SelectedPreset.Value = GenerationPreset();
        model.ApplyPreset();
        Assert.That(model.LipSync.Preset, Is.Null);
        Assert.That(model.LipSync.SelectedId, Is.Null);
    }

    [AvaloniaTest]
    public async Task Preset_speech_creates_one_character_extends_it_and_restores_ranges_with_undo_and_reload()
    {
        using var source = Editor();
        var original = Character(source.Scene);
        var preset = GenerationPreset() with { LipSync = PsdLipSyncPresets.Capture(original) };
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
        using var editor = new TestEditor(Path.Combine(_directory, "other-scene.bescene"));
        editor.Scene.FrameSize.Value = source.Scene.FrameSize.Value;
        using var model = new TtsTabViewModel(new(), editor, (text, settings, _) => Synthesize(text, settings));
        ConfigureSpeech(model, preset);
        model.SelectedPreset.Value = preset;
        model.ApplyPreset();
        var state = new JsonObject();
        model.WriteToJson(state);
        model.LipSync.Preset = null;
        model.ReadFromJson(state);
        await Settle();
        Assert.That(model.LipSync.Preset, Is.EqualTo(preset.LipSync));
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        editor.Time.Value = At(2);
        await model.Generate();
        var target = editor.Scene.EnumerateAllChildren<PsdTachieDrawable>().Single();
        var character = target.FindHierarchicalParent<Element>()!;
        Assert.That(target.Id.Value, Is.Not.EqualTo(original.Id.Value));
        Assert.That(target.Transform.Value!.Id.Value, Is.Not.EqualTo(original.Transform.Value!.Id.Value));
        Assert.That(target.Source.Value!.FullName, Is.EqualTo(original.Source.Value!.FullName));
        Assert.That(target.MouthOpen.Value, Is.EqualTo(original.MouthOpen.Value));
        Assert.That(character.Range.Start, Is.EqualTo(At(2)));
        Assert.That(character.Range.End, Is.EqualTo(At(4)));
        editor.Time.Value = At(6);
        await model.Generate();
        Assert.That(PsdLipSync.Targets(editor.Scene).Single(), Is.SameAs(target), model.Message.Value);
        Assert.That(character.Range.End, Is.EqualTo(At(8)));
        Assert.That(editor.History.UndoCount, Is.EqualTo(2));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(character.Range.End, Is.EqualTo(At(4)));
        Assert.That(editor.Scene.EnumerateAllChildren<TtsController>().Count(), Is.EqualTo(1));
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(character.Range.End, Is.EqualTo(At(8)));
        using (var render = new RenderProbe(editor.Scene))
        {
            render.Mouth(2.5, true, "preset-character-speaking");
            render.Mouth(5, false, "preset-character-gap");
            render.Mouth(6.5, true);
        }

        using var reopened = new TestEditor(Path.Combine(_directory, "reopened.bescene"));
        foreach (var element in editor.Scene.Children)
            reopened.Scene.AddChild((Element)CoreSerializer.DeserializeFromJsonObject(
                CoreSerializer.SerializeToJsonObject(element), typeof(Element)), ElementOverlapHandling.Allow);
        using var reopenedObserver = new CoreObjectOperationObserver(null, reopened.Scene, reopened.Sequence);
        using var reopenedSubscription = reopened.History.Subscribe(reopenedObserver);
        using var next = new TtsTabViewModel(new(), reopened, (text, settings, _) => Synthesize(text, settings));
        ConfigureSpeech(next, preset);
        next.SelectedPreset.Value = preset;
        next.ApplyPreset();
        reopened.Time.Value = At(0);
        await next.Generate();
        var restored = PsdLipSync.Targets(reopened.Scene).Single().FindHierarchicalParent<Element>()!;
        Assert.That(restored.Range.Start, Is.EqualTo(At(0)), next.Message.Value);
        Assert.That(restored.Range.End, Is.EqualTo(At(8)));
        Assert.That(reopened.Scene.EnumerateAllChildren<TtsController>().Count(), Is.EqualTo(3));
        restored.IsLocked.Value = true;
        reopened.Time.Value = At(10);
        await next.Generate();
        Assert.That(next.Message.Value, Does.Contain("ロック"));
        Assert.That(reopened.Scene.EnumerateAllChildren<TtsController>().Count(), Is.EqualTo(3));
        Assert.That(restored.Range.End, Is.EqualTo(At(8)));
        restored.IsLocked.Value = false;
        await next.Generate();
        Assert.That(restored.Range.End, Is.EqualTo(At(12)), next.Message.Value);
    }

    [AvaloniaTest]
    [TestCase(0, false)]
    [TestCase(2, true)]
    [TestCase(3, true)]
    public async Task Script_selects_each_same_psd_character_by_the_speech_interval(double gap, bool useSecond)
    {
        using var editor = Editor();
        var first = Character(editor.Scene);
        var second = Character(editor.Scene);
        var firstElement = first.FindHierarchicalParent<Element>()!;
        var secondElement = second.FindHierarchicalParent<Element>()!;
        firstElement.Length.Value = At(5);
        secondElement.Start.Value = At(5);
        secondElement.Length.Value = At(5);
        var preset = GenerationPreset() with { LipSync = PsdLipSyncPresets.Capture(first) };
        using var model = new ScriptImportViewModel(editor, Synthesize);
        ConfigureScript(model, preset);
        model.RawText.Value = "案内役: 最初のセリフ。\n案内役: 次のセリフ。";
        model.GapSeconds.Value = gap;
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        await model.Generate();
        var voices = editor.Scene.EnumerateAllChildren<TtsController>()
            .OrderBy(x => x.TimeRange.Value.Start).ToArray();
        Assert.That(voices, Has.Length.EqualTo(2), model.Message.Value);
        Assert.That(voices[0].LipSyncGroupId.Value, Is.EqualTo(PsdLipSync.GroupOf(first)).And.Not.EqualTo(Guid.Empty));
        Assert.That(voices[1].LipSyncGroupId.Value,
            Is.EqualTo(PsdLipSync.GroupOf(useSecond ? second : first)).And.Not.EqualTo(Guid.Empty));
        Assert.That(second.LipSyncAudio.Expression != null, Is.EqualTo(useSecond));
        Assert.That(PsdLipSync.Targets(editor.Scene).Count(), Is.EqualTo(2));
        Assert.That(firstElement.Range.Start, Is.EqualTo(At(0)));
        Assert.That(firstElement.Range.End, Is.EqualTo(At(5)));
        Assert.That(secondElement.Range.Start, Is.EqualTo(At(5)));
        Assert.That(secondElement.Range.End, Is.EqualTo(At(10)));
        using var render = new RenderProbe(editor.Scene);
        render.Mouth(1.5, true);
        render.Mouth(3.5 + gap, true, $"same-psd-second-line-gap-{gap}");
        render.Mouth(4.5 + gap, false);
        Assert.That(editor.History.UndoCount, Is.EqualTo(1));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(2));
        Assert.That(first.LipSyncAudio.Expression, Is.Null);
        Assert.That(second.LipSyncAudio.Expression, Is.Null);
        render.Mouth(3.5 + gap, false);
        Assert.That(editor.History.Redo(), Is.True);
        render.Mouth(3.5 + gap, true);
    }

    [AvaloniaTest]
    public async Task Script_presets_for_the_same_psd_share_one_character_across_separate_voice_layers()
    {
        using var editor = Editor();
        var source = Character(editor.Scene);
        var preset = GenerationPreset() with { LipSync = PsdLipSyncPresets.Capture(source) };
        var other = preset with { Id = Guid.NewGuid(), Name = "別の声" };
        editor.Scene.RemoveChild(source.FindHierarchicalParent<Element>()!);
        editor.Scene.Layers.Add(new TimelineLayer { ZIndex = { Value = 0 }, IsLocked = { Value = true } });
        using var model = new ScriptImportViewModel(editor, Synthesize);
        ConfigureScript(model, preset);
        model.Speakers[1].SelectedPreset.Value = other;
        model.SeparateLayers.Value = true;
        model.FirstLayer.Value = 1;
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        await model.Generate();
        var target = PsdLipSync.Targets(editor.Scene).Single();
        var character = target.FindHierarchicalParent<Element>()!;
        Assert.That(character.Range.Start, Is.EqualTo(At(1)));
        Assert.That(character.Range.End, Is.EqualTo(At(8)));
        Assert.That(character.ZIndex.Value, Is.EqualTo(3));
        var voices = editor.Scene.EnumerateAllChildren<TtsController>().ToArray();
        Assert.That(voices, Has.Length.EqualTo(3), model.Message.Value);
        Assert.That(voices.All(x => x.LipSyncGroupId.Value == PsdLipSync.GroupOf(target)), Is.True);
        Assert.That(editor.History.UndoCount, Is.EqualTo(1));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Is.Empty);
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(4));

        // Extending after splitting must keep the interior cut and adjust only the outside boundary.
        new ElementStructureService(editor.History).Split(editor.Scene, [character], At(4));
        var before = PsdLipSync.Targets(editor.Scene).OrderBy(x => x.TimeRange.Value.Start).First();
        model.StartSeconds.Value = 10;
        await model.Generate();
        var parts = PsdLipSync.Targets(editor.Scene).OrderBy(x => x.TimeRange.Value.Start).ToArray();
        Assert.That(parts, Has.Length.EqualTo(2), model.Message.Value);
        Assert.That(parts[0].TimeRange.Value.End, Is.EqualTo(At(4)));
        Assert.That(parts[1].TimeRange.Value.Start, Is.EqualTo(At(4)));
        Assert.That(parts[1].TimeRange.Value.End, Is.EqualTo(At(17)));
        Assert.That(PsdLipSync.GroupOf(before), Is.EqualTo(PsdLipSync.GroupOf(parts[1])));
    }
}
