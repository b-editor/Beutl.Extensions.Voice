using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.ProjectSystem;
using Beutl.Serialization;

namespace Beutl.Extensions.Voice.Tests;

public class VoicePresetTests
{
    private string _directory = null!;
    private string? _previousHome;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "voice-presets-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _previousHome = Environment.GetEnvironmentVariable("BEUTL_HOME");
        Environment.SetEnvironmentVariable("BEUTL_HOME", _directory);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("BEUTL_HOME", _previousHome);
        Directory.Delete(_directory, recursive: true);
    }

    private TestEditor Editor(string name = "scene") => new(Path.Combine(_directory, name + ".bescene"));

    private static TtsTabViewModel Model(TestEditor editor)
    {
        var model = new TtsTabViewModel(new TtsTabExtension(), editor);
        model.Voice.Value =
        [
            new VoiceMetadata { Name = "同名の話者", SpeakerUuid = "speaker-a", Version = "1", Styles = [new VoiceStyle { Id = 1, Name = "通常" }] },
            new VoiceMetadata { Name = "同名の話者", SpeakerUuid = "speaker-b", Version = "2", Styles = [new VoiceStyle { Id = 2, Name = "通常" }] }
        ];
        model.SelectedVoice.Value = model.Voice.Value[1];
        model.IsEnabled.Value = true;
        model.Text.Value = "こんにちは。保存した声で読み上げます。";
        SetParameters(model);
        return model;
    }

    private static void SetParameters(TtsTabViewModel model)
    {
        model.SpeedScale.Value = 1.3;
        model.PitchScale.Value = -0.05;
        model.IntonationScale.Value = 0.8;
        model.VolumeScale.Value = 0.6;
        model.PrePhonemeLength.Value = 0.3;
        model.PostPhonemeLength.Value = 0.4;
    }

    [Test]
    public void Store_keeps_same_named_presets_distinct_and_does_not_overwrite_other_edits()
    {
        string path = Path.Combine(_directory, "presets.json");
        var first = new VoicePreset { Name = "ナレーター", Settings = new() { SpeakerUuid = "a" } };
        var second = first with { Id = Guid.NewGuid(), Settings = new() { SpeakerUuid = "b" } };
        VoicePresetStore.Save(path, first);
        VoicePresetStore.Save(path, second);
        VoicePresetStore.Save(path, first with { Name = "変更後", Settings = first.Settings with { SpeedScale = 1.5 } });
        var reloaded = VoicePresetStore.Load(path);
        Assert.That(reloaded.Single(x => x.Id == first.Id).Name, Is.EqualTo("変更後"));
        Assert.That(reloaded.Single(x => x.Id == second.Id), Is.EqualTo(second));
        VoicePresetStore.Delete(path, first.Id);
        Assert.That(VoicePresetStore.Load(path), Is.EqualTo(new[] { second }));
        File.WriteAllText(path, "broken json");
        Assert.Throws<JsonException>(() => VoicePresetStore.Save(path, first));
        Assert.That(File.ReadAllText(path), Is.EqualTo("broken json"));
    }

    [AvaloniaTest]
    public void Presets_restore_every_value_by_id_and_support_shared_and_document_scopes()
    {
        using var editor = Editor();
        using var model = Model(editor);
        model.PresetName.Value = "いつもの声";
        model.AddPreset();
        var preset = model.SelectedPreset.Value!;
        model.SelectedVoice.Value = model.Voice.Value[0];
        model.SpeedScale.Value = 0.8;
        model.PitchScale.Value = 0.1;
        model.IntonationScale.Value = 1.2;
        model.VolumeScale.Value = 1.5;
        model.PrePhonemeLength.Value = 0;
        model.PostPhonemeLength.Value = 0;
        Assert.That(model.PresetDetails.Value, Does.Contain("1.3 → 0.8"));
        model.ApplyPreset();
        Assert.That(model.SelectedVoice.Value!.SpeakerUuid, Is.EqualTo("speaker-b"));
        Assert.That(model.SelectedStyle.Value!.Id, Is.EqualTo(2));
        Assert.That(new[] { model.SpeedScale.Value, model.PitchScale.Value, model.IntonationScale.Value, model.VolumeScale.Value, model.PrePhonemeLength.Value, model.PostPhonemeLength.Value },
            Is.EqualTo(new[] { 1.3, -0.05, 0.8, 0.6, 0.3, 0.4 }));
        model.DuplicatePreset();
        Assert.That(model.SelectedPreset.Value!.Id, Is.Not.EqualTo(preset.Id));
        model.PresetName.Value = preset.Name;
        model.RenamePreset();
        model.SpeedScale.Value = 1.1;
        model.UpdatePreset();
        Assert.That(model.Presets.Value.Single(x => x.Id == preset.Id).Settings.SpeedScale, Is.EqualTo(1.3));
        model.DeletePreset();
        using var otherEditor = Editor("other");
        using var other = Model(otherEditor);
        Assert.That(other.Presets.Value.Single(), Is.EqualTo(preset));
        model.PresetScope.Value = 1;
        model.PresetName.Value = "この作品だけ";
        model.AddPreset();
        other.PresetScope.Value = 1;
        Assert.That(other.Presets.Value, Is.Empty);
        using var reopened = Model(editor);
        reopened.PresetScope.Value = 1;
        Assert.That(reopened.Presets.Value.Single().Name, Is.EqualTo("この作品だけ"));

        // Scenes in the same project share the project's store, not their own sidecars.
        var project = new Project { Uri = new Uri(Path.Combine(_directory, "project.bep")) };
        project.Items.Add(editor.Scene);
        project.Items.Add(otherEditor.Scene);
        model.RefreshPresets();
        Assert.That(model.Presets.Value, Is.Empty);
        model.PresetName.Value = "プロジェクト共通";
        model.AddPreset();
        other.RefreshPresets();
        Assert.That(other.Presets.Value.Single().Id, Is.EqualTo(model.SelectedPreset.Value!.Id));
    }

    [AvaloniaTest]
    public void Missing_voice_does_not_fall_back_to_same_name_or_style_id_and_query_tracks_input()
    {
        using var editor = Editor();
        using var model = Model(editor);
        model.PresetName.Value = "声";
        model.AddPreset();
        var preset = model.SelectedPreset.Value!;
        model.AudioQuery.Value = new AudioQueryModel();
        model.HasAudioQuery.Value = true;
        model.SpeedScale.Value = 1.1;
        Assert.That(model.AudioQuery.Value, Is.Not.Null, "Parameter edits keep hand-edited accents.");
        model.Text.Value = "別の本文";
        Assert.That(model.AudioQuery.Value, Is.Null);
        model.AudioQuery.Value = new AudioQueryModel();
        model.SelectedVoice.Value = model.Voice.Value[0];
        Assert.That(model.AudioQuery.Value, Is.Null);
        model.SelectedPreset.Value = preset with { Settings = preset.Settings with { SpeakerUuid = "missing" } };
        model.ApplyPreset();
        Assert.That(model.SelectedVoice.Value, Is.Null);
        Assert.That(model.SelectedStyle.Value, Is.Null);
        Assert.That(model.PresetMessage.Value, Does.Contain("見つかりません"));
        model.SelectedPreset.Value = preset with { Settings = preset.Settings with { EngineId = "another-engine" } };
        model.ApplyPreset();
        Assert.That(model.SelectedVoice.Value, Is.Null);
        model.SelectedPreset.Value = preset;
        model.ApplyPreset();
        Assert.That(model.SelectedStyle.Value!.Id, Is.EqualTo(2));
    }

    [AvaloniaTest]
    public void Generation_round_trips_independently_of_preset_updates_and_restores_the_final_query()
    {
        using var editor = Editor();
        using var model = Model(editor);
        model.PresetName.Value = "生成したときの声";
        model.AddPreset();
        var preset = model.SelectedPreset.Value!;
        var generation = new VoiceGeneration
        {
            Text = model.Text.Value, Settings = preset.Settings, PresetId = preset.Id, PresetName = preset.Name,
            AudioQueryJson = JsonSerializer.Serialize(new AudioQueryModel
            {
                AccentPhrases = [new() { Accent = 1, Moras = [new() { Text = "ア", Vowel = "a", VowelLength = 0.2, Pitch = 5.5 }] }]
            })
        };
        var controller = new TtsController { Text = { Value = generation.Text }, Generation = { Value = generation } };
        var saved = CoreSerializer.SerializeToJsonObject(controller);
        var restored = (TtsController)CoreSerializer.DeserializeFromJsonObject(saved, typeof(TtsController));
        Assert.That(restored.Generation.Value, Is.EqualTo(generation));
        model.SpeedScale.Value = 1.6;
        model.UpdatePreset();
        model.DeletePreset();
        Assert.That(restored.Generation.Value!.Settings.SpeedScale, Is.EqualTo(1.3));
        var element = new Element { Name = { Value = "保存済みセリフ" } };
        element.AddObject(restored);
        editor.Selection.Value = element;
        model.LoadSelectedSpeech();
        Assert.That(model.SpeedScale.Value, Is.EqualTo(1.3));
        Assert.That(model.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch, Is.EqualTo(5.5));
        Assert.That(model.GenerationDetails.Value, Does.Contain("生成したときの声"));
        model.ClearEditingSpeech();
        Assert.That(model.HasEditingElement.Value, Is.False);
        var legacy = new TtsController();
        CoreSerializer.PopulateFromJsonObject(legacy, new JsonObject { ["Text"] = "旧セリフ" });
        Assert.That(legacy.Text.Value, Is.EqualTo("旧セリフ"));
        Assert.That(legacy.Generation.Value, Is.Null);
    }

    [AvaloniaTest]
    public async Task Tab_state_restores_ids_and_parameters_after_reopening()
    {
        using var editor = Editor();
        using var model = Model(editor);
        var json = new JsonObject();
        model.WriteToJson(json);
        using var restored = Model(editor);
        restored.SelectedVoice.Value = restored.Voice.Value[0];
        restored.SpeedScale.Value = 0.5;
        restored.OnLoaded();
        restored.ReadFromJson(json);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.That(restored.SelectedVoice.Value!.SpeakerUuid, Is.EqualTo("speaker-b"));
        Assert.That(restored.SpeedScale.Value, Is.EqualTo(1.3));
    }

    [AvaloniaTest]
    [TestCase(320, false)]
    [TestCase(460, true)]
    public async Task Preset_controls_render_in_narrow_and_wide_tabs(int width, bool light)
    {
        using var editor = Editor();
        using var model = Model(editor);
        model.PresetName.Value = "いつものナレーター";
        model.AddPreset();
        model.SpeedScale.Value = 1.1;
        var view = new TtsTabView { DataContext = model };
        var window = new Window { Width = width, Height = 1100, Content = view,
            RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            var apply = view.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "適用"));
            Assert.That(apply.Command, Is.Not.Null);
            apply.Command!.Execute(null);
            Assert.That(model.SpeedScale.Value, Is.EqualTo(1.3));
            Assert.That(apply.Bounds.Width, Is.GreaterThan(20));
            if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is { } directory)
            {
                Directory.CreateDirectory(directory);
                await Settle();
                Capture("presets");
                var element = new Element { Name = { Value = "ナレーション 01" } };
                element.AddObject(new TtsController
                {
                    Text = { Value = model.Text.Value },
                    Generation = { Value = new VoiceGeneration
                    {
                        Text = model.Text.Value, Settings = model.SelectedPreset.Value!.Settings,
                        PresetId = model.SelectedPreset.Value.Id, PresetName = model.SelectedPreset.Value.Name
                    } }
                });
                editor.Selection.Value = element;
                model.LoadSelectedSpeech();
                var expanders = view.GetVisualDescendants().OfType<Expander>().ToArray();
                expanders.Single(x => Equals(x.Header, "声のプリセット")).IsExpanded = false;
                expanders.Single(x => Equals(x.Header, "既存セリフの設定・再生成")).IsExpanded = true;
                expanders.Single(x => Equals(x.Header, "詳細パラメータ")).IsExpanded = true;
                await Settle();
                Assert.That(view.GetVisualDescendants().OfType<TextBlock>()
                    .Any(x => x.Text == model.GenerationDetails.Value && x.IsEffectivelyVisible), Is.True);
                Capture("regeneration");

                static async Task Settle()
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(350); // Let expander transitions and reactive bindings settle.
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
                }

                void Capture(string name)
                {
                    using var frame = window.CaptureRenderedFrame()!;
                    frame.Save(Path.Combine(directory, $"{name}-{width}-{(light ? "light" : "dark")}.png"), new PngBitmapEncoderOptions());
                }
            }
        }
        finally { window.Close(); }
    }
}
