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
        Assert.That(model.PresetDetails.Value, Does.Not.Contain("口パクの変更:"));
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
        Assert.That(model.PresetDetails.Value, Does.Contain("口パクの変更:").And.Contain(PsdLipSync.TargetName(target)));
        model.UpdatePreset();
        Assert.That(model.PresetDetails.Value, Does.Not.Contain("口パクの変更:"));
        Assert.That(model.SelectedPreset.Value!.LipSync!.ObjectJson, Does.Contain("変更したパーツ"));
        Assert.That(VoicePresetStore.Load(VoicePresetStore.GlobalPath).Single(x => x.Id == preset.Id), Is.EqualTo(preset));
        Assert.That(copy.Layers.Value, Is.EqualTo("-非表示"));
        model.SelectedPreset.Value = preset;
        model.ApplyPreset();
        Assert.That(model.LipSync.Preset, Is.EqualTo(preset.LipSync));
        model.LipSync.Preset = null;
        Assert.That(model.PresetDetails.Value, Does.Contain("口パクの変更:").And.Contain("→ 連携しない"));
        Assert.That(model.PresetDetails.Value, Does.Not.Contain("現在の設定と一致"));
        model.PitchScale.Value = 0.1;
        Assert.That(model.PresetDetails.Value, Does.Contain("音高:").And.Contain("口パクの変更:"));
        model.ApplyPreset();
        Assert.That(model.PresetDetails.Value, Does.Not.Contain("口パクの変更:").And.Not.Contain("音高:"));
        model.SelectedPreset.Value = GenerationPreset();
        model.ApplyPreset();
        Assert.That(model.LipSync.Preset, Is.Null);
        Assert.That(model.LipSync.SelectedId, Is.Null);
        Assert.That(model.PresetDetails.Value, Does.Not.Contain("口パクの変更:"));
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
    [TestCase(false)]
    [TestCase(true)]
    public async Task Script_extends_independent_groups_only_to_their_lines_and_keeps_split_groups_continuous(bool split)
    {
        using var editor = Editor();
        var first = Character(editor.Scene);
        var firstElement = first.FindHierarchicalParent<Element>()!;
        firstElement.Start.Value = At(3);
        firstElement.Length.Value = At(7);
        var previous = Speech(editor.Scene, 3);
        PsdLipSync.Connect(previous.Controller, first, extendToSpeech: true);
        editor.Scene.RemoveChild(previous.Element);
        PsdTachieDrawable second;
        if (split)
        {
            new ElementStructureService(editor.History).Split(editor.Scene, [firstElement], At(5));
            second = editor.Scene.EnumerateAllChildren<PsdTachieDrawable>().Single(x => x != first);
        }
        else
        {
            firstElement.Length.Value = At(2);
            second = Character(editor.Scene);
            var element = second.FindHierarchicalParent<Element>()!;
            element.Start.Value = At(8);
            element.Length.Value = At(2);
            previous = Speech(editor.Scene, 8);
            PsdLipSync.Connect(previous.Controller, second, extendToSpeech: true);
            editor.Scene.RemoveChild(previous.Element);
        }
        var secondElement = second.FindHierarchicalParent<Element>()!;
        var preset = GenerationPreset() with { LipSync = PsdLipSyncPresets.Capture(first) };
        using var model = new ScriptImportViewModel(editor, Synthesize);
        ConfigureScript(model, preset);
        model.Speakers[1].SelectedPreset.Value = GenerationPreset();
        model.RawText.Value = "案内役: 最初のセリフ。\nゲスト: 合いの手。\n案内役: 次のセリフ。\nゲスト: 最後のセリフ。";
        model.GapSeconds.Value = 3;
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        await model.Generate();
        var voices = editor.Scene.EnumerateAllChildren<TtsController>().OrderBy(x => x.TimeRange.Value.Start).ToArray();
        Assert.That(voices, Has.Length.EqualTo(4), model.Message.Value);
        Assert.That(voices[0].LipSyncGroupId.Value, Is.EqualTo(PsdLipSync.GroupOf(first)));
        Assert.That(voices[2].LipSyncGroupId.Value, Is.EqualTo(PsdLipSync.GroupOf(second)));
        Assert.That(firstElement.Range.Start, Is.EqualTo(At(1)));
        Assert.That(firstElement.Range.End, Is.EqualTo(At(5)));
        Assert.That(secondElement.Range.Start, Is.EqualTo(At(split ? 5 : 8)));
        Assert.That(secondElement.Range.End, Is.EqualTo(At(split ? 18 : 13)));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(2));
        Assert.That(firstElement.Range.Start, Is.EqualTo(At(3)));
        Assert.That(firstElement.Range.End, Is.EqualTo(At(5)));
        Assert.That(secondElement.Range.Start, Is.EqualTo(At(split ? 5 : 8)));
        Assert.That(secondElement.Range.End, Is.EqualTo(At(10)));
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(firstElement.Range.End, Is.EqualTo(At(5)));
        Assert.That(secondElement.Range.End, Is.EqualTo(At(split ? 18 : 13)));
    }

    [AvaloniaTest]
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task Preset_paths_follow_filesystem_case_rules_for_generation_and_reuse(bool script, bool reuse)
    {
        using var editor = Editor();
        var source = Character(editor.Scene);
        string originalPath = Path.Combine(_directory, "Character.psd");
        string caseVariant = Path.Combine(_directory, "CHARACTER.PSD");
        string otherPath = Path.Combine(_directory, "Other.psd");
        File.Copy(source.Source.Value!.FullName, originalPath);
        bool sameFile = File.Exists(caseVariant);
        if (!sameFile) File.Copy(originalPath, caseVariant);
        File.Copy(originalPath, otherPath);
        var presets = new[] { originalPath, caseVariant, otherPath }.Select(path =>
        {
            source.Source.Value = new FileInfo(path);
            return GenerationPreset() with { LipSync = PsdLipSyncPresets.Capture(source) };
        }).ToArray();
        source.Source.Value = new FileInfo(originalPath);
        if (!reuse) editor.Scene.RemoveChild(source.FindHierarchicalParent<Element>()!);
        if (script)
        {
            using var model = new ScriptImportViewModel(editor, Synthesize);
            model.RawText.Value = "一人目: こんにちは。\n二人目: よろしく。\n三人目: 始めましょう。";
            model.ImportText();
            for (int i = 0; i < presets.Length; i++) model.Speakers[i].SelectedPreset.Value = presets[i];
            model.IsVoiceVoxInstalled.Value = true;
            await model.Generate();
            Assert.That(model.Message.Value, Does.Not.Contain("配置できません"));
        }
        else
        {
            var bindings = presets.Select((preset, i) => (Speech(editor.Scene, 1 + i * 2.5).Controller, preset.LipSync!)).ToArray();
            PsdLipSyncPresets.Connect(editor.Scene, bindings);
        }
        var targets = PsdLipSync.Targets(editor.Scene).ToArray();
        Assert.That(targets, Has.Length.EqualTo(sameFile ? 2 : 3));
        if (reuse) Assert.That(targets, Does.Contain(source));
        var voices = editor.Scene.EnumerateAllChildren<TtsController>().OrderBy(x => x.TimeRange.Value.Start).ToArray();
        Assert.That(voices, Has.Length.EqualTo(3));
        Assert.That(voices[0].LipSyncGroupId.Value, Is.Not.EqualTo(Guid.Empty));
        Assert.That(voices[0].LipSyncGroupId.Value == voices[1].LipSyncGroupId.Value, Is.EqualTo(sameFile));
        Assert.That(voices[2].LipSyncGroupId.Value, Is.Not.EqualTo(voices[0].LipSyncGroupId.Value).And.Not.EqualTo(voices[1].LipSyncGroupId.Value));
        if (script && sameFile)
            Assert.That(voices[0].FindHierarchicalParent<Element>()!.ZIndex.Value,
                Is.EqualTo(voices[1].FindHierarchicalParent<Element>()!.ZIndex.Value));
    }

    [AvaloniaTest]
    [TestCase(0, false)]
    [TestCase(4, true)]
    public async Task Script_places_full_length_characters_above_their_speech_automatically(int firstLayer, bool separateLayers)
    {
        using var editor = Editor();
        var first = Character(editor.Scene);
        var second = Character(editor.Scene);
        string guestPath = Path.Combine(_directory, "guest.psd");
        File.Copy(second.Source.Value!.FullName, guestPath);
        second.Source.Value = new FileInfo(guestPath);
        first.Transform.Value = new TranslateTransform { X = { Value = -60 } };
        second.Transform.Value = new TranslateTransform { X = { Value = 60 } };
        var preset = GenerationPreset() with { LipSync = PsdLipSyncPresets.Capture(first) };
        var guest = GenerationPreset() with { LipSync = PsdLipSyncPresets.Capture(second) };
        editor.Scene.RemoveChild(first.FindHierarchicalParent<Element>()!);
        editor.Scene.RemoveChild(second.FindHierarchicalParent<Element>()!);
        using var model = new ScriptImportViewModel(editor, Synthesize);
        ConfigureScript(model, preset);
        model.Speakers[1].SelectedPreset.Value = guest;
        model.RawText.Value = "案内役: こんにちは。\nゲスト: よろしくお願いします。\n案内役: 始めましょう。\nゲスト: はい。";
        model.StartSeconds.Value = 0;
        model.FirstLayer.Value = firstLayer;
        model.SeparateLayers.Value = separateLayers;
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        await model.Generate();
        var characters = PsdLipSync.Targets(editor.Scene).OrderBy(x => x.FindHierarchicalParent<Element>()!.ZIndex.Value).ToArray();
        Assert.That(characters, Has.Length.EqualTo(2), model.Message.Value);
        Assert.That(characters.Select(x => x.FindHierarchicalParent<Element>()!.ZIndex.Value),
            Is.EqualTo(new[] { firstLayer, firstLayer + 2 }));
        Assert.That(characters.All(x => x.TimeRange.Value.Start == At(0) && x.TimeRange.Value.End == At(9.5)), Is.True);
        var voices = editor.Scene.EnumerateAllChildren<TtsController>().ToArray();
        Assert.That(voices, Has.Length.EqualTo(4));
        Assert.That(voices.Select(x => x.TimeRange.Value.Start), Is.EqualTo(new[] { At(0), At(2.5), At(5), At(7.5) }));
        Assert.That(voices.Select(x => x.FindHierarchicalParent<Element>()!.ZIndex.Value),
            Is.EqualTo(new[] { firstLayer + 1, firstLayer + 3, firstLayer + 1, firstLayer + 3 }));
        Assert.That(voices.Select(x => x.LipSyncGroupId.Value), Is.EqualTo(new[]
        {
            PsdLipSync.GroupOf(characters[0]), PsdLipSync.GroupOf(characters[1]),
            PsdLipSync.GroupOf(characters[0]), PsdLipSync.GroupOf(characters[1])
        }));
        using (var render = new RenderProbe(editor.Scene))
        {
            render.Mouth(0.5, true, "script-paired-first-speaking", offsetX: -60);
            render.Mouth(0.5, false, offsetX: 60);
            render.Mouth(2.25, false, "script-paired-gap", offsetX: -60);
            render.Mouth(2.25, false, offsetX: 60);
            render.Mouth(3, false, offsetX: -60);
            render.Mouth(3, true, "script-paired-second-speaking", offsetX: 60);
        }
        Assert.That(editor.History.UndoCount, Is.EqualTo(1));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Is.Empty);
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(6));
        model.StartSeconds.Value = 12;
        await model.Generate();
        Assert.That(PsdLipSync.Targets(editor.Scene).ToArray(), Is.EqualTo(characters), model.Message.Value);
        Assert.That(characters.All(x => x.TimeRange.Value.Start == At(0) && x.TimeRange.Value.End == At(21.5)), Is.True);
        Assert.That(editor.Scene.EnumerateAllChildren<TtsController>().Skip(4)
            .Select(x => x.FindHierarchicalParent<Element>()!.ZIndex.Value),
            Is.EqualTo(new[] { firstLayer + 1, firstLayer + 3, firstLayer + 1, firstLayer + 3 }));
    }

    [AvaloniaTest]
    public async Task Script_presets_for_the_same_psd_share_one_character_and_audio_layer()
    {
        using var editor = Editor();
        var source = Character(editor.Scene);
        var preset = GenerationPreset() with { LipSync = PsdLipSyncPresets.Capture(source) };
        var other = preset with { Id = Guid.NewGuid(), Name = "別の声" };
        editor.Scene.RemoveChild(source.FindHierarchicalParent<Element>()!);
        editor.Scene.Layers.Add(new TimelineLayer { ZIndex = { Value = 1 }, IsLocked = { Value = true } });
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
        Assert.That(character.ZIndex.Value, Is.EqualTo(2));
        var voices = editor.Scene.EnumerateAllChildren<TtsController>().ToArray();
        Assert.That(voices, Has.Length.EqualTo(3), model.Message.Value);
        Assert.That(voices.All(x => x.LipSyncGroupId.Value == PsdLipSync.GroupOf(target)), Is.True);
        Assert.That(voices.All(x => x.FindHierarchicalParent<Element>()!.ZIndex.Value == 3), Is.True);
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
