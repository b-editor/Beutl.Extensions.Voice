using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.Serialization;
using FluentAvalonia.UI.Controls;

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
    public void Presets_restore_every_value_by_id_and_are_shared_with_unsaved_scenes()
    {
        using var editor = Editor();
        editor.Scene.Uri = null;
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
        using var reopened = Model(editor);
        Assert.That(reopened.Presets.Value.Single(), Is.EqualTo(preset));
        Assert.That(File.Exists(Path.Combine(_directory, "voice", "presets.json")), Is.True);
        Assert.That(Directory.GetFiles(_directory, "*.voice-presets.json"), Is.Empty);
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
    public void Generation_round_trips_independently_of_preset_updates_and_retains_the_final_query()
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
        var query = JsonSerializer.Deserialize<AudioQueryModel>(restored.Generation.Value.AudioQueryJson)!;
        Assert.That(query.AccentPhrases[0].Moras[0].Pitch, Is.EqualTo(5.5));
        Assert.That(restored.Generation.Value.PresetName, Is.EqualTo("生成したときの声"));
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
        model.PresetName.Value = "共通の声";
        model.AddPreset();
        var json = new JsonObject();
        model.WriteToJson(json);
        Assert.That(json.ContainsKey("PresetScope"), Is.False);
        json["PresetScope"] = 1; // Old tab state must not switch away from the global store.
        using var restored = Model(editor);
        restored.SelectedVoice.Value = restored.Voice.Value[0];
        restored.SpeedScale.Value = 0.5;
        restored.OnLoaded();
        restored.ReadFromJson(json);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.That(restored.SelectedVoice.Value!.SpeakerUuid, Is.EqualTo("speaker-b"));
        Assert.That(restored.SpeedScale.Value, Is.EqualTo(1.3));
        Assert.That(restored.SelectedPreset.Value!.Id, Is.EqualTo(model.SelectedPreset.Value!.Id));
    }

    [AvaloniaTest]
    public async Task Preset_menu_opens_name_input_and_exposes_management_and_details()
    {
        using var editor = Editor();
        using var model = Model(editor);
        var view = new TtsTabView { DataContext = model };
        var window = new Window { Width = 320, Height = 600, Content = view };
        try
        {
            window.Show();
            var button = view.FindControl<Button>("PresetMenuButton")!;
            var menu = (FAMenuFlyout)button.Flyout!;
            menu.ShowAt(button);
            await Settle();
            var items = menu.Items.OfType<FAMenuFlyoutItem>().ToArray();
            Assert.That(items.Single(x => x.Text == "削除").IsEnabled, Is.False);
            Capture(menu, "preset-menu");
            menu.Hide();
            items.Single(x => x.Text == "現在の設定を保存…").RaiseEvent(new RoutedEventArgs(FAMenuFlyoutItem.ClickEvent));
            await Settle();
            var inputFlyout = (Flyout)FlyoutBase.GetAttachedFlyout(button)!;
            var input = ((StackPanel)inputFlyout.Content!).Children.OfType<TextBox>().Single();
            input.Text = "会話の声";
            Capture(inputFlyout, "preset-name");
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Settle();
            Assert.That(inputFlyout.IsOpen, Is.False);
            Assert.That(model.Presets.Value.Single().Name, Is.EqualTo("会話の声"));

            menu.ShowAt(button);
            await Settle();
            Assert.That(items.Single(x => x.Text == "名前を変更…").IsEnabled, Is.True);
            Assert.That(items.Single(x => x.Text == "複製").Command, Is.Not.Null);
            model.SpeedScale.Value = 1.2;
            items.Single(x => x.Text == "現在の設定で更新").Command!.Execute(null);
            Assert.That(model.SelectedPreset.Value!.Settings.SpeedScale, Is.EqualTo(1.2));
            menu.Hide();
            items.Single(x => x.Text == "保存値との差分…").RaiseEvent(new RoutedEventArgs(FAMenuFlyoutItem.ClickEvent));
            await Settle();
            var detailsFlyout = (Flyout)FlyoutBase.GetAttachedFlyout(button)!;
            Assert.That(model.SelectedPreset.Value?.Name, Is.EqualTo("会話の声"));
            Assert.That(model.PresetDetails.Value, Is.Not.Empty);
            Assert.That(((StackPanel)detailsFlyout.Content!).GetVisualDescendants().OfType<TextBlock>()
                .Any(x => x.Text == model.PresetDetails.Value), Is.True);
            Capture(detailsFlyout, "preset-details");
            detailsFlyout.Hide();
        }
        finally { window.Close(); }

        static void Capture(PopupFlyoutBase flyout, string name)
        {
            if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is not { } directory) return;
            Directory.CreateDirectory(directory);
            using var frame = TopLevel.GetTopLevel(flyout.Popup.Child)!.CaptureRenderedFrame()!;
            frame.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions());
        }
    }

    [AvaloniaTest]
    [TestCase(320, false)]
    [TestCase(460, true)]
    public async Task Editor_keeps_actions_visible_and_tuning_sliders_inside_their_rows(int width, bool light)
    {
        using var editor = Editor();
        using var model = Model(editor);
        model.PresetName.Value = "いつものナレーター";
        model.AddPreset();
        model.SpeedScale.Value = 1.1;
        var view = new TtsTabView { DataContext = model };
        var window = new Window { Width = width, Height = 600, Content = view,
            RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        try
        {
            window.Show();
            await Settle();
            var text = view.FindControl<TextBox>("SpeechTextBox")!;
            var preset = view.FindControl<ComboBox>("PresetSelector")!;
            Assert.That(text.TranslatePoint(default, view)!.Value.Y, Is.LessThan(preset.TranslatePoint(default, view)!.Value.Y));
            var apply = view.FindControl<Button>("ApplyPresetButton")!;
            apply.Command!.Execute(null);
            await Settle();
            Assert.That(model.SpeedScale.Value, Is.EqualTo(1.3));
            var add = view.FindControl<Button>("AddSpeechButton")!;
            var preview = view.FindControl<Button>("PreviewButton")!;
            double footerY = add.TranslatePoint(default, window)!.Value.Y;
            Assert.That(footerY, Is.InRange(540, 580));
            Assert.That(add.IsEffectivelyVisible, Is.True);
            Capture("editor");

            var tuning = view.FindControl<Expander>("TuningExpander")!;
            tuning.IsExpanded = true;
            await Settle();
            tuning.BringIntoView();
            await Settle();
            Capture("tuning");
            var sliders = tuning.GetVisualDescendants().OfType<Slider>().ToArray();
            Assert.That(sliders, Has.Length.EqualTo(6));
            foreach (var slider in sliders)
            {
                var row = (Grid)slider.Parent!;
                var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
                var border = thumb.GetVisualDescendants().OfType<Border>().First();
                var origin = border.TranslatePoint(default, row)!.Value;
                Assert.That(origin.Y, Is.GreaterThanOrEqualTo(0));
                Assert.That(origin.Y + border.Bounds.Height, Is.LessThanOrEqualTo(row.Bounds.Height),
                    "The slider thumb must fit inside its row, including its outer border.");
            }

            // Expanding and scrolling the accent editor must not move the main actions.
            model.AudioQuery.Value = new AudioQueryModel();
            model.HasAudioQuery.Value = true;
            view.FindControl<Expander>("AccentExpander")!.IsExpanded = true;
            await Settle();
            view.FindControl<ScrollViewer>("EditorScroll")!.Offset = new Vector(0, 10000);
            await Settle();
            Assert.That(view.FindControl<ScrollViewer>("EditorScroll")!.Offset.Y, Is.GreaterThan(0));
            Assert.That(add.TranslatePoint(default, window)!.Value.Y, Is.EqualTo(footerY));

            model.IsGenerating.Value = true;
            await Settle();
            Assert.That(preview.IsEffectivelyEnabled, Is.False);
            Assert.That(add.IsEffectivelyEnabled, Is.False);
            model.IsGenerating.Value = false;
            await Settle();
            Assert.That(add.IsEffectivelyEnabled, Is.True);

            void Capture(string name)
            {
                if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is not { } directory) return;
                Directory.CreateDirectory(directory);
                using var frame = window.CaptureRenderedFrame()!;
                frame.Save(Path.Combine(directory, $"{name}-{width}-{(light ? "light" : "dark")}.png"), new PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); }
    }

    private static async Task Settle()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(250);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
    }
}
