using System.Text.Json;
using Avalonia.Headless.NUnit;
using Beutl.Audio;
using Beutl.Editor.Observers;
using Beutl.Extensions.PsdTachie.Graphics;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.ProjectSystem;
using TextBlock = Beutl.Graphics.Shapes.TextBlock;
using EngineServer = Beutl.Extensions.Voice.Tests.HttpVoiceEngineTests.EngineServer;

namespace Beutl.Extensions.Voice.Tests;

public partial class VoiceMcpToolTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<VoiceEngineConnection> Register(EngineServer server)
    {
        var data = Data(await Invoke("voice_register_engine", new { url = server.Url }));
        return VoiceEngineStore.Load().Single(engine => engine.Id == data.GetProperty("engineId").GetString());
    }

    private async Task<Element> Generate(TestEditor editor, VoiceEngineConnection engine, string text = "hello")
    {
        var data = Data(await Invoke("voice_generate", new { text, voice = Voice(engine) }, editor));
        return editor.Scene.Children.Single(element => element.Id.Value == data.GetProperty("elementId").GetGuid());
    }

    private static PsdTachieDrawable Character(TestEditor editor, int layer)
    {
        var target = new PsdTachieDrawable
        {
            Source = { Value = new FileInfo(Path.Combine(AppContext.BaseDirectory, "TestData", "tachie_rle.psd")) }
        };
        var element = new Element { Name = { Value = "Character" }, ZIndex = { Value = layer }, Length = { Value = MediaTime.FromSeconds(20) } };
        element.AddObject(target);
        editor.Scene.AddChild(element);
        return target;
    }

    [AvaloniaTest]
    public async Task Engine_registration_is_verified_and_reuses_connection_identity_and_name()
    {
        using var server = new EngineServer { FailDiscovery = true };
        Assert.That((await Invoke("voice_register_engine", new { url = server.Url })).IsError, Is.True);
        Assert.That(VoiceEngineStore.Load(), Is.Empty);
        server.FailDiscovery = false;
        var first = Data(await Invoke("voice_register_engine", new { url = server.Url.TrimEnd('/'), name = "My engine" }));
        var second = Data(await Invoke("voice_register_engine", new { url = server.Url, name = "Different name" }));
        Assert.That(second.GetProperty("engineId").GetString(), Is.EqualTo(first.GetProperty("engineId").GetString()));
        Assert.That(second.GetProperty("name").GetString(), Is.EqualTo("My engine"));
        Assert.That(VoiceEngineStore.Load(), Has.Length.EqualTo(1));
        var voices = Data(await Invoke("voice_list_voices", new { engineId = second.GetProperty("engineId").GetString() }));
        Assert.That(voices.GetProperty("voices").GetArrayLength(), Is.EqualTo(1));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Engine_registration_does_not_resurrect_deleted_connections_or_save_after_cancellation(bool cancel)
    {
        using var server = new EngineServer { PauseAt = "/tts/speakers", AbortPausedResponse = cancel };
        var engine = new VoiceEngineConnection { Url = server.Url, Name = "Original" };
        VoiceEngineStore.Save(engine);
        string before = File.ReadAllText(VoiceEngineStore.GlobalPath);
        using var cancellation = new CancellationTokenSource();
        var pending = Invoke("voice_register_engine", new { url = server.Url }, cancellationToken: cancellation.Token);
        await server.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel)
        {
            cancellation.Cancel();
            try { await pending; Assert.Fail("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Assert.That(File.ReadAllText(VoiceEngineStore.GlobalPath), Is.EqualTo(before));
        }
        else
        {
            VoiceEngineStore.Delete(engine.Id);
            server.Resume.TrySetResult();
            Assert.That((await pending).IsError, Is.True);
            Assert.That(VoiceEngineStore.Load(), Is.Empty);
        }
    }

    [AvaloniaTest]
    public async Task Preset_create_update_and_explicit_clearing_preserve_unmentioned_fields_and_existing_audio()
    {
        using var server = new EngineServer();
        var engine = await Register(server);
        using var editor = Editor();
        var target = Character(editor, 0);
        var preset = Data(await Invoke("voice_save_preset", new
        {
            name = "Narrator", voice = Voice(engine), lipSyncTargetId = target.Id.Value, useDefaultSubtitle = true
        }, editor)).GetProperty("preset");
        Guid id = preset.GetProperty("presetId").GetGuid();
        var generated = Data(await Invoke("voice_generate", new { text = "字幕", presetId = id }, editor));
        var speech = editor.Scene.Children.Single(element => element.Id.Value == generated.GetProperty("elementId").GetGuid());
        var controller = speech.Objects.OfType<TtsController>().Single();
        var original = controller.Generation.Value;
        Data(await Invoke("voice_save_preset", new { presetId = id, name = "Renamed" }));
        var saved = VoicePresetStore.Load(VoicePresetStore.GlobalPath).Single();
        Assert.That(saved.Name, Is.EqualTo("Renamed"));
        Assert.That(saved.LipSync, Is.Not.Null);
        Assert.That(saved.Subtitle, Is.Not.Null);
        Data(await Invoke("voice_save_preset", new { presetId = id, lipSyncTargetId = (Guid?)null, subtitleElementId = (Guid?)null }));
        saved = VoicePresetStore.Load(VoicePresetStore.GlobalPath).Single();
        Assert.That(saved.LipSync, Is.Null);
        Assert.That(saved.Subtitle, Is.Null);
        Assert.That(saved.Settings, Is.EqualTo(Voice(engine)));
        Assert.That(controller.Generation.Value, Is.SameAs(original));
        Assert.That((await Invoke("voice_save_preset", new { presetId = Guid.NewGuid(), name = "Missing" })).IsError, Is.True);
        Assert.That(VoicePresetStore.Load(VoicePresetStore.GlobalPath), Has.Length.EqualTo(1));
        editor.Scene.Uri = null;
        var captured = Data(await Invoke("voice_save_preset", new { name = "From speech", elementId = speech.Id.Value }, editor));
        var copy = VoicePresetStore.Load(VoicePresetStore.GlobalPath).Single(item => item.Id == captured.GetProperty("preset").GetProperty("presetId").GetGuid());
        Assert.That(copy.Settings, Is.EqualTo(original!.Settings));
        Assert.That(copy.LipSync, Is.Not.Null, "Capture the current link, independent of later global preset edits.");
        Assert.That(copy.Subtitle, Is.Not.Null);
    }

    [AvaloniaTest]
    public async Task Analysis_round_trips_manual_pitch_and_extension_fields_into_generation_and_rejects_stale_queries()
    {
        using var server = new EngineServer();
        var engine = await Register(server);
        var data = Data(await Invoke("voice_analyze", new { text = "hello", voice = Voice(engine) }));
        var analysis = data.GetProperty("analysis").Deserialize<VoiceAnalysis>(JsonOptions)!;
        Assert.That(server.SynthesisCount, Is.Zero);
        analysis.AudioQuery.AccentPhrases[0].Moras[0].Pitch = 6.25;
        using var editor = Editor();
        Data(await Invoke("voice_generate", new { text = "hello", voice = Voice(engine), analysis }, editor));
        Assert.That(server.LastQuery!["accent_phrases"]![0]!["moras"]![0]!["pitch"]!.GetValue<double>(), Is.EqualTo(6.25));
        Assert.That(server.LastQuery["future_parameter"]!.GetValue<string>(), Is.EqualTo("preserve"));
        Assert.That(server.LastQuery["accent_phrases"]![0]!["moras"]![0]!["future_mora_parameter"]!.GetValue<int>(), Is.EqualTo(123));
        analysis.AudioQuery.AccentPhrases[0].Moras[0].Pitch = 6.8;
        Data(await Invoke("voice_regenerate", new { elementId = editor.Scene.Children.Single().Id.Value, analysis }, editor));
        Assert.That(server.LastQuery["accent_phrases"]![0]!["moras"]![0]!["pitch"]!.GetValue<double>(), Is.EqualTo(6.8));
        int before = server.SynthesisCount;
        Assert.That((await Invoke("voice_generate", new { text = "different", voice = Voice(engine), analysis }, editor)).IsError, Is.True);
        Assert.That((await Invoke("voice_generate", new { text = "hello", voice = Voice(engine, -7), analysis }, editor)).IsError, Is.True);
        Assert.That(server.SynthesisCount, Is.EqualTo(before));
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task Regeneration_preserves_drafts_trims_and_edited_queries_and_updates_text_and_subtitles_atomically()
    {
        using var server = new EngineServer();
        var engine = await Register(server);
        using var editor = Editor();
        var preset = new VoicePreset { Name = "Narrator", Settings = Voice(engine), Subtitle = VoiceSubtitles.Default(editor.Scene) };
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
        Data(await Invoke("voice_generate", new { text = "original", presetId = preset.Id }, editor));
        var element = editor.Scene.Children.Single(item => item.Objects.OfType<TtsController>().Any());
        var controller = element.Objects.OfType<TtsController>().Single();
        var sound = element.Objects.OfType<SourceSound>().Single();
        var caption = editor.Scene.Children.Single(item => item.Objects.OfType<SubtitleController>().Any());
        var text = caption.EnumerateAllChildren<TextBlock>().Single();
        element.Start.Value = MediaTime.FromSeconds(2);
        element.Length.Value = MediaTime.FromSeconds(0.1);
        sound.OffsetPosition.Value = MediaTime.FromSeconds(0.03);
        sound.Speed.Value = 120;
        var query = JsonSerializer.Deserialize<AudioQueryModel>(controller.Generation.Value!.AudioQueryJson)!;
        query.AccentPhrases[0].Moras[0].Pitch = 6.4;
        controller.Draft.Value = controller.Generation.Value with
        {
            Settings = controller.Generation.Value.Settings with { SpeedScale = 1.4 }, AudioQueryJson = JsonSerializer.Serialize(query)
        };
        string originalPath = sound.Source.Value!.Uri.LocalPath;
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        Data(await Invoke("voice_regenerate", new { elementId = element.Id.Value }, editor));
        Assert.That(server.LastQuery!["accent_phrases"]![0]!["moras"]![0]!["pitch"]!.GetValue<double>(), Is.EqualTo(6.4));
        Assert.That(controller.Generation.Value!.Settings.SpeedScale, Is.EqualTo(1.4));
        Assert.That(element.Start.Value, Is.EqualTo(MediaTime.FromSeconds(2)));
        Assert.That(element.Length.Value, Is.EqualTo(MediaTime.FromSeconds(0.1)));
        Assert.That(sound.OffsetPosition.Value, Is.EqualTo(MediaTime.FromSeconds(0.03)));
        Assert.That(sound.Speed.Value, Is.EqualTo(120));
        Assert.That(sound.Source.Value!.Uri.LocalPath, Is.Not.EqualTo(originalPath));
        var previous = controller.Generation.Value;
        string previousPath = sound.Source.Value.Uri.LocalPath;
        Data(await Invoke("voice_regenerate", new { elementId = element.Id.Value, text = "new text", voice = Voice(engine) }, editor));
        Assert.That(controller.Text.Value, Is.EqualTo("new text"));
        Assert.That(controller.AppliedPreset.Value, Is.Null);
        Assert.That(text.Text.Value, Is.EqualTo("new text"));
        Assert.That(server.LastText, Is.EqualTo("new text"));
        Assert.That(server.LastQuery!["accent_phrases"]![0]!["moras"]![0]!["pitch"]!.GetValue<double>(), Is.EqualTo(5));
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(2));
        Assert.That(editor.History.UndoCount, Is.EqualTo(2));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(controller.Generation.Value, Is.EqualTo(previous));
        Assert.That(controller.Text.Value, Is.EqualTo("original"));
        Assert.That(text.Text.Value, Is.EqualTo("original"));
        Assert.That(sound.Source.Value!.Uri.LocalPath, Is.EqualTo(previousPath));
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(text.Text.Value, Is.EqualTo("new text"));
    }

    [AvaloniaTest]
    [TestCase("move")]
    [TestCase("lock")]
    [TestCase("text")]
    [TestCase("close")]
    [TestCase("cancel")]
    public async Task Regeneration_rechecks_the_target_after_awaits_without_blocking_placement_edits(string change)
    {
        using var server = new EngineServer();
        var engine = await Register(server);
        using var editor = Editor();
        var element = await Generate(editor, engine);
        var controller = element.Objects.OfType<TtsController>().Single();
        var original = controller.Generation.Value;
        int files = Directory.GetFiles(_directory, "*.wav", SearchOption.AllDirectories).Length;
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        server.PauseAt = "/tts/synthesis";
        server.AbortPausedResponse = change == "cancel";
        using var cancellation = new CancellationTokenSource();
        var pending = Invoke("voice_regenerate", new { elementId = element.Id.Value, text = "requested" }, editor, cancellation.Token);
        await server.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "move") element.Start.Value = MediaTime.FromSeconds(4);
        if (change == "lock") element.IsLocked.Value = true;
        if (change == "text") controller.Text.Value = "User edit";
        if (change == "close") editor.Dispose();
        if (change == "cancel")
        {
            cancellation.Cancel();
            try { await pending; Assert.Fail("Expected cancellation."); }
            catch (OperationCanceledException) { }
        }
        else
        {
            server.Resume.TrySetResult();
            var result = await pending;
            Assert.That(result.IsError, Is.EqualTo(change != "move"));
        }
        if (change == "move")
        {
            Assert.That(element.Start.Value, Is.EqualTo(MediaTime.FromSeconds(4)));
            Assert.That(controller.Text.Value, Is.EqualTo("requested"));
        }
        else
        {
            Assert.That(controller.Generation.Value, Is.SameAs(original));
            Assert.That(controller.Text.Value, Is.EqualTo(change == "text" ? "User edit" : "hello"));
            Assert.That(Directory.GetFiles(_directory, "*.wav", SearchOption.AllDirectories), Has.Length.EqualTo(files));
        }
    }

    [AvaloniaTest]
    public async Task Script_generation_uses_mixed_engines_actual_durations_and_one_undoable_batch()
    {
        using var firstServer = new EngineServer();
        using var secondServer = new EngineServer(aivis: true, style: -2147483648L);
        var firstEngine = await Register(firstServer);
        var secondEngine = await Register(secondServer);
        using var editor = Editor();
        var first = new VoicePreset { Name = "A", Settings = Voice(firstEngine), Subtitle = VoiceSubtitles.Default(editor.Scene) };
        var second = new VoicePreset { Name = "B", Settings = Voice(secondEngine, -2147483648L) };
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, first);
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, second);
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        editor.Time.Value = MediaTime.FromSeconds(2.5);
        var data = Data(await Invoke("voice_generate_script", new
        {
            script = "A: first\nB：second\nA: third", speakerPresets = new Dictionary<string, Guid> { ["A"] = first.Id, ["B"] = second.Id },
            gapSeconds = 0.125, firstLayer = 2, separateLayers = true
        }, editor));
        var elements = data.GetProperty("elements").EnumerateArray().ToArray();
        Assert.That(elements.Select(item => item.GetProperty("startSeconds").GetDouble()), Is.EqualTo(new[] { 2.5, 2.825, 3.15 }).Within(1e-9));
        Assert.That(elements.Select(item => item.GetProperty("layer").GetInt32()), Is.EqualTo(new[] { 2, 4, 2 }));
        Assert.That(elements[1].GetProperty("settings").GetProperty("styleId").GetInt64(), Is.EqualTo(-2147483648L));
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(5));
        Assert.That(firstServer.SynthesisCount, Is.EqualTo(2));
        Assert.That(secondServer.SynthesisCount, Is.EqualTo(1));
        Assert.That(editor.History.UndoCount, Is.EqualTo(1));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Is.Empty);
        Assert.That(Directory.GetFiles(_directory, "*.wav", SearchOption.AllDirectories), Has.Length.EqualTo(3));
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(5));
    }

    [AvaloniaTest]
    [TestCase("engine")]
    [TestCase("lock")]
    [TestCase("overlap")]
    [TestCase("close")]
    [TestCase("cancel")]
    public async Task Failed_or_cancelled_scripts_remove_the_entire_batch_and_keep_user_changes(string change)
    {
        using var firstServer = new EngineServer();
        using var secondServer = new EngineServer();
        var firstEngine = await Register(firstServer);
        var secondEngine = await Register(secondServer);
        var first = new VoicePreset { Name = "A", Settings = Voice(firstEngine) };
        var second = new VoicePreset { Name = "B", Settings = Voice(secondEngine) };
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, first);
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, second);
        secondServer.PauseAt = "/tts/synthesis";
        secondServer.FailSynthesis = change == "engine";
        secondServer.AbortPausedResponse = change == "cancel";
        using var editor = Editor();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        using var cancellation = new CancellationTokenSource();
        var pending = Invoke("voice_generate_script", new
        {
            script = "A: first\nB: second", speakerPresets = new Dictionary<string, Guid> { ["A"] = first.Id, ["B"] = second.Id }
        }, editor, cancellation.Token);
        await secondServer.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(Directory.GetFiles(_directory, "*.wav", SearchOption.AllDirectories), Has.Length.EqualTo(1));
        if (change == "lock") editor.Scene.Layers.Add(new TimelineLayer { ZIndex = { Value = 1 }, IsLocked = { Value = true } });
        Element? blocker = null;
        if (change == "overlap") editor.Scene.AddChild(blocker = new Element { ZIndex = { Value = 1 }, Length = { Value = MediaTime.FromSeconds(1) } });
        if (change == "close") editor.Dispose();
        if (change == "cancel")
        {
            cancellation.Cancel();
            try { await pending; Assert.Fail("Expected cancellation."); }
            catch (OperationCanceledException) { }
        }
        else
        {
            secondServer.Resume.TrySetResult();
            Assert.That((await pending).IsError, Is.True);
        }
        Assert.That(editor.Scene.Children, Is.EqualTo(blocker == null ? Array.Empty<Element>() : new[] { blocker }));
        Assert.That(Directory.GetFiles(_directory, "*.wav", SearchOption.AllDirectories), Is.Empty);
    }

    [AvaloniaTest]
    public async Task Lip_sync_lists_targets_and_respects_locks_only_when_the_actual_operation_changes_the_target()
    {
        using var server = new EngineServer();
        var engine = await Register(server);
        using var editor = Editor();
        var target = Character(editor, 0);
        var first = await Generate(editor, engine);
        var firstController = first.Objects.OfType<TtsController>().Single();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        first.IsLocked.Value = true;
        editor.History.Commit();
        int history = editor.History.UndoCount;
        var list = Data(await Invoke("voice_set_lip_sync", new { elementId = first.Id.Value }, editor));
        Assert.That(list.GetProperty("targets")[0].GetProperty("targetId").GetGuid(), Is.EqualTo(target.Id.Value));
        Assert.That(editor.History.UndoCount, Is.EqualTo(history));
        Assert.That((await Invoke("voice_set_lip_sync", new { elementId = first.Id.Value, targetId = target.Id.Value }, editor)).IsError, Is.True);
        first.IsLocked.Value = false;
        editor.History.Commit();
        Data(await Invoke("voice_set_lip_sync", new { elementId = first.Id.Value, targetId = target.Id.Value }, editor));
        Guid group = firstController.LipSyncGroupId.Value;
        Assert.That(group, Is.Not.EqualTo(Guid.Empty));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(firstController.LipSyncGroupId.Value, Is.EqualTo(Guid.Empty));
        Assert.That(editor.History.Redo(), Is.True);
        target.FindHierarchicalParent<Element>()!.IsLocked.Value = true;
        editor.History.Commit();
        var second = await Generate(editor, engine);
        Data(await Invoke("voice_set_lip_sync", new { elementId = second.Id.Value, targetId = target.Id.Value }, editor));
        Data(await Invoke("voice_set_lip_sync", new { elementId = first.Id.Value, targetId = (Guid?)null }, editor));
        var secondController = second.Objects.OfType<TtsController>().Single();
        Assert.That(secondController.LipSyncGroupId.Value, Is.EqualTo(group));
        Assert.That((await Invoke("voice_set_lip_sync", new { elementId = second.Id.Value, targetId = (Guid?)null }, editor)).IsError, Is.True);
        Assert.That(secondController.LipSyncGroupId.Value, Is.EqualTo(group));
        target.FindHierarchicalParent<Element>()!.IsLocked.Value = false;
        Data(await Invoke("voice_set_lip_sync", new { elementId = second.Id.Value, targetId = (Guid?)null }, editor));
        PsdLipSync.TryGetProperties(target, out var audio, out var offset);
        Assert.That(audio.Expression, Is.Null);
        Assert.That(offset.Expression, Is.Null);
    }
}
