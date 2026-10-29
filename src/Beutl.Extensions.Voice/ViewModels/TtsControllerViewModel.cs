using System.Reactive.Disposables;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beutl.Audio;
using Beutl.Editor;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Logging;
using Beutl.Media.Source;
using Beutl.ProjectSystem;
using Microsoft.Extensions.Logging;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

namespace Beutl.Extensions.Voice.ViewModels;

public class TtsControllerViewModel : IPropertyEditorContext
{
    private readonly TtsController _controller;
    private readonly CompositeDisposable _subscriptions = new();
    private readonly ILogger _logger = Log.CreateLogger<TtsControllerViewModel>();
    private HistoryManager? _history;
    private bool _disposed;

    public TtsControllerViewModel(IPropertyEditorContext inner, PropertyEditorExtension extension, TtsController controller)
    {
        Inner = inner;
        Extension = extension;
        _controller = controller;
        SelectedPreset.Subscribe(value => HasSelectedPreset.Value = value != null).AddTo(_subscriptions);
        controller.AppliedPreset.GetObservable().Subscribe(_ => RefreshDetails()).AddTo(_subscriptions);
        controller.Generation.GetObservable().Subscribe(_ => RefreshDetails()).AddTo(_subscriptions);
        RefreshPresets();
    }

    public IPropertyEditorContext Inner { get; }
    public PropertyEditorExtension Extension { get; }

    public ReactivePropertySlim<VoicePreset[]> Presets { get; } = new([]);
    public ReactivePropertySlim<VoicePreset?> SelectedPreset { get; } = new();
    public ReactivePropertySlim<bool> HasSelectedPreset { get; } = new();
    public ReactivePropertySlim<bool> HasSettings { get; } = new();
    public ReactivePropertySlim<bool> HasEditor { get; } = new();
    public ReactivePropertySlim<bool> IsGenerating { get; } = new();
    public ReactivePropertySlim<string> SettingsSummary { get; } = new("");
    public ReactivePropertySlim<string> CurrentSettings { get; } = new("");
    public ReactivePropertySlim<string> GenerationSettings { get; } = new("");
    public ReactivePropertySlim<string> Message { get; } = new("");

    public void RefreshPresets()
    {
        var id = SelectedPreset.Value?.Id ?? _controller.AppliedPreset.Value?.Id ?? _controller.Generation.Value?.PresetId;
        try
        {
            Presets.Value = VoicePresetStore.Load(VoicePresetStore.GlobalPath);
            SelectedPreset.Value = Presets.Value.FirstOrDefault(x => x.Id == id);
            Message.Value = "";
        }
        catch (Exception ex)
        {
            Message.Value = $"プリセットを読み込めませんでした: {ex.Message}";
        }
    }

    public void ApplyPreset()
    {
        if (IsGenerating.Value || _disposed || SelectedPreset.Value is not { } preset) return;
        try
        {
            GetEditableTarget();
            _history!.ExecuteInTransaction(() => _controller.AppliedPreset.Value = preset);
            Message.Value = "プリセットを適用しました。「再生成」で音声に反映します。";
        }
        catch (Exception ex)
        {
            Message.Value = ex.Message;
        }
    }

    private void RefreshDetails()
    {
        var generation = _controller.Generation.Value;
        var settings = _controller.AppliedPreset.Value?.Settings ?? generation?.Settings;
        HasSettings.Value = settings != null;
        SettingsSummary.Value = settings == null ? "プリセットを適用すると再生成できます。"
            : $"使用する声: {settings.SpeakerName} / {settings.StyleName}";
        CurrentSettings.Value = settings?.Describe() ?? "未設定";
        GenerationSettings.Value = generation == null ? "生成時の設定が保存されていません。"
            : $"プリセット: {generation.PresetName ?? "なし"}\n{generation.Settings.Describe()}\n本文: {generation.Text}";
    }

    private (Element Element, Scene Scene) GetEditableTarget()
    {
        var element = _controller.FindHierarchicalParent<Element>();
        var scene = element?.FindHierarchicalParent<Scene>();
        if (_history == null || element == null || scene == null || !element.Objects.Contains(_controller))
            throw new InvalidOperationException("編集する読み上げ要素が見つかりません。");
        if (scene.IsElementLocked(element))
            throw new InvalidOperationException("読み上げ要素またはレイヤーがロックされています。");
        return (element, scene);
    }

    public async Task Regenerate()
    {
        if (IsGenerating.Value || _disposed) return;
        IsGenerating.Value = true;
        Message.Value = "";
        try
        {
            var (element, scene) = GetEditableTarget();
            var sound = element.Objects.OfType<SourceSound>().FirstOrDefault()
                ?? throw new InvalidOperationException("読み上げ要素の音声が見つかりません。");
            var source = sound.Source.Value;
            var preset = _controller.AppliedPreset.Value;
            var generation = _controller.Generation.Value;
            var settings = preset?.Settings ?? generation?.Settings
                ?? throw new InvalidOperationException("先に声のプリセットを適用してください。");
            string text = _controller.Text.Value;
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("テキストを入力してください。");

            // Reuse hand-edited accents only for the text and voice they were generated for.
            AudioQueryModel? query = null;
            if (generation != null && generation.Text == text && generation.Settings.UsesSameVoice(settings))
            {
                try { query = JsonSerializer.Deserialize<AudioQueryModel>(generation.AudioQueryJson); }
                catch (JsonException) { /* Older snapshots can be analyzed again. */ }
            }
            string directory = VoiceSynthesisService.GetOutputDirectory(scene);
            var output = await Task.Run(() => VoiceSynthesisService.Synthesize(text, settings, query));
            if (_disposed) return;
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{Guid.NewGuid()}.wav");
            await File.WriteAllBytesAsync(path, output.Wave);
            if (_disposed) return;
            var current = GetEditableTarget();
            if (current.Element != element || current.Scene != scene || !element.Objects.Contains(sound)
                || sound.Source.Value != source || _controller.Text.Value != text
                || _controller.AppliedPreset.Value != preset || _controller.Generation.Value != generation)
                throw new InvalidOperationException("生成中に対象の設定が変わりました。もう一度再生成してください。");

            var nextSource = SoundSource.Open(path);
            _history!.ExecuteInTransaction(() =>
            {
                sound.Source.Value = nextSource;
                _controller.Generation.Value = new VoiceGeneration
                {
                    Text = text, Settings = output.Settings, AudioQueryJson = output.Json,
                    PresetId = preset?.Id ?? generation?.PresetId,
                    PresetName = preset?.Name ?? generation?.PresetName
                };
            });
            Message.Value = "音声を再生成しました。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to regenerate TTS");
            Message.Value = $"再生成できませんでした: {ex.Message}";
        }
        finally
        {
            IsGenerating.Value = false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _subscriptions.Dispose();
        Inner.Dispose();
    }

    public void WriteToJson(JsonObject json)
    {
        Inner.WriteToJson(json);
    }

    public void ReadFromJson(JsonObject json)
    {
        Inner.ReadFromJson(json);
    }

    public void Accept(IPropertyEditorContextVisitor visitor)
    {
        visitor.Visit(this);
        Inner.Accept(visitor);
        if (visitor is IServiceProvider services)
        {
            _history = services.GetService(typeof(HistoryManager)) as HistoryManager;
            HasEditor.Value = _history != null;
        }
    }
}
