using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
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
        Assert.That(model.CurrentSettings.Value, Is.EqualTo(original.Settings.Describe()));
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
        using var model = CreateModel(editor, controller);
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
            Assert.That(model.CurrentSettings.Value, Does.Contain("1.3"));
            Assert.That(model.GenerationSettings.Value, Does.Contain("前回の声"));
            if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is { } directory)
            {
                Directory.CreateDirectory(directory);
                using var frame = window.CaptureRenderedFrame()!;
                frame.Save(Path.Combine(directory, $"controller-{width}-{(light ? "light" : "dark")}.png"), new PngBitmapEncoderOptions());
            }
            model.IsGenerating.Value = true;
            await Settle();
            Assert.That(regenerate.IsEffectivelyEnabled, Is.False);
            Assert.That(textEditor.IsEffectivelyEnabled, Is.False);
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
