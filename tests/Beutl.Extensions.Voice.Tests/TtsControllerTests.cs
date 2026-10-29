using System.Text.Json.Nodes;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Beutl.Audio;
using Beutl.Controls.PropertyEditors;
using Beutl.Editor.Observers;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.ProjectSystem;
using Beutl.Serialization;

namespace Beutl.Extensions.Voice.Tests;

public class TtsControllerTests
{
    private string _directory = null!;
    private string? _previousHome;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "tts-controller-" + Guid.NewGuid());
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

    internal static TtsControllerViewModel CreateModel(TestEditor editor, TtsController controller)
    {
        var inner = new TextContext(controller);
        var model = new TtsControllerViewModel(inner, new CustomPropertyEditorExtension(), controller);
        model.Accept(new ServiceVisitor(editor));
        return model;
    }

    private static TtsController AddController(TestEditor editor, VoiceGeneration? generation = null)
    {
        var controller = new TtsController { Text = { Value = "この要素の音声を作り直します。" }, Generation = { Value = generation } };
        var element = new Element { Length = { Value = MediaTime.FromSeconds(1) } };
        element.AddObject(new SourceSound());
        element.AddObject(controller);
        editor.Scene.AddChild(element, ElementOverlapHandling.ZIndex);
        return controller;
    }

    private static VoicePreset Preset() => new()
    {
        Name = "会話の声",
        Settings = new VoiceSettings
        {
            SpeakerUuid = "speaker", SpeakerName = "話者A", StyleId = 1,
            StyleName = "通常", ModelVersion = "1", SpeedScale = 1.3, PitchScale = 0.05
        }
    };

    private static void SetVoices(TtsControllerViewModel model)
    {
        model.Voice.Value = [new VoiceMetadata
        {
            Name = "話者A", SpeakerUuid = "speaker", Version = "1",
            Styles = [new VoiceStyle { Id = 1, Name = "通常" }, new VoiceStyle { Id = 2, Name = "明るい" }]
        }];
        model.IsEnabled.Value = true;
    }

    private static AudioQueryModel Query() => new()
    {
        AccentPhrases = [new AccentPhraseModel
        {
            Accent = 2, Moras = [new MoraModel { Text = "コ", Vowel = "o", VowelLength = 0.1, Pitch = 5.1 },
                new MoraModel { Text = "ン", Vowel = "N", VowelLength = 0.1, Pitch = 5.5 },
                new MoraModel { Text = "ニ", Vowel = "i", VowelLength = 0.1, Pitch = 5.2 },
                new MoraModel { Text = "チ", Vowel = "i", VowelLength = 0.1, Pitch = 5.0 },
                new MoraModel { Text = "ワ", Vowel = "a", VowelLength = 0.1, Pitch = 4.9 }]
        }]
    };

    [AvaloniaTest]
    public void Detailed_edits_persist_and_undo_without_mutating_the_generated_query()
    {
        using var editor = new TestEditor(Path.Combine(_directory, "scene.bescene"));
        var preset = Preset();
        var original = new VoiceGeneration
        {
            Text = "この要素の音声を作り直します。", Settings = preset.Settings,
            AudioQueryJson = JsonSerializer.Serialize(Query())
        };
        var controller = AddController(editor, original);
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        using var model = CreateModel(editor, controller);
        SetVoices(model);
        model.SelectedPreset.Value = preset;
        model.ApplyPreset();
        int before = editor.History.UndoCount;
        model.SpeedScale.Value = 1.4;
        model.SpeedScale.Value = 1.5;
        model.PitchScale.Value = -0.05;
        model.IntonationScale.Value = 0.8;
        model.VolumeScale.Value = 0.6;
        model.PrePhonemeLength.Value = 0.2;
        model.PostPhonemeLength.Value = 0.3;
        model.ConfirmEdit();
        Assert.That(editor.History.UndoCount, Is.EqualTo(before + 1));
        Assert.That(controller.Generation.Value, Is.EqualTo(original));
        Assert.That(controller.AppliedPreset.Value, Is.EqualTo(preset));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(model.SpeedScale.Value, Is.EqualTo(1.3));
        Assert.That(model.VolumeScale.Value, Is.EqualTo(1));
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(model.SpeedScale.Value, Is.EqualTo(1.5));

        model.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch = 6.1;
        model.AudioQuery.Value.AccentPhrases[0].Accent = 3;
        model.OnAccentPhrasesUpdated();
        Assert.That(controller.Generation.Value!.AudioQueryJson, Is.EqualTo(original.AudioQueryJson));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(model.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch, Is.EqualTo(5.1));
        Assert.That(editor.History.Redo(), Is.True);
        var restored = (TtsController)CoreSerializer.DeserializeFromJsonObject(CoreSerializer.SerializeToJsonObject(controller), typeof(TtsController));
        using var reopened = CreateModel(editor, restored);
        Assert.That(new[] { reopened.SpeedScale.Value, reopened.PitchScale.Value, reopened.IntonationScale.Value,
            reopened.VolumeScale.Value, reopened.PrePhonemeLength.Value, reopened.PostPhonemeLength.Value },
            Is.EqualTo(new[] { 1.5, -0.05, 0.8, 0.6, 0.2, 0.3 }));
        Assert.That(reopened.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch, Is.EqualTo(6.1));
        Assert.That(reopened.AudioQuery.Value.AccentPhrases[0].Accent, Is.EqualTo(3));

        controller.Text.Value = "別の本文";
        editor.History.Commit();
        Assert.That(model.AudioQuery.Value, Is.Null);
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(model.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch, Is.EqualTo(6.1));
        model.SelectedStyle.Value = model.SelectedVoice.Value!.Styles[1];
        Assert.That(model.AudioQuery.Value, Is.Null);
        Assert.That(controller.Draft.Value!.Settings.StyleId, Is.EqualTo(2));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(model.SelectedStyle.Value!.Id, Is.EqualTo(1));
        Assert.That(model.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch, Is.EqualTo(6.1));

        controller.FindHierarchicalParent<Element>()!.IsLocked.Value = true;
        model.SpeedScale.Value = 1.8;
        Assert.That(model.SpeedScale.Value, Is.EqualTo(1.5));
        Assert.That(model.Message.Value, Does.Contain("ロックされています"));
    }

    [AvaloniaTest]
    public void Applying_a_preset_is_persistent_undoable_and_keeps_generation_independent()
    {
        using var editor = new TestEditor(Path.Combine(_directory, "scene.bescene"));
        var preset = Preset();
        var original = new VoiceGeneration { Text = "元の本文", Settings = preset.Settings with { SpeedScale = 1 } };
        var controller = AddController(editor, original);
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
        using var model = CreateModel(editor, controller);
        model.SelectedPreset.Value = model.Presets.Value.Single();
        model.ApplyPreset();
        Assert.That(controller.AppliedPreset.Value, Is.EqualTo(preset));
        Assert.That(controller.Generation.Value, Is.EqualTo(original));
        var restored = (TtsController)CoreSerializer.DeserializeFromJsonObject(CoreSerializer.SerializeToJsonObject(controller), typeof(TtsController));
        Assert.That(restored.AppliedPreset.Value, Is.EqualTo(preset));
        Assert.That(restored.Generation.Value, Is.EqualTo(original));

        VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset with { Settings = preset.Settings with { SpeedScale = 1.8 } });
        VoicePresetStore.Delete(VoicePresetStore.GlobalPath, preset.Id);
        model.RefreshPresets();
        Assert.That(model.SelectedPreset.Value, Is.Null);
        Assert.That(model.HasSettings.Value, Is.True);
        Assert.That(controller.AppliedPreset.Value!.Settings.SpeedScale, Is.EqualTo(1.3));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(controller.AppliedPreset.Value, Is.Null);
        Assert.That(model.SpeedScale.Value, Is.EqualTo(original.Settings.SpeedScale));
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(controller.AppliedPreset.Value, Is.EqualTo(preset));
        Assert.That(model.GenerationSettings.Value, Does.Contain("元の本文"));

        var element = controller.FindHierarchicalParent<Element>()!;
        element.IsLocked.Value = true;
        model.SelectedPreset.Value = preset with { Settings = preset.Settings with { SpeedScale = 1.8 } };
        model.ApplyPreset();
        Assert.That(model.Message.Value, Does.Contain("ロックされています"));
        Assert.That(controller.AppliedPreset.Value, Is.EqualTo(preset));
        element.IsLocked.Value = false;
        model.ApplyPreset();
        Assert.That(controller.AppliedPreset.Value!.Settings.SpeedScale, Is.EqualTo(1.8));
    }

    [AvaloniaTest]
    [TestCase(320, false)]
    [TestCase(460, true)]
    public async Task Controller_editor_applies_presets_and_renders_current_and_generated_settings(int width, bool light)
    {
        using var editor = new TestEditor(Path.Combine(_directory, "scene.bescene"));
        var preset = Preset();
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
        var controller = AddController(editor);
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        using var model = CreateModel(editor, controller);
        SetVoices(model);
        var inner = (TextContext)model.Inner;
        var textEditor = new StringEditor { Header = nameof(TtsController.Text), DataContext = inner };
        inner.Accept(textEditor);
        textEditor.Connect(inner);
        var view = new TtsControllerView { DataContext = model, Inner = { Content = textEditor } };
        var window = new Window { Width = width, Height = 600, Content = new ScrollViewer { Content = view },
            RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        try
        {
            window.Show();
            await Settle();
            var regenerate = view.FindControl<Button>("RegenerateButton")!;
            Assert.That(regenerate.IsEffectivelyEnabled, Is.False, "Legacy elements need a preset before their first regeneration.");
            view.FindControl<ComboBox>("PresetSelector")!.SelectedIndex = 0;
            view.FindControl<Button>("ApplyPresetButton")!.Command!.Execute(null);
            await Settle();
            Assert.That(controller.AppliedPreset.Value, Is.EqualTo(preset));
            Assert.That(regenerate.IsEffectivelyEnabled, Is.True);
            Assert.That(regenerate.Command, Is.Not.Null);
            controller.Generation.Value = new VoiceGeneration
            {
                Text = controller.Text.Value, PresetName = "前回の声",
                Settings = preset.Settings with { SpeedScale = 1.0, PitchScale = 0 }
            };
            view.FindControl<Expander>("SettingsExpander")!.IsExpanded = true;
            await Settle();
            Assert.That(textEditor.Text, Is.EqualTo(controller.Text.Value));
            Assert.That(model.SpeedScale.Value, Is.EqualTo(1.3));
            Assert.That(model.GenerationSettings.Value, Does.Contain("前回の声"));
            if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is { } directory)
            {
                Capture("controller", directory);
            }

            var voiceEditor = view.FindControl<VoiceEditorView>("VoiceEditor")!;
            view.FindControl<Expander>("SettingsExpander")!.IsExpanded = false;
            var tuning = voiceEditor.FindControl<Expander>("TuningExpander")!;
            tuning.IsExpanded = true;
            await Settle();
            tuning.BringIntoView();
            await Settle();
            var sliders = tuning.GetVisualDescendants().OfType<Slider>().ToArray();
            Assert.That(sliders, Has.Length.EqualTo(6));
            sliders[0].Value = 1.45;
            model.ConfirmEdit();
            Assert.That(controller.Draft.Value!.Settings.SpeedScale, Is.EqualTo(1.45));
            foreach (var slider in sliders)
            {
                var row = (Grid)slider.Parent!;
                var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
                var border = thumb.GetVisualDescendants().OfType<Border>().First();
                double top = border.TranslatePoint(default, row)!.Value.Y;
                Assert.That(top, Is.GreaterThanOrEqualTo(0));
                Assert.That(top + border.Bounds.Height, Is.LessThanOrEqualTo(row.Bounds.Height));
            }
            int beforeDrag = editor.History.UndoCount;
            var sliderThumb = sliders[0].GetVisualDescendants().OfType<Thumb>().Single();
            var start = sliderThumb.TranslatePoint(new Point(sliderThumb.Bounds.Width / 2, sliderThumb.Bounds.Height / 2), window)!.Value;
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Vector(20, 0), RawInputModifiers.LeftMouseButton);
            window.MouseMove(start + new Vector(30, 0), RawInputModifiers.LeftMouseButton);
            window.MouseUp(start + new Vector(30, 0), MouseButton.Left);
            await Settle();
            Assert.That(controller.Draft.Value!.Settings.SpeedScale, Is.Not.EqualTo(1.45));
            Assert.That(editor.History.UndoCount, Is.EqualTo(beforeDrag + 1), "A slider drag is one history entry.");
            Assert.That(editor.History.Undo(), Is.True);
            Assert.That(model.SpeedScale.Value, Is.EqualTo(1.45));
            if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is { } tuningDirectory) Capture("controller-tuning", tuningDirectory);
            tuning.IsExpanded = false;
            model.AudioQuery.Value = Query();
            model.OnAccentPhrasesUpdated();
            var accents = voiceEditor.FindControl<Expander>("AccentExpander")!;
            accents.IsExpanded = true;
            await Settle();
            accents.BringIntoView();
            await Settle();
            var panel = voiceEditor.FindControl<AccentPhrasePanel>("AccentPanel")!;
            Assert.That(panel.AccentPhrases, Has.Count.EqualTo(1));
            start = panel.TranslatePoint(new Point(18, 70), window)!.Value;
            var outside = panel.TranslatePoint(new Point(panel.Bounds.Width + 5, 110), window)!.Value;
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(outside, RawInputModifiers.LeftMouseButton);
            window.MouseUp(outside, MouseButton.Left);
            await Settle();
            Assert.That(JsonSerializer.Deserialize<AudioQueryModel>(controller.Draft.Value!.AudioQueryJson)!
                .AccentPhrases[0].Moras[0].Pitch, Is.Not.EqualTo(5.1), "Releasing outside the pitch panel still saves the edit.");
            Assert.That(editor.History.Undo(), Is.True);
            Assert.That(model.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch, Is.EqualTo(5.1));
            if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is { } accentDirectory) Capture("controller-accents", accentDirectory);
            model.IsGenerating.Value = true;
            await Settle();
            Assert.That(regenerate.IsEffectivelyEnabled, Is.False);
            Assert.That(textEditor.IsEffectivelyEnabled, Is.False);

            void Capture(string name, string directory)
            {
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

    private sealed class ServiceVisitor(TestEditor editor) : IPropertyEditorContextVisitor, IServiceProvider
    {
        public void Visit(IPropertyEditorContext context) { }
        public object? GetService(Type serviceType) => editor.GetService(serviceType);
    }

    // The default editor context belongs to Beutl's host, outside the public extension SDK.
    // Connect the real StringEditor to the engine property through its public context contract.
    private sealed class TextContext(TtsController controller) : IValueEditorContext<string>
    {
        public PropertyEditorExtension Extension => PropertyEditorExtension.Instance;
        public IObservable<string> EditValue => controller.Text.GetObservable();
        public string PreviewEdit(string value) => controller.Text.Value = value;
        public void ConfirmEdit(string oldValue, string newValue) => controller.Text.Value = newValue;
        public void Accept(IPropertyEditorContextVisitor visitor)
        {
            visitor.Visit(this);
            if (visitor is StringEditor editor) editor.Classes.Add("multiline");
        }
        public void WriteToJson(JsonObject json) { }
        public void ReadFromJson(JsonObject json) { }
        public void Dispose() { }
    }
}
