using System.Text.Json;
using Beutl.Editor;
using Beutl.Editor.Services;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Logging;
using Beutl.ProjectSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Beutl.Extensions.Voice;

[Export]
public sealed partial class VoiceMcpToolExtension : McpToolExtension
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private static readonly ILogger s_logger = Log.CreateLogger<VoiceMcpToolExtension>();

    public override string Name => "Voice MCP Tools";
    public override string DisplayName => "テキスト読み上げ MCP ツール";

    public override IReadOnlyList<McpToolDefinition> Tools { get; } = CreateTools();

    public override async ValueTask<McpToolResult> InvokeAsync(McpToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return call.Name switch
            {
                "voice_list_engines" => ListEngines(),
                "voice_list_voices" => await ListVoices(call.Arguments, cancellationToken),
                "voice_list_presets" => Json(new
                {
                    presets = VoicePresetStore.Load(VoicePresetStore.GlobalPath).Select(DescribePreset).ToArray()
                }),
                "voice_generate" => await Generate(call, cancellationToken),
                "voice_regenerate" => await Regenerate(call, cancellationToken),
                "voice_generate_script" => await GenerateScript(call, cancellationToken),
                "voice_save_preset" => SavePreset(call),
                "voice_analyze" => await Analyze(call.Arguments, cancellationToken),
                "voice_register_engine" => await RegisterEngine(call.Arguments, cancellationToken),
                "voice_set_lip_sync" => SetLipSync(call),
                "voice_list_dictionary_words" => await ListDictionaryWords(call.Arguments, cancellationToken),
                "voice_save_dictionary_word" => await SaveDictionaryWord(call.Arguments, cancellationToken),
                "voice_delete_dictionary_word" => await DeleteDictionaryWord(call.Arguments, cancellationToken),
                "voice_import_dictionary" => await ImportDictionary(call.Arguments, cancellationToken),
                "voice_export_dictionary" => await ExportDictionary(call.Arguments, cancellationToken),
                _ => McpToolResult.Error($"Unknown voice tool: {call.Name}")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            s_logger.LogWarning(ex, "Voice MCP tool {Tool} failed", call.Name);
            return McpToolResult.Error(ex.Message);
        }
    }

    private static McpToolResult ListEngines()
    {
        var loader = TtsLoader.VoiceVoxLoader.Value;
        return Json(new
        {
            engines = new[] { VoiceEngineConnection.Core }.Concat(VoiceEngineStore.Load()).Select(engine => new
            {
                engineId = engine.Id, engine.Name, engine.IsCore,
                kind = engine.IsCore ? "core" : engine.IsAquesTalkPlayer ? "aquestalkplayer" : "http",
                url = engine.IsCore || engine.IsAquesTalkPlayer ? null : engine.Url,
                applicationPath = engine.IsAquesTalkPlayer ? engine.ApplicationPath : null,
                engine.Version, engine.Capabilities,
                isInstalled = engine.IsCore ? (bool?)(loader?.IsInstalled == true) : null,
                isLoaded = engine.IsCore ? (bool?)(loader?.IsLoaded == true) : null
            }).ToArray()
        });
    }

    private static async Task<McpToolResult> ListVoices(JsonElement arguments, CancellationToken cancellationToken)
    {
        string engineId = arguments.TryGetProperty("engineId", out var id)
            ? id.GetString() ?? VoiceSettings.CoreEngineId : VoiceSettings.CoreEngineId;
        VoiceEngineConnection engine;
        if (engineId == VoiceSettings.CoreEngineId)
        {
            var loader = TtsLoader.VoiceVoxLoader.Value
                ?? throw new InvalidOperationException("VOICEVOX Core is not loaded. Use a registered HTTP engine or set up Core in Beutl.");
            await loader.InitializationTcs.Task.WaitAsync(cancellationToken);
            if (!loader.IsLoaded)
                throw new InvalidOperationException("VOICEVOX Core is unavailable. Use a registered HTTP engine or set up Core in Beutl.");
            engine = VoiceEngineConnection.Core with { Voices = loader.GetVoices() };
        }
        else
        {
            var registered = VoiceEngineStore.Load().FirstOrDefault(connection => connection.Id == engineId)
                ?? throw new InvalidOperationException($"Voice engine '{engineId}' is not registered. Call voice_list_engines.");
            // AquesTalkPlayer's voices are its presets, read from the app's settings.
            engine = await VoiceSynthesisService.DiscoverAsync(registered, cancellationToken);
        }
        return Json(new
        {
            engineId = engine.Id, engineName = engine.Name, engineVersion = engine.Version, engine.Capabilities,
            voices = engine.Voices.Select(voice => voice.AquesTalkPlayer is { } values
                ? (object)new
                {
                    speakerUuid = voice.SpeakerUuid, speakerName = voice.Name, modelVersion = voice.Version,
                    styles = voice.Styles.Select(style => new { styleId = style.Id, styleName = style.Name }).ToArray(),
                    aquesTalkPlayerVoice = values
                }
                : new
                {
                    speakerUuid = voice.SpeakerUuid, speakerName = voice.Name, modelVersion = voice.Version,
                    styles = voice.Styles.Select(style => new { styleId = style.Id, styleName = style.Name }).ToArray()
                }).ToArray()
        });
    }

    private static async Task<McpToolResult> Generate(McpToolCall call, CancellationToken cancellationToken)
    {
        var request = call.Arguments.Deserialize<GenerateRequest>(s_json)!;
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Enter text to speak.");
        if (request.Layer < 0 || request.StartSeconds is { } seconds && (!double.IsFinite(seconds) || seconds < 0))
            throw new ArgumentException("startSeconds and layer must be non-negative.");

        var (settings, preset) = ResolveVoice(request, call.Arguments);
        var query = GetAnalysisQuery(request.Analysis, request.Text, settings);

        var (scene, _) = GetEditor(call.EditorContext);
        var start = request.StartSeconds is { } startSeconds ? MediaTime.FromSeconds(startSeconds)
            : call.EditorContext!.GetRequiredService<IEditorClock>().CurrentTime.Value;
        // Validate the save location and template before contacting the synthesis engine.
        _ = VoiceSynthesisService.GetOutputDirectory(scene);
        var subtitle = VoiceSubtitles.Load(preset?.Subtitle);
        string? path = null;
        bool committed = false;
        try
        {
            var output = await VoiceSynthesisService.SynthesizeAsync(request.Text, settings, query, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _ = GetEditor(call.EditorContext, scene);
            string directory = VoiceSynthesisService.GetOutputDirectory(scene);
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"{Guid.NewGuid()}.wav");
            await File.WriteAllBytesAsync(path, output.Wave, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var (_, history) = GetEditor(call.EditorContext, scene);
            var generation = new VoiceGeneration
            {
                Text = request.Text, Settings = output.Settings, AudioQueryJson = output.Json,
                PresetId = preset?.Id, PresetName = preset?.Name
            };
            var element = VoiceElementFactory.Create(scene, path, generation, start, request.Layer, output.Resampling);
            history.ExecuteInTransaction(() =>
            {
                element.ZIndex.Value = PsdLipSyncPresets.FreeLayer(scene, element.Range, request.Layer);
                scene.AddChild(element, ElementOverlapHandling.ThrowException);
                if (preset?.LipSync is { } lipSync)
                    PsdLipSyncPresets.Connect(scene, [(element.Objects.OfType<TtsController>().Single(), lipSync)]);
                VoiceSubtitles.Place(scene, element, subtitle);
            }, "Generate speech (MCP)", cancellationToken: cancellationToken);
            committed = true;
            return Json(DescribeSpeech(scene, element));
        }
        finally
        {
            // Keep successful audio for Redo; failed and cancelled calls leave no generated file.
            if (!committed && path != null)
                try { File.Delete(path); }
                catch (Exception ex) { s_logger.LogWarning(ex, "Could not delete generated speech file {Path}", path); }
        }
    }

    private static (Scene Scene, HistoryManager History) GetEditor(IEditorContext? editor, Scene? expected = null)
    {
        // Closing a tab disposes its services while synthesis is awaiting the engine.
        var scene = editor?.GetService<Scene>();
        var history = editor?.GetService<HistoryManager>();
        if (scene == null || history == null || expected != null && scene != expected)
            throw new InvalidOperationException("Open a scene in Beutl and keep its editor open until speech generation completes.");
        return (scene, history);
    }

    private static McpToolResult Json<T>(T value) => McpToolResult.Json(JsonSerializer.SerializeToElement(value, s_json));

    private record VoiceRequest
    {
        public Guid? PresetId { get; init; }
        public VoiceSettings? Voice { get; init; }
    }

    private sealed record GenerateRequest : VoiceRequest
    {
        public required string Text { get; init; }
        public VoiceAnalysis? Analysis { get; init; }
        public double? StartSeconds { get; init; }
        public int Layer { get; init; } = 1;
    }
}
