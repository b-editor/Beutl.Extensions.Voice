using System.Text.Json;
using Beutl.Extensions.Voice.Models;
using Beutl.ProjectSystem;
using VoicevoxCoreSharp.Core.Enum;
using VoicevoxCoreSharp.Core.Struct;

namespace Beutl.Extensions.Voice.Services;

// Query and Json are empty for an engine that reads the text itself without an editable query.
internal sealed record VoiceSynthesisResult(byte[] Wave, AudioQueryModel? Query, string Json, VoiceSettings Settings);

internal static class VoiceSynthesisService
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string GetOutputDirectory(Scene scene)
    {
        var uri = scene.FindHierarchicalParent<Project>()?.Uri ?? scene.Uri;
        if (uri is not { IsFile: true })
            throw new InvalidOperationException("先にプロジェクトまたはシーンを保存してください。");
        return Path.Combine(Path.GetDirectoryName(uri.LocalPath)!, "resources", "tts");
    }

    public static Task<AudioQueryModel> CreateQueryAsync(string text, VoiceSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (settings.IsAquesTalkPlayer)
            return Task.Run(() =>
            {
                var (loader, styleId) = ResolveAnalysisVoice();
                return CreateQuery(loader, text, styleId);
            }, cancellationToken);
        if (settings.EngineId != VoiceSettings.CoreEngineId)
            return HttpVoiceEngine.CreateQueryAsync(text, settings, cancellationToken);
        return Task.Run(() => CreateCoreQuery(text, settings), cancellationToken);
    }

    // AquesTalkPlayer reads the text itself unless it is given an analysis.
    public static Task<VoiceSynthesisResult> SynthesizeAsync(string text, VoiceSettings settings, AudioQueryModel? query,
        CancellationToken cancellationToken = default)
    {
        if (settings.IsAquesTalkPlayer)
            return AquesTalkPlayerEngine.SynthesizeAsync(text, settings, query, cancellationToken);
        if (settings.EngineId != VoiceSettings.CoreEngineId)
            return HttpVoiceEngine.SynthesizeAsync(text, settings, query, cancellationToken);
        return Task.Run(() => SynthesizeCore(text, settings, query), cancellationToken);
    }

    public static Task<List<AccentPhraseModel>> ReplaceMoraPitchAsync(List<AccentPhraseModel> phrases,
        VoiceSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.EngineId != VoiceSettings.CoreEngineId && !settings.IsAquesTalkPlayer)
            return HttpVoiceEngine.ReplaceMoraPitchAsync(phrases, settings, cancellationToken);
        return Task.Run(() =>
        {
            // AquesTalkPlayer's pitches only draw its accents, so the voice that analyzed them draws them.
            var (loader, styleId) = settings.IsAquesTalkPlayer
                ? ResolveAnalysisVoice() : (ResolveVoice(settings).Loader, checked((uint)settings.StyleId));
            var result = loader.Synthesizer!.ReplaceMoraPitch(JsonSerializer.Serialize(phrases, JsonOptions),
                styleId, out var json);
            if (result != ResultCode.RESULT_OK) throw new InvalidOperationException(result.ToMessage());
            return JsonSerializer.Deserialize<List<AccentPhraseModel>>(json!, JsonOptions)
                ?? throw new JsonException("VOICEVOXの応答を解析できませんでした。");
        }, cancellationToken);
    }

    private static AudioQueryModel CreateCoreQuery(string text, VoiceSettings settings)
    {
        var (loader, _) = ResolveVoice(settings);
        return CreateQuery(loader, text, checked((uint)settings.StyleId));
    }

    private static VoiceSynthesisResult SynthesizeCore(
        string text, VoiceSettings settings, AudioQueryModel? query)
    {
        var (loader, resolved) = ResolveVoice(settings);
        query ??= CreateQuery(loader, text, checked((uint)resolved.StyleId));
        resolved.ApplyTo(query);
        string json = JsonSerializer.Serialize(query, JsonOptions);
        var result = loader.Synthesizer!.Synthesis(json, checked((uint)resolved.StyleId), SynthesisOptions.Default(), out _, out var wave);
        if (result != ResultCode.RESULT_OK || wave == null) throw new InvalidOperationException(result.ToMessage());
        return new(wave, query, json, resolved);
    }

    // Accents come from VOICEVOX's dictionary whichever voice is asked, so the first voice that loads analyzes the text
    // of engines without an analysis of their own.
    private static (VoiceVoxLoader Loader, uint StyleId) ResolveAnalysisVoice()
    {
        var loader = TtsLoader.VoiceVoxLoader.Value;
        if (loader?.IsLoaded != true || loader.Synthesizer == null)
            throw new InvalidOperationException(AquesTalkPlayerEngine.AnalysisNeedsCore);
        foreach (var style in loader.VoiceSets.SelectMany(x => x.Metadata).SelectMany(x => x.Styles).Where(x => x.Type is null or "talk"))
        {
            uint styleId = checked((uint)style.Id);
            if (loader.EnsureVoiceModelLoaded(styleId)) return (loader, styleId);
        }
        throw new InvalidOperationException(AquesTalkPlayerEngine.AnalysisNeedsCore);
    }

    private static (VoiceVoxLoader Loader, VoiceSettings Settings) ResolveVoice(VoiceSettings settings)
    {
        var loader = TtsLoader.VoiceVoxLoader.Value;
        if (loader?.IsLoaded != true || loader.Synthesizer == null)
            throw new InvalidOperationException("VOICEVOXの音声モデルを利用できません。");
        var voice = settings.EngineId == VoiceSettings.CoreEngineId
            ? loader.VoiceSets.SelectMany(x => x.Metadata).FirstOrDefault(x =>
                x.SpeakerUuid == settings.SpeakerUuid && x.Styles.Any(s => s.Id == settings.StyleId))
            : null;
        var style = voice?.Styles.FirstOrDefault(x => x.Id == settings.StyleId);
        if (voice == null || style == null)
            throw new InvalidOperationException($"保存された声が見つかりません: {settings.SpeakerName} / {settings.StyleName}。別のプリセットを適用してください。");
        if (!loader.EnsureVoiceModelLoaded(checked((uint)settings.StyleId)))
            throw new InvalidOperationException("VOICEVOXの音声モデルを読み込めませんでした。");
        return (loader, settings with { SpeakerName = voice.Name, StyleName = style.Name, ModelVersion = voice.Version });
    }

    private static AudioQueryModel CreateQuery(VoiceVoxLoader loader, string text, uint styleId)
    {
        var result = loader.Synthesizer!.CreateAudioQuery(text, styleId, out var json);
        if (result != ResultCode.RESULT_OK) throw new InvalidOperationException(result.ToMessage());
        return JsonSerializer.Deserialize<AudioQueryModel>(json!, JsonOptions)
            ?? throw new JsonException("VOICEVOXの応答を解析できませんでした。");
    }
}
