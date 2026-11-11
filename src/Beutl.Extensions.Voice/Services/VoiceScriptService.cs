using Beutl.Editor;
using Beutl.Extensions.Voice.Models;
using Beutl.Logging;
using Beutl.ProjectSystem;
using Microsoft.Extensions.Logging;

namespace Beutl.Extensions.Voice.Services;

internal sealed record VoiceScriptLine(string Text, string Speaker, VoicePreset Preset);

internal static class VoiceScriptService
{
    private static readonly ILogger s_logger = Log.CreateLogger(typeof(VoiceScriptService));

    public static async Task<Element[]> GenerateAsync(Scene scene, HistoryManager history,
        IReadOnlyList<VoiceScriptLine> lines, double startSeconds, double gapSeconds, int firstLayer, bool separateLayers,
        Func<string, VoiceSettings, CancellationToken, Task<VoiceSynthesisResult>> synthesize,
        CancellationToken cancellationToken = default, Action<int, string>? progress = null, Action? validateEditor = null)
    {
        if (lines.Count == 0) throw new InvalidOperationException("台本を入力してください。");
        if (!double.IsFinite(startSeconds) || startSeconds < 0 || !double.IsFinite(gapSeconds) || gapSeconds < 0 || firstLayer < 0)
            throw new InvalidOperationException("開始位置、間隔、レイヤーには 0 以上の値を指定してください。");
        var start = MediaTime.FromSeconds(startSeconds);
        var gap = MediaTime.FromSeconds(gapSeconds);
        var requests = Prepare();
        CheckLayers();
        _ = VoiceSynthesisService.GetOutputDirectory(scene);
        var paths = new List<string>();
        var elements = new List<Element>();
        bool committed = false;
        try
        {
            for (int i = 0; i < requests.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = requests[i];
                progress?.Invoke(i, request.Line.Speaker);
                var output = await synthesize(request.Line.Text, request.Line.Preset.Settings, cancellationToken);
                CheckEditor();
                string directory = VoiceSynthesisService.GetOutputDirectory(scene);
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"{Guid.NewGuid()}.wav");
                paths.Add(path);
                await File.WriteAllBytesAsync(path, output.Wave, cancellationToken);
                CheckEditor();
                var generation = new VoiceGeneration
                {
                    Text = request.Line.Text, Settings = output.Settings, AudioQueryJson = output.Json,
                    PresetId = request.Line.Preset.Id, PresetName = request.Line.Preset.Name
                };
                var element = VoiceElementFactory.Create(scene, path, generation, start, request.Layer);
                elements.Add(element);
                start += element.Length.Value + gap;
            }
            progress?.Invoke(requests.Length, "");
            CheckLayers();
            history.ExecuteInTransaction(() =>
            {
                PsdLipSyncPresets.PlaceScript(scene, requests.Select((request, i) =>
                    (elements[i], request.Line.Preset.LipSync, VoiceSubtitles.Create(scene, elements[i], request.Subtitle))).ToArray(), firstLayer);
            }, "台本の音声を一括配置", cancellationToken: cancellationToken);
            committed = true;
            return elements.ToArray();
        }
        finally
        {
            if (!committed)
                foreach (string path in paths)
                    try { File.Delete(path); }
                    catch (Exception ex) { s_logger.LogWarning(ex, "Could not delete generated speech file {Path}", path); }
        }

        Request[] Prepare()
        {
            var speakers = lines.DistinctBy(line => line.Speaker, StringComparer.Ordinal).ToArray();
            var subtitles = new Dictionary<VoiceSubtitleTemplate, Element?>();
            return lines.Select((line, i) =>
            {
                if (string.IsNullOrWhiteSpace(line.Text)) throw new InvalidOperationException($"{i + 1} 件目のセリフを入力してください。");
                int layer = line.Preset.LipSync == null && separateLayers
                    ? checked(firstLayer + speakers.TakeWhile(speaker => speaker.Speaker != line.Speaker)
                        .Sum(speaker => speaker.Preset is { LipSync: null, Subtitle: not null } ? 2 : 1)) : firstLayer;
                Element? subtitle = null;
                if (line.Preset.Subtitle is { } template && !subtitles.TryGetValue(template, out subtitle))
                    subtitles.Add(template, subtitle = VoiceSubtitles.Load(template));
                return new Request(line, layer, subtitle);
            }).ToArray();
        }

        void CheckEditor()
        {
            cancellationToken.ThrowIfCancellationRequested();
            validateEditor?.Invoke();
        }

        void CheckLayers()
        {
            // Subtitle pairs use the planner's available-layer search; plain audio keeps its explicit layer.
            foreach (int layer in requests.Where(request => request.Line.Preset.LipSync == null).GroupBy(request => request.Layer)
                         .Where(group => group.All(request => request.Line.Preset.Subtitle == null)).Select(group => group.Key))
                if (scene.IsLayerLocked(layer)) throw new InvalidOperationException($"レイヤー {layer} はロックされています。");
        }
    }

    private sealed record Request(VoiceScriptLine Line, int Layer, Element? Subtitle);
}
