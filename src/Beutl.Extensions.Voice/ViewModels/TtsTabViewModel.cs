using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Beutl.Audio.Effects;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Logging;
using Beutl.ProjectSystem;
using Beutl.Editor;
using Beutl.Editor.Services;
using Beutl.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public partial class TtsTabViewModel : VoiceEditorViewModel, IToolContext
{
    private readonly Scene _scene;
    private readonly ILogger _logger = Log.CreateLogger<TtsTabViewModel>();
    private readonly IReadOnlyReactiveProperty<MediaTime> _currentTime;
    private readonly HistoryManager _historyManager;
    private readonly Func<string, VoiceSettings, AudioQueryModel?, Task<VoiceSynthesisResult>> _synthesize;

    public TtsTabViewModel(TtsTabExtension extension, IEditorContext editorContext)
        : this(extension, editorContext, (text, settings, query) => VoiceSynthesisService.SynthesizeAsync(text, settings, query)) { }

    internal TtsTabViewModel(TtsTabExtension extension, IEditorContext editorContext,
        Func<string, VoiceSettings, AudioQueryModel?, Task<VoiceSynthesisResult>> synthesize,
        Func<IEnumerable<ObjectTemplateItem>>? templates = null)
    {
        _currentTime = editorContext.GetRequiredService<IEditorClock>().CurrentTime;
        _scene = editorContext.GetRequiredService<Scene>();
        _historyManager = editorContext.GetRequiredService<HistoryManager>();
        _synthesize = synthesize;
        Extension = extension;
        LipSync = new LipSyncSelectionViewModel(templates);
        Subtitles = new SubtitleSelectionViewModel(_scene, templates);
        Subtitles.Refresh();
        WebVoices = new WebVoiceAssignmentsViewModel(_scene);
        InitializePresets();
        Script = new ScriptImportViewModel(editorContext,
            (text, settings) => synthesize(text, settings, null));
    }

    public ToolTabExtension Extension { get; }
    public IReactiveProperty<bool> IsSelected { get; } = new ReactiveProperty<bool>();
    public IReadOnlyReactiveProperty<string> Header { get; } = new ReactivePropertySlim<string>("テキスト読み上げ");
    public ReactivePropertySlim<int> SelectedPage { get; } = new();
    internal VoiceDictionaryViewModel Dictionary { get; } = new();
    public ScriptImportViewModel Script { get; }
    public LipSyncSelectionViewModel LipSync { get; }
    public SubtitleSelectionViewModel Subtitles { get; }
    public WebVoiceAssignmentsViewModel WebVoices { get; }

    private static void ShowError(string title, string message) =>
        Dispatcher.UIThread.Post(() => NotificationService.ShowError(title, message));

    public async Task Generate()
    {
        if (IsGenerating.Value || CaptureRequest() is not { } request) return;
        var preset = _appliedPreset;
        var query = CopyCurrentQuery();
        var start = _currentTime.Value;
        IsGenerating.Value = true;
        string? path = null;
        bool committed = false;
        try
        {
            var lipSync = LipSync.CapturePreset();
            var subtitleTemplate = VoiceSubtitles.Load(Subtitles.CaptureTemplate());
            Message.Value = "";
            string directory = VoiceSynthesisService.GetOutputDirectory(_scene);
            var output = await _synthesize(request.Text, request.Settings, query);
            if (_disposed) return;
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"{Guid.NewGuid()}.wav");
            await File.WriteAllBytesAsync(path, output.Wave);
            if (_disposed) return;
            var generation = new VoiceGeneration
            {
                Text = request.Text, Settings = output.Settings, PresetId = preset?.Id,
                PresetName = preset?.Name, AudioQueryJson = output.Json
            };
            var element = VoiceElementFactory.Create(_scene, path, generation, start, 1, output.Resampling);
            _historyManager.ExecuteInTransaction(() => VoicePlacement.Place(_scene, element, lipSync, subtitleTemplate));
            committed = true;
            PublishQuery(request.Text, request.Settings, output.Query);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate TTS");
            Message.Value = $"音声生成に失敗しました: {ex.Message}";
            ShowError("音声生成に失敗しました。", ex.Message);
        }
        finally
        {
            if (!committed && path != null)
                try { File.Delete(path); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not delete generated speech file {Path}", path); }
            IsGenerating.Value = false;
        }
    }

    public async Task Play()
    {
        if (IsGenerating.Value || CaptureRequest() is not { } request) return;
        var query = CopyCurrentQuery();
        IsGenerating.Value = true;
        try
        {
            var output = await _synthesize(request.Text, request.Settings, query);
            if (_disposed) return;
            PublishQuery(request.Text, request.Settings, output.Query);
            using var player = new SimpleWavePlayer(PreviewWave(output));
            await player.Play(default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to play TTS");
            Message.Value = $"音声再生に失敗しました: {ex.Message}";
            ShowError("音声再生に失敗しました。", ex.Message);
        }
        finally
        {
            IsGenerating.Value = false;
        }
    }

    // Speech that is resampled on the timeline plays converted to the project's rate in the same way.
    internal byte[] PreviewWave(VoiceSynthesisResult output) => output.Resampling switch
    {
        null => output.Wave,
        ResamplingMethod.Lagrange => VoiceWave.ResampleLagrange(output.Wave, _scene.FindHierarchicalParent<Project>().GetSampleRate()),
        var method => throw new NotSupportedException($"試聴では{method}でのリサンプリングに対応していません。")
    };

    public void WriteToJson(JsonObject json)
    {
        json["SelectedPageId"] = new[] { "speech", "script", "dictionary", "settings" }[Math.Clamp(SelectedPage.Value, 0, 3)];
        // Keep the legacy numeric field meaningful to older extension versions.
        json[nameof(SelectedPage)] = SelectedPage.Value == 3 ? 2 : SelectedPage.Value == 2 ? 0 : SelectedPage.Value;
        Script.WriteToJson(json);
        json[nameof(Text)] = Text.Value;
        json["VoiceSettings"] = JsonSerializer.SerializeToNode(CaptureStoredSettings());
        json["SelectedPresetId"] = SelectedPreset.Value?.Id.ToString();
        json["AppliedPreset"] = JsonSerializer.SerializeToNode(_appliedPreset);
        json["LipSyncTemplateId"] = LipSync.SelectedId?.ToString();
        json["LipSyncPreset"] = JsonSerializer.SerializeToNode(LipSync.Preset);
        json["SubtitleTemplateId"] = Subtitles.SelectedId?.ToString();
        json["SubtitleTemplate"] = JsonSerializer.SerializeToNode(Subtitles.Template);
    }

    public async void ReadFromJson(JsonObject json)
    {
        int legacyPage = Math.Clamp((int?)json[nameof(SelectedPage)] ?? 0, 0, 2);
        SelectedPage.Value = (string?)json["SelectedPageId"] switch
        {
            "script" => 1, "dictionary" => 2, "settings" => 3, "speech" => 0,
            _ => legacyPage == 2 ? 3 : legacyPage
        };
        Script.ReadFromJson(json);
        Subtitles.SelectedId = Guid.TryParse((string?)json["SubtitleTemplateId"], out var subtitleId) ? subtitleId : null;
        if (json["SubtitleTemplate"]?.Deserialize<VoiceSubtitleTemplate>() is { } subtitleTemplate) Subtitles.Template = subtitleTemplate;
        // A character chosen from the timeline by an older version is not offered as a template.
        LipSync.SelectedId = Guid.TryParse((string?)json["LipSyncTemplateId"], out var lipSyncId) ? lipSyncId : null;
        if (json["LipSyncPreset"]?.Deserialize<VoiceLipSyncPreset>() is { } lipSyncPreset) LipSync.Preset = lipSyncPreset;
        Text.Value = (string?)json[nameof(Text)] ?? "";
        if (Guid.TryParse((string?)json["SelectedPresetId"], out var id))
            SelectedPreset.Value = Presets.Value.FirstOrDefault(x => x.Id == id);
        var settings = json["VoiceSettings"]?.Deserialize<VoiceSettings>();
        // Only name-based migration needs Core metadata; ID-based settings can be retained until available.
        if (settings == null) await _initTcs.Task;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // A newer HTTP selection supersedes a pending legacy Core migration.
            if (_disposed || settings == null && IsExternalEngine.Value) return;
            if (settings != null) ApplySettings(settings);
            else
            {
                // Migrate name-only tab state only when it identifies a single voice/style.
                var voices = Voice.Value.Where(x => x.Name == (string?)json[nameof(SelectedVoice)]).ToArray();
                SelectedVoice.Value = voices.Length == 1 ? voices[0] : null;
                var styles = SelectedVoice.Value?.Styles.Where(x => x.Name == (string?)json[nameof(SelectedStyle)]).ToArray();
                SelectedStyle.Value = styles?.Length == 1 ? styles[0] : null;
            }
            // Older state records only the selection, which does not establish an applied preset.
            _appliedPreset = json["AppliedPreset"]?.Deserialize<VoicePreset>();
        });
    }

    public object? GetService(Type serviceType) => null;

    public override void Dispose()
    {
        Dictionary.Dispose();
        Script.Dispose();
        WebVoices.Dispose();
        base.Dispose();
    }
}
