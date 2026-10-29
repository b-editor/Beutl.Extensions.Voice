using System.Reactive.Disposables;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Beutl.Audio;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Logging;
using Beutl.Media.Source;
using Beutl.ProjectSystem;
using Beutl.Utilities;
using Beutl.Editor;
using Beutl.Editor.Services;
using Beutl.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;
using VoicevoxCoreSharp.Core.Enum;
using VoicevoxCoreSharp.Core.Struct;

namespace Beutl.Extensions.Voice.ViewModels;

public partial class TtsTabViewModel : IToolContext
{
    private readonly Scene _scene;
    private readonly ILogger _logger = Log.CreateLogger<TtsTabViewModel>();
    private readonly IReadOnlyReactiveProperty<MediaTime> _currentTime;
    private readonly HistoryManager _historyManager;
    private readonly TaskCompletionSource _initTcs = new();
    private readonly CompositeDisposable _subscriptions = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public TtsTabViewModel(TtsTabExtension extension, IEditorContext editorContext)
    {
        _currentTime = editorContext.GetRequiredService<IEditorClock>().CurrentTime;
        _scene = editorContext.GetRequiredService<Scene>();
        _historyManager = editorContext.GetRequiredService<HistoryManager>();
        Extension = extension;
        InitializePresets();
        TtsLoader.VoiceVoxLoader.Subscribe(async loader =>
        {
            if (loader == null) return;
            await loader.InitializationTcs.Task;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_disposed && ReferenceEquals(loader, TtsLoader.VoiceVoxLoader.Value)) OnLoaded();
            });
        }).AddTo(_subscriptions);
    }

    public ToolTabExtension Extension { get; }
    public IReactiveProperty<bool> IsSelected { get; } = new ReactiveProperty<bool>();
    public IReadOnlyReactiveProperty<string> Header { get; } = new ReactivePropertySlim<string>("テキスト読み上げ");
    public ReactiveProperty<string> Text { get; } = new("");
    public ReactiveProperty<VoiceMetadata[]> Voice { get; } = new([]);
    public ReactiveProperty<VoiceMetadata?> SelectedVoice { get; } = new();
    public ReactiveProperty<VoiceStyle?> SelectedStyle { get; } = new();
    public ReactiveProperty<bool> IsGenerating { get; } = new();
    public ReactiveProperty<bool> IsEnabled { get; } = new();
    public ReactiveProperty<bool> IsVoiceVoxInstalled { get; } = new(true);
    public ReactiveProperty<AudioQueryModel?> AudioQuery { get; } = new();
    public ReactiveProperty<bool> HasAudioQuery { get; } = new();
    public ReactiveProperty<double> SpeedScale { get; } = new(1.0);
    public ReactiveProperty<double> PitchScale { get; } = new(0.0);
    public ReactiveProperty<double> IntonationScale { get; } = new(1.0);
    public ReactiveProperty<double> VolumeScale { get; } = new(1.0);
    public ReactiveProperty<double> PrePhonemeLength { get; } = new(0.1);
    public ReactiveProperty<double> PostPhonemeLength { get; } = new(0.1);

    public void OnLoaded()
    {
        try
        {
            var loader = TtsLoader.VoiceVoxLoader.Value;
            IsVoiceVoxInstalled.Value = loader?.IsInstalled == true;
            IsEnabled.Value = loader?.IsLoaded == true;
            if (loader?.IsLoaded != true) return;
            var previous = CaptureSettings() ?? _unavailableSettings;
            Voice.Value = loader.VoiceSets.SelectMany(x => x.Metadata)
                .GroupBy(x => x.SpeakerUuid)
                .Select(group => new VoiceMetadata
                {
                    Name = group.First().Name,
                    Version = group.First().Version,
                    SpeakerUuid = group.Key,
                    Styles = group.SelectMany(x => x.Styles).ToArray()
                }).ToArray();
            if (previous != null) ApplySettings(previous);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize VOICEVOX");
            ShowError("VOICEVOXの初期化に失敗しました。", ex.Message);
        }
        finally
        {
            _initTcs.TrySetResult();
        }
    }

    private (string Text, VoiceSettings Settings)? CaptureRequest()
    {
        if (string.IsNullOrWhiteSpace(Text.Value))
        {
            ShowWarning("テキストを入力してください。");
            return null;
        }
        if (CaptureSettings() is not { } settings)
        {
            ShowWarning("話者とスタイルを選択してください。");
            return null;
        }
        return (Text.Value, settings);
    }

    public async Task CreateQuery()
    {
        if (IsGenerating.Value || CaptureRequest() is not { } request) return;
        IsGenerating.Value = true;
        try
        {
            var query = await Task.Run(() => CreateQueryCore(request.Text, request.Settings));
            PublishQuery(request.Text, request.Settings, query);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create AudioQuery");
            ShowError("AudioQueryの作成に失敗しました。", ex.Message);
        }
        finally
        {
            IsGenerating.Value = false;
        }
    }

    private AudioQueryModel CreateQueryCore(string text, VoiceSettings settings)
    {
        var loader = TtsLoader.VoiceVoxLoader.Value;
        if (loader?.Synthesizer == null || !loader.EnsureVoiceModelLoaded(settings.StyleId))
            throw new InvalidOperationException("VOICEVOXの音声モデルを利用できません。");
        var result = loader.Synthesizer.CreateAudioQuery(text, settings.StyleId, out var json);
        if (result != ResultCode.RESULT_OK) throw new InvalidOperationException(result.ToMessage());
        return JsonSerializer.Deserialize<AudioQueryModel>(json!, s_jsonOptions)
            ?? throw new JsonException("VOICEVOXの応答を解析できませんでした。");
    }

    private void PublishQuery(string text, VoiceSettings settings, AudioQueryModel query)
    {
        if (_disposed || Text.Value != text || CaptureSettings()?.UsesSameVoice(settings) != true) return;
        AudioQuery.Value = query;
        HasAudioQuery.Value = true;
    }

    private static void ShowWarning(string message) =>
        Dispatcher.UIThread.Post(() => NotificationService.ShowWarning("警告", message));

    private static void ShowError(string title, string message) =>
        Dispatcher.UIThread.Post(() => NotificationService.ShowError(title, message));

    // Snapshot mutable accent edits on the UI thread before synthesis starts.
    private AudioQueryModel? CopyCurrentQuery() => AudioQuery.Value is { } query
        ? JsonSerializer.Deserialize<AudioQueryModel>(JsonSerializer.Serialize(query, s_jsonOptions), s_jsonOptions)
        : null;

    private (byte[] Wave, AudioQueryModel Query, string Json) Synthesize(string text, VoiceSettings settings, AudioQueryModel? query)
    {
        query ??= CreateQueryCore(text, settings);
        settings.ApplyTo(query);
        string json = JsonSerializer.Serialize(query, s_jsonOptions);
        var loader = TtsLoader.VoiceVoxLoader.Value;
        if (loader?.Synthesizer == null || !loader.EnsureVoiceModelLoaded(settings.StyleId))
            throw new InvalidOperationException("VOICEVOXの音声モデルを利用できません。");
        var result = loader.Synthesizer.Synthesis(json, settings.StyleId, SynthesisOptions.Default(), out _, out var wave);
        if (result != ResultCode.RESULT_OK || wave == null) throw new InvalidOperationException(result.ToMessage());
        return (wave, query, json);
    }

    public async Task Generate()
    {
        if (IsGenerating.Value || CaptureRequest() is not { } request) return;
        var preset = SelectedPreset.Value;
        var query = CopyCurrentQuery();
        var start = _currentTime.Value;
        IsGenerating.Value = true;
        try
        {
            var uri = _scene.FindHierarchicalParent<Project>()?.Uri ?? _scene.Uri;
            if (uri is not { IsFile: true }) throw new InvalidOperationException("先にプロジェクトまたはシーンを保存してください。");
            string directory = Path.Combine(Path.GetDirectoryName(uri.LocalPath)!, "resources", "tts");
            var output = await Task.Run(() => Synthesize(request.Text, request.Settings, query));
            if (_disposed) return;
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{Guid.NewGuid()}.wav");
            await File.WriteAllBytesAsync(path, output.Wave);
            if (_disposed) return;
            var generation = new VoiceGeneration
            {
                Text = request.Text, Settings = request.Settings, PresetId = preset?.Id,
                PresetName = preset?.Name, AudioQueryJson = output.Json
            };
            var source = SoundSource.Open(path);
            _historyManager.ExecuteInTransaction(() =>
            {
                var element = new Element
                {
                    Start = { Value = start }, ZIndex = { Value = 1 },
                    Uri = RandomFileNameGenerator.GenerateUri(_scene.Uri!, "belm"),
                    Name = { Value = request.Text.ReplaceLineEndings(" ") },
                    AccentColor = { Value = ElementAccentColorGenerator.GenerateColor(typeof(TtsController).FullName!) }
                };
                element.AddObject(new SourceSound { Source = { Value = source } });
                element.AddObject(new TtsController { Text = { Value = request.Text }, Generation = { Value = generation } });
                if (element.TryGetOriginalDuration(out var duration)) element.Length.Value = duration;
                _scene.AddChild(element, ElementOverlapHandling.ZIndex);
            });
            PublishQuery(request.Text, request.Settings, output.Query);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate TTS");
            ShowError("音声生成に失敗しました。", ex.Message);
        }
        finally
        {
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
            var output = await Task.Run(() => Synthesize(request.Text, request.Settings, query));
            if (_disposed) return;
            PublishQuery(request.Text, request.Settings, output.Query);
            using var player = new SimpleWavePlayer(output.Wave);
            await player.Play(default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to play TTS");
            ShowError("音声再生に失敗しました。", ex.Message);
        }
        finally
        {
            IsGenerating.Value = false;
        }
    }

    public void OnAccentPhrasesUpdated() => AudioQuery.ForceNotify();

    public void Dispose()
    {
        _disposed = true;
        _subscriptions.Dispose();
        _initTcs.TrySetResult();
    }

    public void WriteToJson(JsonObject json)
    {
        json[nameof(Text)] = Text.Value;
        json["VoiceSettings"] = JsonSerializer.SerializeToNode(CaptureSettings() ?? _unavailableSettings);
        json["SelectedPresetId"] = SelectedPreset.Value?.Id.ToString();
    }

    public async void ReadFromJson(JsonObject json)
    {
        Text.Value = (string?)json[nameof(Text)] ?? "";
        if (Guid.TryParse((string?)json["SelectedPresetId"], out var id))
            SelectedPreset.Value = Presets.Value.FirstOrDefault(x => x.Id == id);
        await _initTcs.Task;
        if (_disposed) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (json["VoiceSettings"]?.Deserialize<VoiceSettings>() is { } settings) ApplySettings(settings);
            else
            {
                // Migrate name-only tab state only when it identifies a single voice/style.
                var voices = Voice.Value.Where(x => x.Name == (string?)json[nameof(SelectedVoice)]).ToArray();
                SelectedVoice.Value = voices.Length == 1 ? voices[0] : null;
                var styles = SelectedVoice.Value?.Styles.Where(x => x.Name == (string?)json[nameof(SelectedStyle)]).ToArray();
                SelectedStyle.Value = styles?.Length == 1 ? styles[0] : null;
            }
        });
    }

    public object? GetService(Type serviceType) => null;
}
