using System.Text.Json;
using Beutl.Audio;
using Beutl.Audio.Effects;
using Beutl.Editor;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Logging;
using Beutl.Media.Source;
using Beutl.ProjectSystem;
using Microsoft.Extensions.Logging;

namespace Beutl.Extensions.Voice.Services;

internal static class VoiceRegenerationService
{
    private static readonly ILogger s_logger = Log.CreateLogger(typeof(VoiceRegenerationService));

    public static VoiceGeneration CaptureSettings(TtsController controller)
    {
        var draft = controller.Draft.Value;
        var preset = controller.AppliedPreset.Value;
        var generation = controller.Generation.Value;
        return new()
        {
            Text = controller.Text.Value,
            Settings = draft?.Settings ?? preset?.Settings ?? generation?.Settings
                ?? throw new InvalidOperationException("話者を選ぶか、声のプリセットを適用してください。"),
            PresetId = preset?.Id ?? draft?.PresetId ?? generation?.PresetId,
            PresetName = preset?.Name ?? draft?.PresetName ?? generation?.PresetName
        };
    }

    public static AudioQueryModel? CaptureQuery(TtsController controller, VoiceGeneration request)
    {
        var saved = new[] { controller.Draft.Value, controller.Generation.Value }.FirstOrDefault(value =>
            value != null && value.Text == request.Text && value.Settings.UsesSameVoice(request.Settings)
            && !string.IsNullOrEmpty(value.AudioQueryJson));
        if (saved == null) return null;
        try { return JsonSerializer.Deserialize<AudioQueryModel>(saved.AudioQueryJson); }
        catch (JsonException) { return null; } // Legacy snapshots can be analyzed again.
    }

    public static async Task RegenerateAsync(TtsController controller, VoiceGeneration request, AudioQueryModel? query,
        Func<(Scene Scene, HistoryManager History)> getEditor, CancellationToken cancellationToken = default,
        bool replacePreset = false, VoicePreset? preset = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Text)) throw new InvalidOperationException("テキストを入力してください。");
        var (scene, _) = getEditor();
        var element = controller.FindHierarchicalParent<Element>();
        if (element == null || element.FindHierarchicalParent<Scene>() != scene || !element.Objects.Contains(controller))
            throw new InvalidOperationException("編集する読み上げ要素が見つかりません。");
        if (scene.IsElementLocked(element))
            throw new InvalidOperationException("読み上げ要素またはレイヤーがロックされています。");
        var sound = element.Objects.OfType<SourceSound>().FirstOrDefault()
            ?? throw new InvalidOperationException("読み上げ要素の音声が見つかりません。");
        var source = sound.Source.Value;
        var previousText = controller.Text.Value;
        var previousPreset = controller.AppliedPreset.Value;
        var previousGeneration = controller.Generation.Value;
        var previousDraft = controller.Draft.Value;
        var subtitleUpdates = VoiceSubtitles.PrepareUpdate(controller, request.Text);
        _ = VoiceSynthesisService.GetOutputDirectory(scene);
        string? path = null;
        bool committed = false;
        try
        {
            var output = await VoiceSynthesisService.SynthesizeAsync(request.Text, request.Settings, query, cancellationToken);
            _ = ValidateTarget();
            string directory = VoiceSynthesisService.GetOutputDirectory(scene);
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"{Guid.NewGuid()}.wav");
            await File.WriteAllBytesAsync(path, output.Wave, cancellationToken);
            var history = ValidateTarget();
            var nextSource = SoundSource.Open(path);
            // AquesTalkPlayer speech is resampled as when it was added; a disabled resampling stays as the user left it.
            bool resample = output.Settings.IsAquesTalkPlayer
                && sound.Effect.Value is AudioEffectGroup effects && !effects.Children.OfType<ResamplingEffect>().Any();
            history.ExecuteInTransaction(() =>
            {
                if (resample) VoiceElementFactory.AddResampling(sound, AquesTalkPlayerEngine.Resampling);
                VoiceSubtitles.Update(controller, subtitleUpdates);
                sound.Source.Value = nextSource;
                controller.Text.Value = request.Text;
                if (replacePreset) controller.AppliedPreset.Value = preset;
                controller.Generation.Value = request with { Settings = output.Settings, AudioQueryJson = output.Json };
                controller.Draft.Value = controller.Generation.Value;
            }, "音声を再生成", cancellationToken: cancellationToken);
            committed = true;
        }
        finally
        {
            if (!committed && path != null)
                try { File.Delete(path); }
                catch (Exception ex) { s_logger.LogWarning(ex, "Could not delete generated speech file {Path}", path); }
        }

        HistoryManager ValidateTarget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = getEditor();
            if (current.Scene != scene || controller.FindHierarchicalParent<Element>() != element
                || element.FindHierarchicalParent<Scene>() != scene || !element.Objects.Contains(controller)
                || !element.Objects.Contains(sound) || sound.Source.Value != source
                || controller.Text.Value != previousText || controller.AppliedPreset.Value != previousPreset
                || controller.Generation.Value != previousGeneration || controller.Draft.Value != previousDraft)
                throw new InvalidOperationException("生成中に対象の設定が変わりました。もう一度再生成してください。");
            if (scene.IsElementLocked(element))
                throw new InvalidOperationException("読み上げ要素またはレイヤーがロックされています。");
            return current.History;
        }
    }
}
