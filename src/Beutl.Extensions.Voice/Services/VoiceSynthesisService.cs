using System.Text.Json;
using Beutl.Extensions.Voice.Models;
using Beutl.ProjectSystem;
using VoicevoxCoreSharp.Core.Enum;
using VoicevoxCoreSharp.Core.Struct;

namespace Beutl.Extensions.Voice.Services;

internal sealed record VoiceSynthesisResult(byte[] Wave, AudioQueryModel Query, string Json, VoiceSettings Settings);

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

    public static AudioQueryModel CreateQuery(string text, VoiceSettings settings)
    {
        var (loader, _) = ResolveVoice(settings);
        return CreateQuery(loader, text, settings.StyleId);
    }

    public static VoiceSynthesisResult Synthesize(
        string text, VoiceSettings settings, AudioQueryModel? query)
    {
        var (loader, resolved) = ResolveVoice(settings);
        query ??= CreateQuery(loader, text, resolved.StyleId);
        resolved.ApplyTo(query);
        string json = JsonSerializer.Serialize(query, JsonOptions);
        var result = loader.Synthesizer!.Synthesis(json, resolved.StyleId, SynthesisOptions.Default(), out _, out var wave);
        if (result != ResultCode.RESULT_OK || wave == null) throw new InvalidOperationException(result.ToMessage());
        return new(wave, query, json, resolved);
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
        if (!loader.EnsureVoiceModelLoaded(settings.StyleId))
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
