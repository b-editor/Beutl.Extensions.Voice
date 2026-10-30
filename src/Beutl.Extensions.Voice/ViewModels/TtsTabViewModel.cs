using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
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
        Func<string, VoiceSettings, AudioQueryModel?, Task<VoiceSynthesisResult>> synthesize)
    {
        _currentTime = editorContext.GetRequiredService<IEditorClock>().CurrentTime;
        _scene = editorContext.GetRequiredService<Scene>();
        _historyManager = editorContext.GetRequiredService<HistoryManager>();
        _synthesize = synthesize;
        Extension = extension;
        InitializePresets();
        Script = new ScriptImportViewModel(editorContext,
            (text, settings) => synthesize(text, settings, null));
    }

    public ToolTabExtension Extension { get; }
    public IReactiveProperty<bool> IsSelected { get; } = new ReactiveProperty<bool>();
    public IReadOnlyReactiveProperty<string> Header { get; } = new ReactivePropertySlim<string>("テキスト読み上げ");
    public ReactivePropertySlim<int> SelectedPage { get; } = new();
    public ScriptImportViewModel Script { get; }

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
            var element = VoiceElementFactory.Create(_scene, path, generation, start, 1);
            _historyManager.ExecuteInTransaction(() =>
            {
                _scene.AddChild(element, ElementOverlapHandling.ZIndex);
            });
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
            using var player = new SimpleWavePlayer(output.Wave);
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

    public void WriteToJson(JsonObject json)
    {
        json[nameof(SelectedPage)] = SelectedPage.Value;
        Script.WriteToJson(json);
        json[nameof(Text)] = Text.Value;
        json["VoiceSettings"] = JsonSerializer.SerializeToNode(CaptureStoredSettings());
        json["SelectedPresetId"] = SelectedPreset.Value?.Id.ToString();
        json["AppliedPreset"] = JsonSerializer.SerializeToNode(_appliedPreset);
    }

    public async void ReadFromJson(JsonObject json)
    {
        SelectedPage.Value = Math.Clamp((int?)json[nameof(SelectedPage)] ?? 0, 0, 2);
        Script.ReadFromJson(json);
        Text.Value = (string?)json[nameof(Text)] ?? "";
        if (Guid.TryParse((string?)json["SelectedPresetId"], out var id))
            SelectedPreset.Value = Presets.Value.FirstOrDefault(x => x.Id == id);
        var settings = json["VoiceSettings"]?.Deserialize<VoiceSettings>();
        // Only name-based migration needs Core metadata; ID-based settings can be retained until available.
        if (settings == null) await _initTcs.Task;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // A newer HTTP selection supersedes a pending legacy Core migration.
            if (_disposed || settings == null && IsHttpEngine.Value) return;
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
        Script.Dispose();
        base.Dispose();
    }
}
