using System.Text;
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
using Beutl.Audio;
using Beutl.Editor.Observers;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.ProjectSystem;

namespace Beutl.Extensions.Voice.Tests;

public class ScriptImportTests
{
    private const string Script = "案内役: こんにちは。\nゲスト： よろしくお願いします。\n案内役: 始めましょう。";
    private string _directory = null!;
    private string? _previousHome;
    private static readonly VoiceMetadata[] Voices =
    [
        new() { Name = "テスト話者A", SpeakerUuid = "a", Version = "1", Styles = [new() { Id = 1, Name = "通常" }, new() { Id = 2, Name = "ささやき" }] },
        new() { Name = "テスト話者B", SpeakerUuid = "b", Version = "1", Styles = [new() { Id = 3, Name = "通常" }] }
    ];

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "voice-script-" + Guid.NewGuid());
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

    private TestEditor Editor() => new(Path.Combine(_directory, "scene.bescene"));

    private static void AssignVoices(ScriptImportViewModel model)
    {
        for (int i = 0; i < model.Speakers.Count; i++)
        {
            var speaker = model.Speakers[i];
            speaker.Voice.Value = Voices;
            speaker.SelectedVoice.Value = Voices[i % Voices.Length];
            speaker.IsEnabled.Value = true;
        }
        model.IsVoiceVoxInstalled.Value = true;
    }

    private static VoiceSynthesisResult Wave(string text, VoiceSettings settings)
    {
        const int sampleRate = 24000;
        // An odd sample count catches placements that round durations to milliseconds or frames.
        int samples = text.Length * 1001;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2);
        writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
        writer.Write(new byte[samples * 2]);
        var query = new AudioQueryModel();
        settings.ApplyTo(query);
        return new(stream.ToArray(), query, JsonSerializer.Serialize(query), settings);
    }

    [Test]
    public void Parser_handles_line_endings_speaker_delimiters_and_plain_text()
    {
        Assert.That(ScriptLine.Parse("\r\n A : 最初: 続き\rB：次\n 名前なし \nA:\n", true), Is.EqualTo(new[]
        {
            new ScriptLine("A", "最初: 続き"), new ScriptLine("B", "次"),
            new ScriptLine("", "名前なし"), new ScriptLine("A", "")
        }));
        Assert.That(ScriptLine.Parse(" https://example.com \n12:30です。", false), Is.EqualTo(new[]
        {
            new ScriptLine("", "https://example.com"), new ScriptLine("", "12:30です。")
        }));
    }

    [AvaloniaTest]
    public async Task Unknown_speakers_keep_edits_and_assignments_across_retry_and_tab_restore()
    {
        using var editor = Editor();
        using var model = new ScriptImportViewModel(new(), editor, Wave);
        string path = Path.Combine(_directory, "script.txt");
        await File.WriteAllTextAsync(path, Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await using (var stream = File.OpenRead(path)) await model.ImportFile(stream);
        await model.Generate();
        Assert.That(model.Message.Value, Does.Contain("案内役").And.Contain("選択してください"));
        Assert.That(model.Lines, Has.Count.EqualTo(3));
        AssignVoices(model);
        var first = model.Speakers[0];
        first.SpeedScale.Value = 1.3;
        var preset = new VoicePreset { Name = "追加したプリセット", Settings = first.Settings! };
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
        model.RefreshPresets();
        first.SelectedPreset.Value = first.Presets.Value.Single();
        first.ApplyPreset();
        VoicePresetStore.Delete(VoicePresetStore.GlobalPath, preset.Id);
        model.RefreshPresets();
        Assert.That(first.SelectedPreset.Value, Is.Null);
        Assert.That(first.AppliedPreset, Is.EqualTo(preset), "Refreshing the list must retain the applied settings snapshot.");
        model.Lines[0].Text.Value = "編集済みのセリフ";
        model.Lines[0].Speaker.Value = "新しい話者";
        Assert.That(model.Speakers.Select(x => x.Name), Is.EqualTo(new[] { "新しい話者", "ゲスト", "案内役" }));
        model.Lines[0].Speaker.Value = "案内役";
        Assert.That(model.Speakers[0], Is.SameAs(first));
        model.StartSeconds.Value = 2.5;
        model.GapSeconds.Value = 0.35;
        model.FirstLayer.Value = 4;
        model.SeparateLayers.Value = true;
        var state = new JsonObject();
        model.WriteToJson(state);
        using var restored = new ScriptImportViewModel(new(), editor, Wave);
        restored.ReadFromJson(state);
        Assert.That(restored.RawText.Value, Is.EqualTo(Script));
        Assert.That(restored.Lines[0].Text.Value, Is.EqualTo("編集済みのセリフ"));
        Assert.That(restored.Speakers[0].Settings, Is.EqualTo(first.Settings));
        Assert.That(restored.Speakers[0].AppliedPreset, Is.EqualTo(preset));
        Assert.That(restored.StartSeconds.Value, Is.EqualTo(2.5));
        Assert.That(restored.GapSeconds.Value, Is.EqualTo(0.35));
        Assert.That(restored.FirstLayer.Value, Is.EqualTo(4));
        Assert.That(restored.SeparateLayers.Value, Is.True);
        model.Lines[1].Remove();
        Assert.That(model.Speakers.Single(), Is.SameAs(first));
        Assert.That(model.Lines.Select(x => x.Number.Value), Is.EqualTo(new[] { 1, 2 }));
        await model.Generate();
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(2), model.Message.Value);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Batch_uses_wav_durations_order_and_settings_and_is_one_undo(bool separateLayers)
    {
        var preset = new VoicePreset { Name = "案内用", Settings = new()
        {
            SpeakerUuid = "a", SpeakerName = Voices[0].Name, StyleId = 2, StyleName = "ささやき", ModelVersion = "1", SpeedScale = 1.25
        } };
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
        using var editor = Editor();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        using var model = new ScriptImportViewModel(new(), editor, Wave);
        model.RawText.Value = Script;
        model.ImportText();
        AssignVoices(model);
        model.Speakers[0].SelectedPreset.Value = model.Speakers[0].Presets.Value.Single();
        model.Speakers[0].ApplyPreset();
        model.StartSeconds.Value = 2.25;
        model.GapSeconds.Value = 0.15;
        model.FirstLayer.Value = 4;
        model.SeparateLayers.Value = separateLayers;
        await model.Generate();
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(3), model.Message.Value);
        var elements = editor.Scene.Children.ToArray();
        var start = MediaTime.FromSeconds(2.25);
        for (int i = 0; i < elements.Length; i++)
        {
            var element = elements[i];
            var generation = element.Objects.OfType<TtsController>().Single().Generation.Value!;
            Assert.That(generation.Text, Is.EqualTo(model.Lines[i].Text.Value));
            Assert.That(generation.Settings.SpeakerUuid, Is.EqualTo(i == 1 ? "b" : "a"));
            Assert.That(generation.Settings.StyleId, Is.EqualTo(i == 1 ? 3 : 2));
            if (i != 1)
            {
                Assert.That(generation.PresetId, Is.EqualTo(preset.Id));
                Assert.That(generation.Settings.SpeedScale, Is.EqualTo(1.25));
            }
            Assert.That(element.Start.Value, Is.EqualTo(start));
            Assert.That(element.ZIndex.Value, Is.EqualTo(separateLayers && i == 1 ? 5 : 4));
            Assert.That(element.TryGetOriginalDuration(out var actualDuration), Is.True);
            Assert.That(element.Length.Value, Is.EqualTo(actualDuration));
            Assert.That(actualDuration.ToSeconds(), Is.EqualTo(generation.Text.Length * 1001 / 24000d).Within(1e-8));
            start += actualDuration + MediaTime.FromSeconds(0.15);
        }
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Is.Empty);
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(editor.Scene.Children, Is.EqualTo(elements));
        Assert.That(elements.All(x => File.Exists(x.Objects.OfType<SourceSound>().Single().Source.Value!.Uri.LocalPath)), Is.True);
    }

    [AvaloniaTest]
    [TestCase("failure")]
    [TestCase("cancel")]
    [TestCase("overlap")]
    [TestCase("lock")]
    public async Task Failed_or_cancelled_batch_leaves_no_partial_elements_or_files_and_can_retry(string reason)
    {
        using var editor = Editor();
        var existing = new Element
        {
            Uri = new Uri(Path.Combine(_directory, "existing.belm")), Start = { Value = MediaTime.FromSeconds(0.3) },
            Length = { Value = MediaTime.FromSeconds(1) }, ZIndex = { Value = reason == "overlap" ? 1 : 9 }
        };
        editor.Scene.AddChild(existing);
        var layer = new TimelineLayer { ZIndex = { Value = 1 } };
        editor.Scene.Layers.Add(layer);
        var originalDuration = editor.Scene.Duration.Value;
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        var enteredSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSecond = new ManualResetEventSlim();
        int count = 0;
        using var model = new ScriptImportViewModel(new(), editor, (text, settings) =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                enteredSecond.SetResult();
                if (!releaseSecond.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                if (reason == "failure") throw new InvalidOperationException("engine failure");
            }
            return Wave(text, settings);
        });
        model.RawText.Value = Script;
        model.ImportText();
        AssignVoices(model);
        var generationTask = model.Generate();
        try
        {
            await Task.WhenAny(enteredSecond.Task, generationTask).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(enteredSecond.Task.IsCompleted, Is.True, model.Message.Value);
            if (reason == "cancel") model.Cancel();
            if (reason == "lock") layer.IsLocked.Value = true;
        }
        finally { releaseSecond.Set(); }
        await generationTask;
        Assert.That(model.Message.Value, Does.Contain(reason == "cancel" ? "中止" : "配置できません"));
        Assert.That(editor.Scene.Children.Single(), Is.SameAs(existing));
        Assert.That(editor.Scene.Duration.Value, Is.EqualTo(originalDuration));
        Assert.That(Directory.GetFiles(Path.Combine(_directory, "resources", "tts")), Is.Empty);
        Assert.That(model.RawText.Value, Is.EqualTo(Script));
        Assert.That(model.Lines, Has.Count.EqualTo(3));
        Assert.That(model.Speakers[0].AvailableSettings, Is.Not.Null);
        model.FirstLayer.Value = 3;
        await model.Generate();
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(4), model.Message.Value);
        Assert.That(editor.Scene.Children.First(), Is.SameAs(existing));
    }

    [AvaloniaTest]
    [TestCase(320, false)]
    [TestCase(460, true)]
    public async Task Tab_supports_editing_and_keeps_controls_inside_narrow_layout(int width, bool light)
    {
        using var editor = Editor();
        using var model = new ScriptImportViewModel(new(), editor, Wave);
        model.IsVoiceVoxInstalled.Value = true;
        var view = new ScriptImportView { DataContext = model };
        var window = new Window { Width = width, Height = 700, Content = view,
            RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        try
        {
            window.Show();
            await Settle();
            view.FindControl<TextBox>("ScriptTextBox")!.Text = Script;
            view.FindControl<Button>("ImportScriptButton")!.Command!.Execute(null);
            AssignVoices(model);
            await Settle();
            Capture("input");
            view.FindControl<Expander>("InputSection")!.IsExpanded = false;
            await Settle();
            var speakerSection = view.FindControl<Expander>("SpeakersSection")!;
            var voiceSelector = speakerSection.GetVisualDescendants().OfType<ComboBox>()
                .First(x => ReferenceEquals(x.ItemsSource, model.Speakers[0].Voice.Value));
            voiceSelector.SelectedItem = Voices[1];
            var styleSelector = speakerSection.GetVisualDescendants().OfType<ComboBox>()
                .First(x => ReferenceEquals(x.DataContext, model.Speakers[0]) && ReferenceEquals(x.ItemsSource, Voices[1].Styles));
            styleSelector.SelectedItem = Voices[1].Styles[0];
            Assert.That(model.Speakers[0].AvailableSettings!.SpeakerUuid, Is.EqualTo("b"));
            var preset = new VoicePreset { Name = "案内役の声", Settings = new()
            {
                SpeakerUuid = "a", SpeakerName = Voices[0].Name, StyleId = 2, StyleName = "ささやき", ModelVersion = "1", SpeedScale = 1.2
            } };
            VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
            model.RefreshPresets();
            var presetSelector = speakerSection.GetVisualDescendants().OfType<ComboBox>()
                .First(x => ReferenceEquals(x.ItemsSource, model.Speakers[0].Presets.Value));
            presetSelector.SelectedItem = model.Speakers[0].Presets.Value.Single();
            speakerSection.GetVisualDescendants().OfType<Button>()
                .First(x => Equals(x.Content, "適用") && ReferenceEquals(x.DataContext, model.Speakers[0])).Command!.Execute(null);
            Assert.That(model.Speakers[0].AvailableSettings, Is.EqualTo(preset.Settings));
            Capture("speakers");
            speakerSection.IsExpanded = false;
            await Settle();
            var firstText = view.FindControl<Expander>("LinesSection")!.GetVisualDescendants().OfType<TextBox>()
                .First(x => x.AcceptsReturn);
            firstText.Text = "画面から編集したセリフです。";
            Assert.That(model.Lines[0].Text.Value, Is.EqualTo(firstText.Text));
            var firstAlias = view.FindControl<Expander>("LinesSection")!.GetVisualDescendants().OfType<TextBox>()
                .First(x => !x.AcceptsReturn);
            firstAlias.Focus();
            firstAlias.Text = "未割り当て";
            firstText.Focus();
            await Settle();
            Assert.That(model.Lines[0].Speaker.Value, Is.EqualTo("未割り当て"));
            Assert.That(model.Speakers.Single(x => x.Name == "案内役").AvailableSettings, Is.EqualTo(preset.Settings));
            firstAlias.Focus();
            firstAlias.Text = "案内役";
            firstText.Focus();
            await Settle();
            Assert.That(model.Speakers[0].AvailableSettings, Is.EqualTo(preset.Settings));
            Capture("lines");
            view.FindControl<Expander>("LinesSection")!.IsExpanded = false;
            await Settle();
            view.FindControl<NumericUpDown>("GapInput")!.Value = 0.3m;
            Assert.That(model.GapSeconds.Value, Is.EqualTo(0.3));
            Capture("placement");
            var add = view.FindControl<Button>("GenerateScriptButton")!;
            Assert.That(add.IsEffectivelyVisible, Is.True);
            Assert.That(add.TranslatePoint(default, window)!.Value.Y + add.Bounds.Height, Is.LessThanOrEqualTo(700));
            add.Command!.Execute(null);
            for (int i = 0; model.IsGenerating.Value && i < 500; i++) await Task.Delay(10);
            Assert.That(model.IsGenerating.Value, Is.False);
            Assert.That(editor.Scene.Children, Has.Count.EqualTo(3), model.Message.Value);

            void Capture(string section)
            {
                foreach (var control in view.GetVisualDescendants().OfType<Control>()
                    .Where(x => x is TextBox or ComboBox or NumericUpDown && x.IsEffectivelyVisible))
                {
                    var origin = control.TranslatePoint(default, view)!.Value;
                    Assert.That(origin.X, Is.GreaterThanOrEqualTo(0), control.ToString());
                    Assert.That(origin.X + control.Bounds.Width, Is.LessThanOrEqualTo(width), control.ToString());
                }
                if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is not { } directory) return;
                Directory.CreateDirectory(directory);
                using var frame = window.CaptureRenderedFrame()!;
                frame.Save(Path.Combine(directory, $"script-{section}-{width}-{(light ? "light" : "dark")}.png"), new PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Native_engine_generates_two_speakers_in_script_order()
    {
        string? coreHome = Environment.GetEnvironmentVariable("VOICEVOX_TEST_HOME");
        if (coreHome == null) Assert.Ignore("Set VOICEVOX_TEST_HOME to an installed VOICEVOX Core directory.");
        var loader = new VoiceVoxLoader(coreHome!);
        try
        {
            await Task.Run(loader.Load);
            Assert.That(loader.IsLoaded, Is.True);
            TtsLoader.VoiceVoxLoader.Value = loader;
            using var editor = Editor();
            using var model = new ScriptImportViewModel(new(), editor);
            model.RawText.Value = "A:こんにちは。\nB:ありがとう。";
            model.ImportText();
            for (int i = 0; i < 2; i++) model.Speakers[i].SelectedVoice.Value = model.Speakers[i].Voice.Value[i];
            await model.Generate();
            Assert.That(editor.Scene.Children, Has.Count.EqualTo(2), model.Message.Value);
            var elements = editor.Scene.Children.ToArray();
            Assert.That(elements[1].Start.Value, Is.EqualTo(elements[0].Length.Value + MediaTime.FromSeconds(0.2)));
            var generations = elements.Select(x => x.Objects.OfType<TtsController>().Single().Generation.Value!).ToArray();
            Assert.That(generations.Select(x => x.Settings.SpeakerUuid).Distinct().Count(), Is.EqualTo(2));
            Assert.That(generations.Select(x => x.Text), Is.EqualTo(new[] { "こんにちは。", "ありがとう。" }));
            Assert.That(generations.All(x => JsonSerializer.Deserialize<AudioQueryModel>(x.AudioQueryJson)!.AccentPhrases.Count > 0), Is.True);
        }
        finally
        {
            TtsLoader.VoiceVoxLoader.Value = null;
            loader.Unload();
        }
    }

    private static async Task Settle()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(250);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
    }
}
