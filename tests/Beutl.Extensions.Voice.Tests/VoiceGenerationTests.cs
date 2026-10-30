using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Beutl.Audio;
using Beutl.Editor.Observers;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.Serialization;

namespace Beutl.Extensions.Voice.Tests;

public class VoiceGenerationTests
{
    [AvaloniaTest]
    public async Task Native_generation_keeps_snapshots_independent_and_supports_undo()
    {
        string? coreHome = Environment.GetEnvironmentVariable("VOICEVOX_TEST_HOME");
        if (coreHome == null) Assert.Ignore("Set VOICEVOX_TEST_HOME to an installed VOICEVOX Core directory.");
        string directory = Path.Combine(Path.GetTempPath(), "voice-generation-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string? previousHome = Environment.GetEnvironmentVariable("BEUTL_HOME");
        Environment.SetEnvironmentVariable("BEUTL_HOME", directory);
        var loader = new VoiceVoxLoader(coreHome!);
        try
        {
            await Task.Run(loader.Load);
            Assert.That(loader.IsLoaded, Is.True);
            TtsLoader.VoiceVoxLoader.Value = loader;
            using var editor = new TestEditor(Path.Combine(directory, "scene.bescene"));
            using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
            using var subscription = editor.History.Subscribe(observer);
            using var model = new TtsTabViewModel(new TtsTabExtension(), editor);
            model.OnLoaded();
            model.SelectedVoice.Value = model.Voice.Value.First();
            model.Text.Value = "こんにちは。";
            model.PresetName.Value = "実音声のテスト";
            model.AddPreset();
            var appliedPreset = model.SelectedPreset.Value!;
            model.ApplyPreset();
            var inspectedPreset = appliedPreset with
            {
                Id = Guid.NewGuid(), Name = "確認するだけのプリセット",
                Settings = appliedPreset.Settings with { SpeedScale = 0.8 }
            };
            VoicePresetStore.Save(VoicePresetStore.GlobalPath, inspectedPreset);
            model.RefreshPresets();
            model.SelectedPreset.Value = model.Presets.Value.Single(x => x.Id == inspectedPreset.Id);
            await model.Generate();
            Assert.That(editor.Scene.Children, Has.Count.EqualTo(1));
            var element = editor.Scene.Children.Single();
            var controller = element.Objects.OfType<TtsController>().Single();
            var sound = element.Objects.OfType<SourceSound>().Single();
            var original = controller.Generation.Value!;
            Assert.That(original.PresetId, Is.EqualTo(appliedPreset.Id));
            Assert.That(original.PresetName, Is.EqualTo(appliedPreset.Name));
            Assert.That(original.Settings.SpeedScale, Is.EqualTo(appliedPreset.Settings.SpeedScale));
            VoicePresetStore.Delete(VoicePresetStore.GlobalPath, inspectedPreset.Id);
            model.RefreshPresets();
            model.SelectedPreset.Value = model.Presets.Value.Single();
            string originalPath = sound.Source.Value!.Uri.LocalPath;
            Assert.That(new FileInfo(originalPath).Length, Is.GreaterThan(44));
            element.Start.Value = MediaTime.FromSeconds(3);
            element.Length.Value = MediaTime.FromSeconds(1);
            sound.OffsetPosition.Value = MediaTime.FromSeconds(0.2);
            editor.History.Commit();
            editor.Selection.Value = element;
            model.SpeedScale.Value = 1.4;
            model.UpdatePreset();
            Assert.That(controller.Generation.Value, Is.EqualTo(original));
            Assert.That(sound.Source.Value.Uri.LocalPath, Is.EqualTo(originalPath));
            model.Text.Value = "本文も変更しました。";
            await model.Generate();
            Assert.That(editor.Scene.Children, Has.Count.EqualTo(2));
            var added = editor.Scene.Children.Single(x => x != element);
            var addedController = added.Objects.OfType<TtsController>().Single();
            var updated = addedController.Generation.Value!;
            Assert.That(updated.PresetId, Is.EqualTo(appliedPreset.Id));
            string updatedPath = added.Objects.OfType<SourceSound>().Single().Source.Value!.Uri.LocalPath;
            Assert.That(updatedPath, Is.Not.EqualTo(originalPath));
            Assert.That(File.Exists(originalPath), Is.True);
            Assert.That(updated.Text, Is.EqualTo(model.Text.Value));
            Assert.That(updated.Settings.SpeedScale, Is.EqualTo(1.4));
            Assert.That(JsonSerializer.Deserialize<AudioQueryModel>(updated.AudioQueryJson)!.SpeedScale, Is.EqualTo(1.4));
            Assert.That(element.Start.Value, Is.EqualTo(MediaTime.FromSeconds(3)));
            Assert.That(element.Length.Value, Is.EqualTo(MediaTime.FromSeconds(1)));
            Assert.That(sound.OffsetPosition.Value, Is.EqualTo(MediaTime.FromSeconds(0.2)));
            Assert.That(editor.History.Undo(), Is.True);
            Assert.That(editor.Scene.Children.Single(), Is.SameAs(element));
            Assert.That(controller.Generation.Value, Is.EqualTo(original));
            Assert.That(sound.Source.Value!.Uri.LocalPath, Is.EqualTo(originalPath));
            Assert.That(editor.History.Redo(), Is.True);
            Assert.That(editor.Scene.Children, Has.Count.EqualTo(2));
            Assert.That(addedController.Generation.Value, Is.EqualTo(updated));

            // The property editor owns its controller even when a different element is selected.
            editor.Selection.Value = added;
            var handEditedQuery = JsonSerializer.Deserialize<AudioQueryModel>(original.AudioQueryJson)!;
            handEditedQuery.AccentPhrases[0].Moras[0].Pitch = 5.5;
            original = original with { AudioQueryJson = JsonSerializer.Serialize(handEditedQuery) };
            controller.Generation.Value = original;
            editor.History.Commit();
            using var controllerModel = TtsControllerTests.CreateModel(editor, controller);
            controllerModel.SelectedPreset.Value = controllerModel.Presets.Value.Single();
            controllerModel.ApplyPreset();
            var applied = controller.AppliedPreset.Value!;
            Assert.That(applied.Settings.SpeedScale, Is.EqualTo(1.4));
            Assert.That(sound.Source.Value!.Uri.LocalPath, Is.EqualTo(originalPath));
            var voiceWindow = new Window { Width = 320, Height = 600,
                Content = new VoiceEditorView { DataContext = controllerModel } };
            try
            {
                voiceWindow.Show();
                Dispatcher.UIThread.RunJobs();
                var beforeRefresh = controller.Draft.Value;
                controllerModel.OnLoaded();
                Dispatcher.UIThread.RunJobs();
                Assert.That(controller.Draft.Value, Is.EqualTo(beforeRefresh));
                Assert.That(controllerModel.AudioQuery.Value, Is.Not.Null, "Refreshing voice metadata must retain edited accents.");
                Assert.That(controllerModel.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch, Is.EqualTo(5.5));
            }
            finally { voiceWindow.Close(); }
            model.DeletePreset();
            controllerModel.RefreshPresets();
            Assert.That(controllerModel.SelectedPreset.Value, Is.Null);
            controllerModel.SpeedScale.Value = 1.6;
            controllerModel.VolumeScale.Value = 0.7;
            controllerModel.ConfirmEdit();
            controllerModel.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch = 5.8;
            controllerModel.OnAccentPhrasesUpdated();
            element.IsLocked.Value = true;
            await controllerModel.Regenerate();
            Assert.That(controllerModel.Message.Value, Does.Contain("ロックされています"));
            Assert.That(sound.Source.Value!.Uri.LocalPath, Is.EqualTo(originalPath));
            Assert.That(controller.Generation.Value, Is.EqualTo(original));
            element.IsLocked.Value = false;
            await controllerModel.Regenerate();
            Assert.That(controllerModel.Message.Value, Is.EqualTo("音声を再生成しました。"));
            var regenerated = controller.Generation.Value!;
            string regeneratedPath = sound.Source.Value!.Uri.LocalPath;
            Assert.That(regeneratedPath, Is.Not.EqualTo(originalPath));
            Assert.That(File.Exists(originalPath), Is.True);
            Assert.That(regenerated.Settings.SpeedScale, Is.EqualTo(1.6));
            Assert.That(regenerated.Settings.VolumeScale, Is.EqualTo(0.7));
            Assert.That(JsonSerializer.Deserialize<AudioQueryModel>(regenerated.AudioQueryJson)!.AccentPhrases[0].Moras[0].Pitch,
                Is.EqualTo(5.8), "Regeneration uses the controller's manual pitch edits and tuning.");
            Assert.That(addedController.Generation.Value, Is.EqualTo(updated));
            Assert.That(added.Objects.OfType<SourceSound>().Single().Source.Value!.Uri.LocalPath, Is.EqualTo(updatedPath));
            Assert.That(element.Start.Value, Is.EqualTo(MediaTime.FromSeconds(3)));
            Assert.That(element.Length.Value, Is.EqualTo(MediaTime.FromSeconds(1)));
            Assert.That(sound.OffsetPosition.Value, Is.EqualTo(MediaTime.FromSeconds(0.2)));
            Assert.That(editor.History.Undo(), Is.True);
            Assert.That(controller.Generation.Value, Is.EqualTo(original));
            Assert.That(sound.Source.Value!.Uri.LocalPath, Is.EqualTo(originalPath));
            Assert.That(controller.AppliedPreset.Value, Is.EqualTo(applied));
            Assert.That(controllerModel.SpeedScale.Value, Is.EqualTo(1.6), "Undo regeneration retains the pending settings.");
            Assert.That(controllerModel.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch, Is.EqualTo(5.8));
            Assert.That(editor.History.Redo(), Is.True);
            Assert.That(controller.Generation.Value, Is.EqualTo(regenerated));
            Assert.That(sound.Source.Value!.Uri.LocalPath, Is.EqualTo(regeneratedPath));

            controller.Text.Value = "新しい文章を読み上げます。";
            Assert.That(controllerModel.AudioQuery.Value, Is.Null);
            await controllerModel.CreateQuery();
            Assert.That(controllerModel.HasAudioQuery.Value, Is.True);
            controllerModel.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch = 6.0;
            controllerModel.OnAccentPhrasesUpdated();
            await controllerModel.Regenerate();
            Assert.That(controllerModel.Message.Value, Is.EqualTo("音声を再生成しました。"));
            Assert.That(controller.Generation.Value!.Text, Is.EqualTo(controller.Text.Value));
            Assert.That(controller.Generation.Value!.AudioQueryJson, Is.Not.EqualTo(regenerated.AudioQueryJson),
                "A changed text must be analyzed again, not synthesized with the previous accents.");
            Assert.That(JsonSerializer.Deserialize<AudioQueryModel>(controller.Generation.Value.AudioQueryJson)!
                .AccentPhrases[0].Moras[0].Pitch, Is.EqualTo(6.0));
            regenerated = controller.Generation.Value;
            regeneratedPath = sound.Source.Value!.Uri.LocalPath;

            controllerModel.SelectedPreset.Value = applied with { Settings = applied.Settings with { SpeakerUuid = "missing-speaker" } };
            controllerModel.ApplyPreset();
            await controllerModel.Regenerate();
            Assert.That(controllerModel.Message.Value, Does.Contain("保存された声が見つかりません"));
            Assert.That(controller.Generation.Value, Is.EqualTo(regenerated));
            Assert.That(sound.Source.Value!.Uri.LocalPath, Is.EqualTo(regeneratedPath));

            // Legacy elements can also select a voice directly, without a preset.
            controller.Generation.Value = null;
            controller.AppliedPreset.Value = null;
            controller.Draft.Value = null;
            controllerModel.SpeedScale.Value = 1.5;
            controllerModel.PitchScale.Value = 0.05;
            controllerModel.ConfirmEdit();
            var chosenVoice = controllerModel.Voice.Value.First(x => x.Styles.Length > 1);
            controllerModel.SelectedVoice.Value = chosenVoice;
            controllerModel.SelectedStyle.Value = chosenVoice.Styles.Last();
            var pending = controller.Draft.Value!;
            var restored = (TtsController)CoreSerializer.DeserializeFromJsonObject(CoreSerializer.SerializeToJsonObject(controller), typeof(TtsController));
            using (var reopened = TtsControllerTests.CreateModel(editor, restored))
            {
                reopened.OnLoaded();
                Assert.That(reopened.SelectedVoice.Value!.SpeakerUuid, Is.EqualTo(chosenVoice.SpeakerUuid));
                Assert.That(reopened.SelectedStyle.Value!.Id, Is.EqualTo(chosenVoice.Styles.Last().Id));
                Assert.That(reopened.HasSettings.Value, Is.True);
                Assert.That(reopened.SpeedScale.Value, Is.EqualTo(1.5));
                Assert.That(reopened.PitchScale.Value, Is.EqualTo(0.05));
                Assert.That(restored.Draft.Value, Is.EqualTo(pending), "Refreshing metadata preserves the reopened draft.");
                Assert.That(restored.Generation.Value, Is.Null);
            }
            await controllerModel.Regenerate();
            Assert.That(controllerModel.Message.Value, Is.EqualTo("音声を再生成しました。"));
            Assert.That(controller.Generation.Value!.Text, Is.EqualTo(controller.Text.Value));
            Assert.That(addedController.Generation.Value, Is.EqualTo(updated));
        }
        finally
        {
            TtsLoader.VoiceVoxLoader.Value = null;
            loader.Unload();
            Environment.SetEnvironmentVariable("BEUTL_HOME", previousHome);
            Directory.Delete(directory, recursive: true);
        }
    }
}
