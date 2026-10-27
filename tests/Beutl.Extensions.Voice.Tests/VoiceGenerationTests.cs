using System.Text.Json;
using Avalonia.Headless.NUnit;
using Beutl.Audio;
using Beutl.Editor.Observers;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;

namespace Beutl.Extensions.Voice.Tests;

public class VoiceGenerationTests
{
    [AvaloniaTest]
    public async Task Native_generation_and_regeneration_keep_snapshots_and_support_undo()
    {
        string? coreHome = Environment.GetEnvironmentVariable("VOICEVOX_TEST_HOME");
        if (coreHome == null) Assert.Ignore("Set VOICEVOX_TEST_HOME to an installed VOICEVOX Core directory.");
        string directory = Path.Combine(Path.GetTempPath(), "voice-generation-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
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
            model.PresetScope.Value = 1;
            model.PresetName.Value = "実音声のテスト";
            model.AddPreset();
            await model.Generate();
            Assert.That(editor.Scene.Children, Has.Count.EqualTo(1));
            var element = editor.Scene.Children.Single();
            var controller = element.Objects.OfType<TtsController>().Single();
            var sound = element.Objects.OfType<SourceSound>().Single();
            var original = controller.Generation.Value!;
            string originalPath = sound.Source.Value!.Uri.LocalPath;
            Assert.That(new FileInfo(originalPath).Length, Is.GreaterThan(44));
            element.Start.Value = MediaTime.FromSeconds(3);
            element.Length.Value = MediaTime.FromSeconds(1);
            sound.OffsetPosition.Value = MediaTime.FromSeconds(0.2);
            editor.History.Commit();
            editor.Selection.Value = element;
            model.LoadSelectedSpeech();
            model.SpeedScale.Value = 1.4;
            model.UpdatePreset();
            Assert.That(controller.Generation.Value, Is.EqualTo(original));
            Assert.That(sound.Source.Value.Uri.LocalPath, Is.EqualTo(originalPath));
            var start = element.Start.Value;
            var length = element.Length.Value;
            model.Text.Value = "本文も変更しました。";
            await model.Regenerate();
            var updated = controller.Generation.Value!;
            string updatedPath = sound.Source.Value!.Uri.LocalPath;
            Assert.That(updatedPath, Is.Not.EqualTo(originalPath));
            Assert.That(File.Exists(originalPath), Is.True);
            Assert.That(updated.Text, Is.EqualTo(model.Text.Value));
            Assert.That(updated.Settings.SpeedScale, Is.EqualTo(1.4));
            Assert.That(JsonSerializer.Deserialize<AudioQueryModel>(updated.AudioQueryJson)!.SpeedScale, Is.EqualTo(1.4));
            Assert.That(element.Start.Value, Is.EqualTo(start));
            Assert.That(element.Length.Value, Is.EqualTo(length));
            Assert.That(sound.OffsetPosition.Value, Is.EqualTo(MediaTime.FromSeconds(0.2)));
            Assert.That(editor.History.Undo(), Is.True);
            Assert.That(controller.Generation.Value, Is.EqualTo(original));
            Assert.That(sound.Source.Value!.Uri.LocalPath, Is.EqualTo(originalPath));
            Assert.That(model.GenerationDetails.Value, Is.EqualTo(original.Describe()));
            Assert.That(editor.History.Redo(), Is.True);
            Assert.That(controller.Generation.Value, Is.EqualTo(updated));
            Assert.That(sound.Source.Value!.Uri.LocalPath, Is.EqualTo(updatedPath));
            Assert.That(model.GenerationDetails.Value, Is.EqualTo(updated.Describe()));
            model.DeletePreset();
            Assert.That(controller.Generation.Value, Is.EqualTo(updated));
        }
        finally
        {
            TtsLoader.VoiceVoxLoader.Value = null;
            loader.Unload();
            Directory.Delete(directory, recursive: true);
        }
    }
}
