using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private static readonly VoicePreset[] Presets =
    [
        new() { Name = "案内役の声", Settings = new()
        {
            SpeakerUuid = "a", SpeakerName = "テスト話者A", StyleId = 2, StyleName = "ささやき", ModelVersion = "1",
            SpeedScale = 1.25, PitchScale = -0.05, IntonationScale = 0.8, VolumeScale = 0.6,
            PrePhonemeLength = 0.3, PostPhonemeLength = 0.4
        } },
        new() { Name = "ゲストの声", Settings = new()
        {
            SpeakerUuid = "b", SpeakerName = "テスト話者B", StyleId = 3, StyleName = "通常", ModelVersion = "1"
        } }
    ];

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "voice-script-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _previousHome = Environment.GetEnvironmentVariable("BEUTL_HOME");
        Environment.SetEnvironmentVariable("BEUTL_HOME", _directory);
        foreach (var preset in Presets) VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("BEUTL_HOME", _previousHome);
        Directory.Delete(_directory, recursive: true);
    }

    private TestEditor Editor() => new(Path.Combine(_directory, "scene.bescene"));

    private static void AssignPresets(ScriptImportViewModel model)
    {
        for (int i = 0; i < model.Speakers.Count; i++)
        {
            var speaker = model.Speakers[i];
            speaker.SelectedPreset.Value = speaker.Presets.Value.Single(x => x.Id == Presets[i % Presets.Length].Id);
        }
        model.IsVoiceVoxInstalled.Value = true;
    }

    private static Task<VoiceSynthesisResult> Wave(string text, VoiceSettings settings) => Task.FromResult(MakeWave(text, settings));

    private static VoiceSynthesisResult MakeWave(string text, VoiceSettings settings)
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
    public async Task Reimport_and_tab_restore_keep_source_script_and_preset_assignments()
    {
        using var editor = Editor();
        using var model = new ScriptImportViewModel(editor, Wave);
        string path = Path.Combine(_directory, "script.txt");
        await File.WriteAllTextAsync(path, Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await using (var stream = File.OpenRead(path)) await model.ImportFile(stream);
        await model.Generate();
        Assert.That(model.Message.Value, Does.Contain("案内役").And.Contain("選択してください"));
        Assert.That(model.Lines, Has.Count.EqualTo(3));
        AssignPresets(model);
        var first = model.Speakers[0];
        model.RawText.Value = Script.Replace("案内役", "新しい話者");
        model.ImportText();
        Assert.That(model.Speakers[0].SelectedPreset.Value, Is.Null);
        model.RawText.Value = Script.Replace("こんにちは。", "元の台本で修正したセリフ");
        model.ImportText();
        Assert.That(model.Speakers[0], Is.SameAs(first));
        Assert.That(first.SelectedPreset.Value, Is.EqualTo(Presets[0]));
        model.StartSeconds.Value = 2.5;
        model.GapSeconds.Value = 0.35;
        model.FirstLayer.Value = 4;
        model.SeparateLayers.Value = true;
        var state = new JsonObject();
        model.WriteToJson(state);
        using var restored = new ScriptImportViewModel(editor, Wave);
        restored.ReadFromJson(state);
        Assert.That(restored.RawText.Value, Is.EqualTo(model.RawText.Value));
        Assert.That(restored.Lines[0].Text, Is.EqualTo("元の台本で修正したセリフ"));
        Assert.That(restored.Speakers[0].SelectedPreset.Value, Is.EqualTo(first.SelectedPreset.Value));
        Assert.That(restored.StartSeconds.Value, Is.EqualTo(2.5));
        Assert.That(restored.GapSeconds.Value, Is.EqualTo(0.35));
        Assert.That(restored.FirstLayer.Value, Is.EqualTo(4));
        Assert.That(restored.SeparateLayers.Value, Is.True);
        model.RawText.Value = string.Join('\n', model.RawText.Value.Split('\n').Where(x => x.StartsWith("案内役")));
        model.ImportText();
        Assert.That(model.Speakers.Single(), Is.SameAs(first));
        Assert.That(model.Lines, Has.Count.EqualTo(2));
        await model.Generate();
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(2), model.Message.Value);
    }

    [AvaloniaTest]
    public async Task Unavailable_preset_assignment_survives_save_restore_and_refresh()
    {
        using var editor = Editor();
        using var model = new ScriptImportViewModel(editor, Wave);
        model.RawText.Value = Script;
        model.ImportText();
        AssignPresets(model);
        var state = new JsonObject();
        model.WriteToJson(state);

        VoicePresetStore.Delete(VoicePresetStore.GlobalPath, Presets[0].Id);
        using var restored = new ScriptImportViewModel(editor, Wave);
        restored.ReadFromJson(state);
        restored.RefreshPresets();
        Assert.That(restored.Speakers[0].SelectedPreset.Value, Is.Null);
        await restored.Generate();
        Assert.That(editor.Scene.Children, Is.Empty, "Unavailable presets must not generate using their old settings.");
        restored.WriteToJson(state);

        using var reopened = new ScriptImportViewModel(editor, Wave);
        reopened.ReadFromJson(state);
        var updated = Presets[0] with { Settings = Presets[0].Settings with { SpeedScale = 1.4 } };
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, updated);
        reopened.RefreshPresets();
        Assert.That(reopened.Speakers[0].SelectedPreset.Value, Is.EqualTo(updated));

        VoicePresetStore.Delete(VoicePresetStore.GlobalPath, updated.Id);
        reopened.RefreshPresets();
        reopened.Speakers[0].SelectedPreset.Value = Presets[1];
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, updated);
        reopened.RefreshPresets();
        Assert.That(reopened.Speakers[0].SelectedPreset.Value, Is.EqualTo(Presets[1]));
        reopened.Speakers[0].SelectedPreset.Value = null;
        reopened.WriteToJson(state);
        restored.ReadFromJson(state);
        restored.RefreshPresets();
        Assert.That(restored.Speakers[0].SelectedPreset.Value, Is.Null, "Explicitly cleared assignments must stay cleared.");
    }

    [AvaloniaTest]
    public async Task Single_utterance_cleans_up_failed_creation_and_keeps_successful_audio_for_undo_redo()
    {
        using var editor = Editor();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        bool emptyAudio = true;
        using var model = new TtsTabViewModel(new(), editor,
            (text, settings, _) => Wave(emptyAudio ? "" : text, settings));
        var settings = Presets[0].Settings;
        model.SelectedVoice.Value = new VoiceMetadata
        {
            Name = settings.SpeakerName, SpeakerUuid = settings.SpeakerUuid, Version = settings.ModelVersion,
            Styles = [new VoiceStyle { Id = settings.StyleId, Name = settings.StyleName }]
        };
        model.Text.Value = "こんにちは。";
        await model.Generate();
        Assert.That(editor.Scene.Children, Is.Empty);
        Assert.That(editor.History.UndoCount, Is.Zero);
        string directory = Path.Combine(_directory, "resources", "tts");
        Assert.That(Directory.GetFiles(directory), Is.Empty);

        emptyAudio = false;
        await model.Generate();
        var element = editor.Scene.Children.Single();
        string path = element.Objects.OfType<SourceSound>().Single().Source.Value!.Uri.LocalPath;
        Assert.That(File.Exists(path), Is.True);
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Is.Empty);
        Assert.That(File.Exists(path), Is.True);
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(editor.Scene.Children.Single(), Is.SameAs(element));
        Assert.That(element.TryGetOriginalDuration(out var duration), Is.True);
        Assert.That(duration, Is.EqualTo(element.Length.Value));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Batch_uses_wav_durations_order_and_settings_and_is_one_undo(bool separateLayers)
    {
        using var editor = Editor();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        using var model = new ScriptImportViewModel(editor, Wave);
        model.RawText.Value = Script;
        model.ImportText();
        AssignPresets(model);
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
            var preset = Presets[i == 1 ? 1 : 0];
            Assert.That(generation.Text, Is.EqualTo(model.Lines[i].Text));
            Assert.That(generation.PresetId, Is.EqualTo(preset.Id));
            Assert.That(generation.Settings, Is.EqualTo(preset.Settings));
            var query = JsonSerializer.Deserialize<AudioQueryModel>(generation.AudioQueryJson)!;
            Assert.That(new[] { query.SpeedScale, query.PitchScale, query.IntonationScale, query.VolumeScale,
                query.PrePhonemeLength, query.PostPhonemeLength }, Is.EqualTo(new[]
            {
                preset.Settings.SpeedScale, preset.Settings.PitchScale, preset.Settings.IntonationScale,
                preset.Settings.VolumeScale, preset.Settings.PrePhonemeLength, preset.Settings.PostPhonemeLength
            }));
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
        if (reason == "overlap")
        {
            existing.Name.Value = "Existing edit";
            editor.History.Commit();
        }
        var enteredSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSecond = new ManualResetEventSlim();
        int count = 0;
        using var model = new ScriptImportViewModel(editor, (text, settings) => Task.Run(() =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                enteredSecond.SetResult();
                if (!releaseSecond.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                if (reason == "failure") throw new InvalidOperationException("engine failure");
            }
            return MakeWave(text, settings);
        }));
        model.RawText.Value = Script;
        model.ImportText();
        AssignPresets(model);
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
        Assert.That(model.Speakers[0].SelectedPreset.Value, Is.Not.Null);
        if (reason == "overlap")
        {
            Assert.That(editor.History.UndoCount, Is.EqualTo(1), "A failed batch must not enter the undo history.");
            Assert.That(editor.History.Undo(), Is.True);
            Assert.That(existing.Name.Value, Is.Not.EqualTo("Existing edit"));
            Assert.That(editor.Scene.Children.Single(), Is.SameAs(existing));
            Assert.That(editor.History.Redo(), Is.True);
            Assert.That(existing.Name.Value, Is.EqualTo("Existing edit"));
            Assert.That(editor.Scene.Children.Single(), Is.SameAs(existing));
        }
        model.FirstLayer.Value = 3;
        await model.Generate();
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(4), model.Message.Value);
        Assert.That(editor.Scene.Children.First(), Is.SameAs(existing));
    }

    [AvaloniaTest]
    [TestCase(320, false)]
    [TestCase(460, true)]
    public async Task Tab_generates_current_source_text_with_selected_presets(int width, bool light)
    {
        using var editor = Editor();
        using var model = new ScriptImportViewModel(editor, Wave);
        model.IsVoiceVoxInstalled.Value = true;
        var view = new ScriptImportView { DataContext = model };
        var window = new Window { Width = width, Height = 700, Content = view,
            RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        try
        {
            window.Show();
            await Settle();
            var input = view.FindControl<TextBox>("ScriptTextBox")!;
            Assert.That(input.Focus(), Is.True);
            input.Text = Script;
            Assert.That(model.Speakers, Is.Empty, "Typing must not rebuild the speaker selectors until focus leaves the input.");
            Assert.That(view.FindControl<Button>("OpenScriptButton")!.Focus(), Is.True);
            await Settle();
            var speakerSection = view.FindControl<Expander>("SpeakersSection")!;
            var selectors = speakerSection.GetVisualDescendants().OfType<ComboBox>().ToArray();
            Assert.That(selectors, Has.Length.EqualTo(2), "Each script speaker has only a preset selector.");
            for (int i = 0; i < selectors.Length; i++)
                selectors[i].SelectedItem = model.Speakers[i].Presets.Value.Single(x => x.Id == Presets[i].Id);
            Assert.That(speakerSection.GetVisualDescendants().OfType<Button>().Any(x => Equals(x.Content, "適用")), Is.False);
            Capture("input");
            Assert.That(input.Focus(), Is.True);
            input.Text = Script.Replace("こんにちは。", "入力欄で変更したセリフです。");
            Assert.That(view.FindControl<Button>("OpenScriptButton")!.Focus(), Is.True);
            Assert.That(model.Lines[0].Text, Is.EqualTo("入力欄で変更したセリフです。"));
            Assert.That(model.Speakers.Select(x => x.SelectedPreset.Value), Is.EqualTo(Presets),
                "Automatic imports must retain the presets assigned to unchanged speaker names.");
            view.FindControl<Expander>("InputSection")!.IsExpanded = false;
            await Settle();
            Capture("speakers");

            // Replacing a saved preset keeps its ID selected and immediately uses its updated tuning.
            var updated = Presets[0] with { Settings = Presets[0].Settings with { SpeedScale = 1.4 } };
            VoicePresetStore.Save(VoicePresetStore.GlobalPath, updated);
            model.RefreshPresets();
            Assert.That(selectors[0].SelectedItem, Is.EqualTo(updated));
            VoicePresetStore.Delete(VoicePresetStore.GlobalPath, updated.Id);
            model.RefreshPresets();
            Assert.That(selectors[0].SelectedItem, Is.Null);
            await model.Generate();
            Assert.That(editor.Scene.Children, Is.Empty);
            Assert.That(model.Message.Value, Does.Contain("案内役").And.Contain("プリセットを選択"));
            VoicePresetStore.Save(VoicePresetStore.GlobalPath, updated);
            model.RefreshPresets();
            await Settle();
            Assert.That(selectors[0].SelectedItem, Is.EqualTo(updated), "Returning presets restore their assignment automatically.");

            speakerSection.IsExpanded = false;
            await Settle();
            view.FindControl<NumericUpDown>("GapInput")!.Value = 0.3m;
            Assert.That(model.GapSeconds.Value, Is.EqualTo(0.3));
            Capture("placement");
            var add = view.FindControl<Button>("GenerateScriptButton")!;
            Assert.That(add.IsEffectivelyVisible, Is.True);
            Assert.That(add.TranslatePoint(default, window)!.Value.Y + add.Bounds.Height, Is.LessThanOrEqualTo(700));
            view.FindControl<Expander>("InputSection")!.IsExpanded = true;
            await Settle();
            Assert.That(input.Focus(), Is.True);
            input.Text = Script.Replace("こんにちは。", "生成ボタンを押す直前に変更したセリフです。");
            var generatePoint = add.TranslatePoint(new Point(add.Bounds.Width / 2, add.Bounds.Height / 2), window)!.Value;
            window.MouseDown(generatePoint, Avalonia.Input.MouseButton.Left);
            window.MouseUp(generatePoint, Avalonia.Input.MouseButton.Left);
            for (int i = 0; model.IsGenerating.Value && i < 500; i++) await Task.Delay(10);
            Assert.That(model.IsGenerating.Value, Is.False);
            Assert.That(editor.Scene.Children, Has.Count.EqualTo(3), model.Message.Value);
            var generation = editor.Scene.Children[0].Objects.OfType<TtsController>().Single().Generation.Value!;
            Assert.That(generation.Text, Is.EqualTo("生成ボタンを押す直前に変更したセリフです。"),
                "The source input is also the user's confirmation of what will be generated.");
            Assert.That(generation.Settings,
                Is.EqualTo(updated.Settings), "The ComboBox selection is the complete source of generation settings.");

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
    [TestCase(390, 410, false)]
    [TestCase(320, 350, true)]
    public async Task Tab_scroll_reaches_placement_after_status_is_cleared_and_panel_is_resized(
        int width, int height, bool withSpeakers)
    {
        using var editor = Editor();
        using var model = new ScriptImportViewModel(editor, Wave);
        model.IsVoiceVoxInstalled.Value = true;
        if (withSpeakers)
        {
            model.RawText.Value = Script;
            model.ImportText();
        }
        var view = new ScriptImportView { DataContext = model };
        // Match the tab header and nested content presenters used by the dock host.
        var host = new DockPanel();
        var header = new Border { Height = 28 };
        DockPanel.SetDock(header, Dock.Top);
        host.Children.Add(header);
        host.Children.Add(new ContentControl { Content = new ContentControl { Content = view } });
        var window = new Window { Width = width + 160, Height = height + 300, Content = host,
            RequestedThemeVariant = ThemeVariant.Dark };
        try
        {
            window.Show();
            await Settle();
            var scroll = view.FindControl<ScrollViewer>("EditorScroll")!;
            model.Message.Value = "台本を入力して話者にプリセットを割り当ててください。";
            await Settle();
            model.Message.Value = "";
            await Settle();
            window.Width = width;
            window.Height = height;
            view.FindControl<TextBox>("ScriptTextBox")!.Focus();
            model.IsVoiceVoxInstalled.Value = false;
            await Settle();
            model.IsVoiceVoxInstalled.Value = true;
            await Settle();
            await CheckScrollEnd("cleared");
            model.Message.Value = string.Join('\n', Enumerable.Repeat("台本を入力して話者にプリセットを割り当ててください。", 8));
            await Settle();
            await CheckScrollEnd("status");

            async Task CheckScrollEnd(string stage)
            {
                var bar = scroll.GetVisualDescendants().OfType<ScrollBar>()
                    .Single(x => x.Name == "PART_VerticalScrollBar" && x.TemplatedParent == scroll);
                var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
                var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
                window.MouseMove(start);
                await Settle();
                window.MouseDown(start, Avalonia.Input.MouseButton.Left);
                var end = new Point(start.X, window.Height + 200);
                window.MouseMove(end);
                window.MouseUp(end, Avalonia.Input.MouseButton.Left);
                await Settle();

                Assert.That(scroll.ScrollBarMaximum.Y, Is.GreaterThan(0));
                Assert.That(scroll.Offset.Y, Is.EqualTo(scroll.ScrollBarMaximum.Y).Within(1), stage);
                var placement = view.FindControl<Expander>("PlacementSection")!;
                var bottom = placement.TranslatePoint(new Point(0, placement.Bounds.Height), scroll)!.Value.Y;
                Assert.That(bottom, Is.LessThanOrEqualTo(scroll.Viewport.Height + 1),
                    "The end of the placement settings must remain reachable after the status disappears.");
                var add = view.FindControl<Button>("GenerateScriptButton")!;
                double buttonTop = add.TranslatePoint(default, view)!.Value.Y;
                Assert.That(buttonTop, Is.GreaterThanOrEqualTo(scroll.Bounds.Bottom));
                Assert.That(buttonTop + add.Bounds.Height,
                    Is.LessThanOrEqualTo(view.Bounds.Height));

                if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is not { } directory) return;
                Directory.CreateDirectory(directory);
                using var frame = window.CaptureRenderedFrame()!;
                frame.Save(Path.Combine(directory, $"script-scroll-{width}-{height}-{stage}.png"), new PngBitmapEncoderOptions());
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
            using var model = new ScriptImportViewModel(editor);
            model.RawText.Value = "A:こんにちは。\nB:ありがとう。";
            model.ImportText();
            var voices = loader.VoiceSets.SelectMany(x => x.Metadata).DistinctBy(x => x.SpeakerUuid).Take(2).ToArray();
            if (voices.Length < 2) Assert.Ignore("Install at least two distinct VOICEVOX speakers to run this test.");
            for (int i = 0; i < voices.Length; i++)
            {
                var voice = voices[i];
                var style = voice.Styles[0];
                var preset = new VoicePreset { Name = voice.Name, Settings = new()
                {
                    SpeakerUuid = voice.SpeakerUuid, SpeakerName = voice.Name, StyleId = style.Id,
                    StyleName = style.Name, ModelVersion = voice.Version
                } };
                VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
                model.RefreshPresets();
                model.Speakers[i].SelectedPreset.Value = model.Speakers[i].Presets.Value.Single(x => x.Id == preset.Id);
            }
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
