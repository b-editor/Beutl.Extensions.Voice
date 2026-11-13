using System.Text.Json;
using Beutl.Audio;
using Beutl.Editor.Services;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.ProjectSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Beutl.Extensions.Voice;

public sealed partial class VoiceMcpToolExtension
{
    private static (VoiceSettings Settings, VoicePreset? Preset) ResolveVoice(VoiceRequest request, JsonElement arguments)
    {
        if ((request.PresetId == null) == (request.Voice == null))
            throw new ArgumentException("Supply exactly one of presetId or voice.");
        var preset = request.PresetId is { } id
            ? VoicePresetStore.Load(VoicePresetStore.GlobalPath).FirstOrDefault(item => item.Id == id)
                ?? throw new ArgumentException($"Voice preset '{id}' was not found. Call voice_list_presets.")
            : null;
        if (request.Voice != null) ValidateVoice(request.Voice, arguments.GetProperty("voice"));
        return (preset?.Settings ?? request.Voice!.WithSupportedTuning(), preset);
    }

    private static void ValidateVoice(VoiceSettings settings, JsonElement voice)
    {
        if (string.IsNullOrWhiteSpace(settings.SpeakerUuid) || !voice.TryGetProperty("styleId", out _))
            throw new ArgumentException("voice requires speakerUuid and styleId. Call voice_list_voices.");
    }

    private static AudioQueryModel? GetAnalysisQuery(VoiceAnalysis? analysis, string text, VoiceSettings settings)
    {
        if (analysis == null) return null;
        if (analysis.Settings == null || analysis.AudioQuery == null || analysis.Text != text
            || !analysis.Settings.UsesSameVoice(settings))
            throw new ArgumentException("The analysis must match the requested text, engine, speaker and style. Call voice_analyze for the new text or voice.");
        return analysis.AudioQuery;
    }

    private static async Task<McpToolResult> Analyze(JsonElement arguments, CancellationToken cancellationToken)
    {
        var request = arguments.Deserialize<GenerateRequest>(s_json)!;
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Enter text to analyze.");
        var (settings, _) = ResolveVoice(request, arguments);
        var query = await VoiceSynthesisService.CreateQueryAsync(request.Text, settings, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Json(new { analysis = new VoiceAnalysis(request.Text, settings, query) });
    }

    private static async Task<McpToolResult> Regenerate(McpToolCall call, CancellationToken cancellationToken)
    {
        var request = call.Arguments.Deserialize<RegenerateRequest>(s_json)!;
        var (scene, _) = GetEditor(call.EditorContext);
        var (element, controller) = GetSpeech(scene, request.ElementId);
        bool replacePreset = request.PresetId != null || request.Voice != null;
        VoicePreset? preset = null;
        VoiceGeneration generation;
        if (replacePreset)
        {
            var selection = ResolveVoice(request, call.Arguments);
            preset = selection.Preset;
            generation = new()
            {
                Settings = selection.Settings, PresetId = preset?.Id, PresetName = preset?.Name
            };
        }
        else generation = VoiceRegenerationService.CaptureSettings(controller);
        generation = generation with { Text = request.Text ?? controller.Text.Value };
        var query = GetAnalysisQuery(request.Analysis, generation.Text, generation.Settings)
            ?? VoiceRegenerationService.CaptureQuery(controller, generation);
        await VoiceRegenerationService.RegenerateAsync(controller, generation, query,
            () => GetEditor(call.EditorContext, scene), cancellationToken, replacePreset, preset);
        return Json(DescribeSpeech(scene, element));
    }

    private static async Task<McpToolResult> GenerateScript(McpToolCall call, CancellationToken cancellationToken)
    {
        var request = call.Arguments.Deserialize<ScriptRequest>(s_json)!;
        var (scene, history) = GetEditor(call.EditorContext);
        double start = request.StartSeconds ?? call.EditorContext!.GetRequiredService<IEditorClock>().CurrentTime.Value.ToSeconds();
        var presets = VoicePresetStore.Load(VoicePresetStore.GlobalPath).ToDictionary(preset => preset.Id);
        var lines = ScriptLine.Parse(request.Script, request.HasSpeakerNames).Select(line =>
        {
            if (request.SpeakerPresets == null || !request.SpeakerPresets.TryGetValue(line.Speaker, out var id))
                throw new ArgumentException($"Assign a preset to speaker '{line.Speaker}' in speakerPresets.");
            if (!presets.TryGetValue(id, out var preset))
                throw new ArgumentException($"Voice preset '{id}' was not found. Call voice_list_presets.");
            return new VoiceScriptLine(line.Text, line.Speaker, preset);
        }).ToArray();
        var elements = await VoiceScriptService.GenerateAsync(scene, history, lines, start, request.GapSeconds,
            request.FirstLayer, request.SeparateLayers,
            (text, settings, token) => VoiceSynthesisService.SynthesizeAsync(text, settings, null, token),
            cancellationToken, validateEditor: () => { _ = GetEditor(call.EditorContext, scene); });
        return Json(new { sceneId = scene.Id.Value, elements = elements.Select(element => DescribeSpeech(scene, element)).ToArray() });
    }

    private static (Element Element, TtsController Controller) GetSpeech(Scene scene, Guid id)
    {
        var element = scene.Children.FirstOrDefault(item => item.Id.Value == id);
        var controller = element?.Objects.OfType<TtsController>().SingleOrDefault();
        if (element == null || controller == null)
            throw new ArgumentException($"Speech element '{id}' was not found in the selected scene.");
        return (element, controller);
    }

    private static object DescribeSpeech(Scene scene, Element element)
    {
        var generation = element.Objects.OfType<TtsController>().Single().Generation.Value;
        return new
        {
            sceneId = scene.Id.Value, elementId = element.Id.Value,
            audioPath = element.Objects.OfType<SourceSound>().First().Source.Value?.Uri.LocalPath,
            startSeconds = element.Start.Value.ToSeconds(), durationSeconds = element.Length.Value.ToSeconds(),
            layer = element.ZIndex.Value, presetId = generation?.PresetId, presetName = generation?.PresetName,
            settings = generation?.Settings
        };
    }

    private sealed record RegenerateRequest : VoiceRequest
    {
        public required Guid ElementId { get; init; }
        public string? Text { get; init; }
        public VoiceAnalysis? Analysis { get; init; }
    }

    private sealed record ScriptRequest
    {
        public required string Script { get; init; }
        public required Dictionary<string, Guid> SpeakerPresets { get; init; }
        public bool HasSpeakerNames { get; init; } = true;
        public double? StartSeconds { get; init; }
        public double GapSeconds { get; init; } = 0.2;
        public int FirstLayer { get; init; } = 1;
        public bool SeparateLayers { get; init; }
    }
}
