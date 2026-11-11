using System.Text.Json;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.ProjectSystem;

namespace Beutl.Extensions.Voice;

public sealed partial class VoiceMcpToolExtension
{
    private static object DescribePreset(VoicePreset preset) => new
    {
        presetId = preset.Id, preset.Name, preset.Settings,
        hasLipSync = preset.LipSync != null, subtitleName = preset.Subtitle?.Name
    };

    private static async Task<McpToolResult> RegisterEngine(JsonElement arguments, CancellationToken cancellationToken)
    {
        string url = HttpVoiceEngine.NormalizeUrl(arguments.GetProperty("url").GetString()!);
        string name = arguments.TryGetProperty("name", out var value) ? value.GetString()?.Trim() ?? "" : "";
        var existing = VoiceEngineStore.FindByUrl(VoiceEngineStore.Load(), url);
        var discovered = await HttpVoiceEngine.DiscoverAsync(existing ?? new() { Name = name, Url = url }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var connection = VoiceEngineStore.RegisterDiscovered(discovered, allowCreate: existing == null);
        return Json(new
        {
            engineId = connection.Id, connection.Name, connection.Url, connection.Version,
            connection.Capabilities, voiceCount = connection.Voices.Length
        });
    }

    private static McpToolResult SavePreset(McpToolCall call)
    {
        var request = call.Arguments.Deserialize<SavePresetRequest>(s_json)!;
        if (request.Voice != null && request.ElementId != null)
            throw new ArgumentException("Supply either voice or elementId, not both.");
        if (request.Name != null && string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Enter a preset name.");
        bool setLipSync = call.Arguments.TryGetProperty("lipSyncTargetId", out _) || request.ElementId != null;
        bool setSubtitle = call.Arguments.TryGetProperty("subtitleElementId", out _) || request.UseDefaultSubtitle || request.ElementId != null;
        if (request.UseDefaultSubtitle && call.Arguments.TryGetProperty("subtitleElementId", out _))
            throw new ArgumentException("Supply either useDefaultSubtitle or subtitleElementId.");
        VoiceSettings? settings = request.Voice;
        if (settings != null) ValidateVoice(settings, call.Arguments.GetProperty("voice"));
        VoiceLipSyncPreset? lipSync = null;
        VoiceSubtitleTemplate? subtitle = null;
        if (request.ElementId != null || request.LipSyncTargetId != null || request.SubtitleElementId != null || request.UseDefaultSubtitle)
        {
            var (scene, _) = GetEditor(call.EditorContext);
            if (request.ElementId is { } id)
            {
                var (speech, controller) = GetSpeech(scene, id);
                settings = VoiceRegenerationService.CaptureSettings(controller).Settings;
                var target = controller.LipSyncGroupId.Value == Guid.Empty ? null : PsdLipSync.Targets(scene)
                    .Where(item => PsdLipSync.GroupOf(item) == controller.LipSyncGroupId.Value)
                    .OrderByDescending(item => item.TimeRange.Value.Intersects(speech.Range)).FirstOrDefault();
                if (target != null) lipSync = PsdLipSyncPresets.Capture(target);
                var caption = scene.EnumerateAllChildren<SubtitleController>()
                    .FirstOrDefault(item => item.SourceId.Value == controller.Id.Value)?.FindHierarchicalParent<Element>();
                if (caption != null) subtitle = VoiceSubtitles.Capture(caption);
            }
            if (call.Arguments.TryGetProperty("lipSyncTargetId", out _))
            {
                lipSync = request.LipSyncTargetId is { } targetId
                    ? PsdLipSyncPresets.Capture(PsdLipSync.Targets(scene).FirstOrDefault(item => item.Id.Value == targetId)
                        ?? throw new ArgumentException($"Lip sync target '{targetId}' was not found. Call voice_set_lip_sync to list targets."))
                    : null;
            }
            if (call.Arguments.TryGetProperty("subtitleElementId", out _))
            {
                subtitle = request.SubtitleElementId is { } captionId
                    ? VoiceSubtitles.Capture(scene.Children.FirstOrDefault(item => item.Id.Value == captionId)
                        ?? throw new ArgumentException($"Subtitle element '{captionId}' was not found."))
                    : null;
            }
            if (request.UseDefaultSubtitle) subtitle = VoiceSubtitles.Default(scene);
        }
        // Explicit null also clears associations when no scene is open.
        VoicePreset Build(VoicePreset current) => current with
        {
            Name = request.Name?.Trim() ?? current.Name,
            Settings = settings ?? current.Settings,
            LipSync = setLipSync ? lipSync : current.LipSync,
            Subtitle = setSubtitle ? subtitle : current.Subtitle
        };
        VoicePreset saved;
        if (request.PresetId is { } presetId)
        {
            VoicePreset? updated = null;
            VoicePresetStore.Update(VoicePresetStore.GlobalPath, presetId, current => updated = Build(current));
            saved = updated!;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.Name) || settings == null)
                throw new ArgumentException("Creating a preset requires name and either voice or elementId.");
            saved = Build(new VoicePreset());
            VoicePresetStore.Save(VoicePresetStore.GlobalPath, saved);
        }
        return Json(new { preset = DescribePreset(saved) });
    }

    private static McpToolResult SetLipSync(McpToolCall call)
    {
        var (scene, history) = GetEditor(call.EditorContext);
        var (element, controller) = GetSpeech(scene, call.Arguments.GetProperty("elementId").GetGuid());
        if (call.Arguments.TryGetProperty("targetId", out var id))
        {
            if (scene.IsElementLocked(element))
                throw new InvalidOperationException("読み上げ要素またはレイヤーがロックされています。");
            if (id.ValueKind == JsonValueKind.Null)
                history.ExecuteInTransaction(() => PsdLipSync.Disconnect(controller, scene), "口パク連携を解除");
            else
            {
                var targetId = id.GetGuid();
                var target = PsdLipSync.Targets(scene).FirstOrDefault(item => item.Id.Value == targetId)
                    ?? throw new ArgumentException($"Lip sync target '{targetId}' was not found. Omit targetId to list targets.");
                history.ExecuteInTransaction(() => PsdLipSync.Connect(controller, target), "口パクを連携");
            }
        }
        return Json(new
        {
            elementId = element.Id.Value, groupId = controller.LipSyncGroupId.Value,
            targets = PsdLipSync.Targets(scene).Select(target => new
            {
                targetId = target.Id.Value, name = PsdLipSync.TargetName(target),
                elementId = target.FindHierarchicalParent<Element>()!.Id.Value,
                linked = controller.LipSyncGroupId.Value != Guid.Empty && PsdLipSync.GroupOf(target) == controller.LipSyncGroupId.Value
            }).ToArray()
        });
    }

    private sealed record SavePresetRequest
    {
        public Guid? PresetId { get; init; }
        public string? Name { get; init; }
        public Guid? ElementId { get; init; }
        public VoiceSettings? Voice { get; init; }
        public Guid? LipSyncTargetId { get; init; }
        public Guid? SubtitleElementId { get; init; }
        public bool UseDefaultSubtitle { get; init; }
    }
}
