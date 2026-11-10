using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Avalonia.Headless.NUnit;
using Beutl.Audio;
using Beutl.Editor.Observers;
using Beutl.Extensibility;
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
    private readonly VoiceMcpToolExtension _extension = new();
    private string _directory = null!;
    private string? _previousHome;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "voice-mcp-" + Guid.NewGuid());
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

    private TestEditor Editor() => new(Path.Combine(_directory, "scene.bescene"));

    private Task<McpToolResult> Invoke(string name, object arguments, TestEditor? editor = null,
        CancellationToken cancellationToken = default) => _extension.InvokeAsync(new McpToolCall(name,
        JsonSerializer.SerializeToElement(arguments, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        editor, new Services()), cancellationToken).AsTask();

    private static JsonElement Data(McpToolResult result)
    {
        Assert.That(result.IsError, Is.False, string.Join("\n", result.Content.OfType<McpTextContent>().Select(x => x.Text)));
        return result.StructuredContent!.Value;
    }

    private static VoiceSettings Voice(VoiceEngineConnection engine, long styleId = 7) => new()
    {
        EngineId = engine.Id, SpeakerUuid = "same-speaker", StyleId = styleId, SpeedScale = 1.25
    };

    private void AssertNoOutput(TestEditor editor)
    {
        Assert.That(editor.Scene.Children, Is.Empty);
        Assert.That(Directory.GetFiles(_directory, "*.wav", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public void Definitions_are_stable_and_describe_discovery_and_generation()
    {
        Assert.That(_extension.Tools, Is.SameAs(_extension.Tools));
        Assert.That(_extension.Tools, Has.Count.EqualTo(10));
        Assert.That(_extension.Tools.Select(tool => tool.Name), Is.Unique);
        foreach (var tool in _extension.Tools)
        {
            Assert.That(tool.Name, Does.StartWith("voice_"));
            Assert.That(tool.InputSchema.GetProperty("type").GetString(), Is.EqualTo("object"));
            Assert.That(tool.DestructiveHint, Is.EqualTo(tool.Name is "voice_regenerate" or "voice_save_preset" or "voice_set_lip_sync"));
            Assert.That(tool.ReadOnlyHint, Is.EqualTo(tool.Name.StartsWith("voice_list_", StringComparison.Ordinal) || tool.Name == "voice_analyze"));
        }
        var generate = _extension.Tools.Single(tool => tool.Name == "voice_generate");
        Assert.That(generate.IdempotentHint, Is.False);
        Assert.That(generate.InputSchema.GetProperty("properties").GetProperty("voice")
            .GetProperty("required").EnumerateArray().Select(item => item.GetString()),
            Is.EquivalentTo(new[] { "speakerUuid", "styleId" }));
        Assert.That(generate.InputSchema.GetProperty("properties").GetProperty("voice")
            .GetProperty("properties").GetProperty("styleId").TryGetProperty("minimum", out _), Is.False,
            "HTTP engines can use signed style IDs.");
    }

    [AvaloniaTest]
    public async Task Engines_and_presets_can_be_listed_without_an_editor_or_engine_connection()
    {
        var engine = new VoiceEngineConnection { Name = "Offline", Url = "http://127.0.0.1:1/" };
        VoiceEngineStore.Save(engine);
        var preset = new VoicePreset
        {
            Name = "Narrator", Settings = Voice(engine),
            LipSync = new("character.psd", "{}"), Subtitle = new("Caption", "{}")
        };
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
        var engines = Data(await Invoke("voice_list_engines", new { })).GetProperty("engines");
        Assert.That(engines.EnumerateArray().Select(item => item.GetProperty("engineId").GetString()),
            Is.EqualTo(new[] { VoiceSettings.CoreEngineId, engine.Id }));
        var saved = Data(await Invoke("voice_list_presets", new { })).GetProperty("presets")[0];
        Assert.That(saved.GetProperty("presetId").GetGuid(), Is.EqualTo(preset.Id));
        Assert.That(saved.GetProperty("settings").GetProperty("engineId").GetString(), Is.EqualTo(engine.Id));
        Assert.That(saved.GetProperty("hasLipSync").GetBoolean(), Is.True);
        Assert.That(saved.GetProperty("subtitleName").GetString(), Is.EqualTo("Caption"));
        Assert.That(saved.TryGetProperty("objectJson", out _), Is.False);
    }

    [AvaloniaTest]
    public async Task Voice_discovery_returns_live_speech_styles_without_changing_registered_settings()
    {
        using var server = new EngineServer(aivis: true);
        var engine = new VoiceEngineConnection { Name = "Registered engine", Url = server.Url };
        VoiceEngineStore.Save(engine);
        string before = File.ReadAllText(VoiceEngineStore.GlobalPath);
        var data = Data(await Invoke("voice_list_voices", new { engineId = engine.Id }));
        var voice = data.GetProperty("voices")[0];
        Assert.That(voice.GetProperty("speakerUuid").GetString(), Is.EqualTo("same-speaker"));
        Assert.That(voice.GetProperty("styles").GetArrayLength(), Is.EqualTo(1), "Singing styles cannot synthesize speech.");
        Assert.That(voice.GetProperty("styles")[0].GetProperty("styleId").GetInt64(), Is.EqualTo(7));
        Assert.That(data.GetProperty("capabilities").GetProperty("tempoDynamics").GetBoolean(), Is.True);
        Assert.That(File.ReadAllText(VoiceEngineStore.GlobalPath), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(7L)]
    [TestCase(-2147483648L)]
    [TestCase(4294967295L)]
    public async Task Generation_captures_playhead_avoids_new_locks_and_overlaps_and_supports_undo_redo(long styleId)
    {
        using var server = new EngineServer(style: styleId);
        var engine = await HttpVoiceEngine.DiscoverAsync(new() { Url = server.Url });
        VoiceEngineStore.Save(engine);
        using var editor = Editor();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        editor.Time.Value = MediaTime.FromSeconds(2.5);
        server.PauseAt = "/tts/synthesis";
        var pending = Invoke("voice_generate", new { text = "こんにちは。", voice = Voice(engine, styleId), layer = 3 }, editor);
        await server.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        editor.Time.Value = MediaTime.FromSeconds(10);
        editor.Scene.Layers.Add(new TimelineLayer { ZIndex = { Value = 3 }, IsLocked = { Value = true } });
        var occupied = new Element { ZIndex = { Value = 4 }, Length = { Value = MediaTime.FromSeconds(20) } };
        editor.Scene.AddChild(occupied);
        editor.History.Commit();
        server.Resume.TrySetResult();
        var data = Data(await pending);
        var element = editor.Scene.Children.Single(item => item != occupied);
        var generation = element.Objects.OfType<TtsController>().Single().Generation.Value!;
        string path = data.GetProperty("audioPath").GetString()!;
        Assert.That(data.GetProperty("elementId").GetGuid(), Is.EqualTo(element.Id.Value));
        Assert.That(data.GetProperty("sceneId").GetGuid(), Is.EqualTo(editor.Scene.Id.Value));
        Assert.That(data.GetProperty("startSeconds").GetDouble(), Is.EqualTo(2.5));
        Assert.That(data.GetProperty("durationSeconds").GetDouble(), Is.EqualTo(0.2).Within(1e-9));
        Assert.That(data.GetProperty("layer").GetInt32(), Is.EqualTo(5));
        Assert.That(generation.Text, Is.EqualTo("こんにちは。"));
        Assert.That(generation.Settings.EngineId, Is.EqualTo(engine.Id));
        Assert.That(generation.Settings.StyleId, Is.EqualTo(styleId));
        Assert.That(server.LastStyle, Is.EqualTo(styleId.ToString(CultureInfo.InvariantCulture)));
        Assert.That(generation.Settings.SpeakerName, Is.EqualTo("テスト話者"));
        Assert.That(generation.Settings.SpeedScale, Is.EqualTo(1.25));
        Assert.That(server.LastQuery!["speedScale"]!.GetValue<double>(), Is.EqualTo(1.25));
        Assert.That(element.Objects.OfType<SourceSound>().Single().Source.Value!.Uri.LocalPath, Is.EqualTo(path));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Is.EqualTo(new[] { occupied }));
        Assert.That(File.Exists(path), Is.True, "Redo needs the generated audio.");
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(editor.Scene.Children, Does.Contain(element));
    }

    [AvaloniaTest]
    public async Task Preset_generation_applies_voice_lip_sync_and_subtitles_in_one_transaction()
    {
        using var server = new EngineServer();
        var engine = await HttpVoiceEngine.DiscoverAsync(new() { Url = server.Url });
        VoiceEngineStore.Save(engine);
        using var editor = Editor();
        var character = new PsdTachieDrawable
        {
            Source = { Value = new FileInfo(Path.Combine(AppContext.BaseDirectory, "TestData", "tachie_rle.psd")) }
        };
        var preset = new VoicePreset
        {
            Name = "Narrator", Settings = Voice(engine), LipSync = PsdLipSyncPresets.Capture(character),
            Subtitle = VoiceSubtitles.Default(editor.Scene)
        };
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        var data = Data(await Invoke("voice_generate", new { text = "字幕のテスト", presetId = preset.Id, startSeconds = 1.25 }, editor));
        var controller = editor.Scene.EnumerateAllChildren<TtsController>().Single();
        Assert.That(controller.Generation.Value!.PresetId, Is.EqualTo(preset.Id));
        Assert.That(controller.LipSyncGroupId.Value, Is.Not.EqualTo(Guid.Empty));
        Assert.That(PsdLipSync.Targets(editor.Scene).Count(), Is.EqualTo(1));
        var speech = controller.FindHierarchicalParent<Element>()!;
        var caption = editor.Scene.Children.Single(item => item.Objects.OfType<SubtitleController>().Any());
        Assert.That(caption.EnumerateAllChildren<TextBlock>().Single().Text.Value, Is.EqualTo("字幕のテスト"));
        Assert.That(caption.Range, Is.EqualTo(speech.Range));
        Assert.That(caption.ZIndex.Value, Is.EqualTo(speech.ZIndex.Value + 1));
        Assert.That(data.GetProperty("startSeconds").GetDouble(), Is.EqualTo(1.25));
        Assert.That(editor.History.UndoCount, Is.EqualTo(1));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Is.Empty);
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(3));
    }

    [AvaloniaTest]
    [TestCase("{}")]
    [TestCase("{\"text\":\"hello\"}")]
    [TestCase("{\"text\":\" \",\"voice\":{\"speakerUuid\":\"a\",\"styleId\":0}}")]
    [TestCase("{\"text\":\"hello\",\"voice\":{\"speakerUuid\":\"a\"}}")]
    [TestCase("{\"text\":\"hello\",\"presetId\":\"00000000-0000-0000-0000-000000000001\",\"voice\":{\"speakerUuid\":\"a\",\"styleId\":0}}")]
    [TestCase("{\"text\":\"hello\",\"voice\":{\"speakerUuid\":\"a\",\"styleId\":0},\"startSeconds\":-1}")]
    public async Task Invalid_requests_return_tool_errors_without_modifying_the_scene(string json)
    {
        using var editor = Editor();
        var result = await Invoke("voice_generate", JsonSerializer.Deserialize<JsonElement>(json), editor);
        Assert.That(result.IsError, Is.True);
        AssertNoOutput(editor);
    }

    [AvaloniaTest]
    public async Task Generation_requires_an_open_saved_scene_before_contacting_the_engine()
    {
        var request = new { text = "hello", voice = new { speakerUuid = "a", styleId = 0 } };
        Assert.That((await Invoke("voice_generate", request)).IsError, Is.True);
        using var editor = Editor();
        editor.Scene.Uri = null;
        var result = await Invoke("voice_generate", request, editor);
        Assert.That(result.IsError, Is.True);
        Assert.That(((McpTextContent)result.Content.Single()).Text, Does.Contain("保存"));
        AssertNoOutput(editor);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Closing_the_editor_or_cancelling_during_synthesis_leaves_no_output(bool cancel)
    {
        using var server = new EngineServer { PauseAt = "/tts/synthesis", AbortPausedResponse = cancel };
        var engine = new VoiceEngineConnection { Url = server.Url };
        VoiceEngineStore.Save(engine);
        using var editor = Editor();
        using var cancellation = new CancellationTokenSource();
        var pending = Invoke("voice_generate", new { text = "hello", voice = Voice(engine) }, editor, cancellation.Token);
        await server.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel)
        {
            cancellation.Cancel();
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail("A cancelled MCP call must propagate cancellation to the host.");
            }
            catch (OperationCanceledException) { }
        }
        else
        {
            editor.Dispose();
            server.Resume.TrySetResult();
            Assert.That((await pending).IsError, Is.True);
        }
        AssertNoOutput(editor);
    }

    [AvaloniaTest]
    public async Task Engine_and_placement_failures_return_errors_and_rollback_generated_files_and_elements()
    {
        using var server = new EngineServer { FailSynthesis = true };
        var engine = new VoiceEngineConnection { Url = server.Url };
        VoiceEngineStore.Save(engine);
        using var editor = Editor();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        var request = new { text = "hello", voice = Voice(engine) };
        Assert.That((await Invoke("voice_generate", request, editor)).IsError, Is.True);
        AssertNoOutput(editor);
        server.FailSynthesis = false;
        var preset = new VoicePreset { Settings = Voice(engine), LipSync = new("missing.psd", "{}") };
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
        Assert.That((await Invoke("voice_generate", new { text = "hello", presetId = preset.Id }, editor)).IsError, Is.True);
        AssertNoOutput(editor);
        Assert.That(editor.History.UndoCount, Is.Zero);
    }

    private sealed class Services : IEditorContextServices
    {
        public IExtensionProvider ExtensionProvider => throw new NotSupportedException();
        public bool TryGetService<T>([NotNullWhen(true)] out T? service) where T : class
        {
            service = null;
            return false;
        }
    }
}
