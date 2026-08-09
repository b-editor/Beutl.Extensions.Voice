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
using Beutl.Extensions.Voice.Internals;
using Beutl.Serialization;
using Beutl.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reactive.Bindings;
using VoicevoxCoreSharp.Core.Enum;
using VoicevoxCoreSharp.Core.Struct;

namespace Beutl.Extensions.Voice.ViewModels;

public class TtsTabViewModel : IToolContext
{
    private readonly IEditorContext _editorContext;
    private readonly Scene _scene;
    private readonly ILogger _logger = Log.CreateLogger<TtsTabViewModel>();
    private IReactiveProperty<TimeSpan> _currentTime;
    private HistoryManager _historyManager;
    private TaskCompletionSource _initTcs = new();
    private int _busy;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public TtsTabViewModel(TtsTabExtension extension, IEditorContext editorContext)
    {
        _editorContext = editorContext;
        _currentTime = editorContext.GetRequiredService<IEditorClock>().CurrentTime;
        _scene = editorContext.GetRequiredService<Scene>();
        _historyManager = editorContext.GetRequiredService<HistoryManager>();
        Extension = extension;
        TtsLoader.VoiceVoxLoader.Subscribe(l =>
        {
            if (l == null) return;
            l.InitializationTcs.Task.ContinueWith(_ => OnLoaded());
        });
    }

    public ToolTabExtension Extension { get; }

    public IReactiveProperty<bool> IsSelected { get; } = new ReactiveProperty<bool>();

    public string Header { get; } = "テキスト読み上げ";

    public ReactiveProperty<string> Text { get; } = new();

    public ReactiveProperty<VoiceMetadata[]> Voice { get; } = new([]);

    public ReactiveProperty<VoiceMetadata?> SelectedVoice { get; } = new();

    public ReactiveProperty<VoiceStyle?> SelectedStyle { get; } = new();

    public ReactiveProperty<bool> IsGenerating { get; } = new();

    public ReactiveProperty<bool> IsEnabled { get; } = new();

    public ReactiveProperty<bool> IsVoiceVoxInstalled { get; } = new(true);

    // AudioQuery関連プロパティ
    public ReactiveProperty<AudioQueryModel?> AudioQuery { get; } = new();

    public ReactiveProperty<bool> HasAudioQuery { get; } = new();

    // グローバルパラメータ
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
            if (loader == null)
            {
                return;
            }

            IsVoiceVoxInstalled.Value = loader.IsInstalled;
            if (!loader.IsLoaded)
            {
                return;
            }

            IsEnabled.Value = true;
            var a = loader.VoiceSets
                .SelectMany(x => x.Metadata.Select(y => new VoiceFlattenSet(x.Model, y)));

            var b = a.SelectMany(x => x.Metadata.Styles.Select(y => (x.Metadata, Style: y)));

            Voice.Value = b.GroupBy(x => x.Metadata.Name, x => x,
                    (x, y) =>
                    {
                        var items = y.ToArray();
                        var metadata = items[0].Metadata;
                        return new VoiceMetadata
                        {
                            Name = x,
                            Version = metadata.Version,
                            SpeakerUuid = metadata.SpeakerUuid,
                            Styles = items.Select(z => z.Style).ToArray()
                        };
                    })
                .ToArray();
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

    public Task CreateQuery()
    {
        return Task.Run(() =>
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;

            try
            {
                IsGenerating.Value = true;
                var query = CreateQueryCore();
                if (query == null) return;

                UpdateAudioQuery(query);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create AudioQuery");
                ShowError("AudioQueryの作成に失敗しました。", ex.Message);
            }
            finally
            {
                IsGenerating.Value = false;
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    private AudioQueryModel? CreateQueryCore()
    {
        try
        {
            var loader = TtsLoader.VoiceVoxLoader.Value;
            var synthesizer = loader?.Synthesizer;
            var voice = SelectedVoice.Value;
            var style = SelectedStyle.Value ?? voice?.Styles.FirstOrDefault();
            if (loader == null || synthesizer == null)
            {
                _logger.LogError("Synthesizer is not initialized");
                ShowWarning("VOICEVOXが初期化されていません。");
                return null;
            }

            if (style == null)
            {
                _logger.LogError("Style is not selected");
                ShowWarning("話者またはスタイルを選択してください。");
                return null;
            }

            if (string.IsNullOrWhiteSpace(Text.Value))
            {
                _logger.LogError("Text is empty");
                ShowWarning("テキストを入力してください。");
                return null;
            }

            if (!loader.EnsureVoiceModelLoaded(style.Id))
            {
                _logger.LogError("Failed to load voice model for style {StyleId}", style.Id);
                ShowError("音声モデルのロードに失敗しました。", $"Style ID: {style.Id}");
                return null;
            }

            var result = synthesizer.CreateAudioQuery(Text.Value, style.Id, out var audioQueryJson);
            if (result != ResultCode.RESULT_OK || audioQueryJson == null)
            {
                var message = result.ToMessage();
                _logger.LogError("Failed to create AudioQuery: {Result}", message);
                ShowError("AudioQueryの作成に失敗しました。", message);
                return null;
            }

            var query = JsonSerializer.Deserialize<AudioQueryModel>(audioQueryJson, s_jsonOptions);
            if (query == null)
            {
                _logger.LogError("Failed to deserialize AudioQuery");
                ShowError("AudioQueryの読み込みに失敗しました。", "VOICEVOXの応答を解析できませんでした。");
                return null;
            }

            _logger.LogInformation("AudioQuery created with {Count} accent phrases",
                query.AccentPhrases.Count);
            return query;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create AudioQuery");
            ShowError("AudioQueryの作成に失敗しました。", ex.Message);
            return null;
        }
    }

    private void UpdateAudioQuery(AudioQueryModel query)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AudioQuery.Value = query;
            HasAudioQuery.Value = true;
        });
    }

    private static void ShowWarning(string message)
    {
        Dispatcher.UIThread.Post(() => NotificationService.ShowWarning("警告", message));
    }

    private static void ShowError(string title, string message)
    {
        Dispatcher.UIThread.Post(() => NotificationService.ShowError(title, message));
    }

    private string BuildAudioQueryJson(AudioQueryModel query)
    {
        query.SpeedScale = SpeedScale.Value;
        query.PitchScale = PitchScale.Value;
        query.IntonationScale = IntonationScale.Value;
        query.VolumeScale = VolumeScale.Value;
        query.PrePhonemeLength = PrePhonemeLength.Value;
        query.PostPhonemeLength = PostPhonemeLength.Value;
        return JsonSerializer.Serialize(query, s_jsonOptions);
    }

    public Task Generate()
    {
        return Task.Run(async () =>
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;

            try
            {
                IsGenerating.Value = true;

                // AudioQueryがない場合は先に作成
                var query = AudioQuery.Value;
                if (query == null)
                {
                    query = CreateQueryCore();
                    if (query == null) return;
                    UpdateAudioQuery(query);
                }

                var outputWave = SynthesisFromQuery(query);
                if (outputWave == null)
                {
                    return;
                }

                _logger.LogInformation("Writing output wav file...");

                var projectDir = _scene.FindHierarchicalParent<Project>() is { Uri: { } projUri }
                    ? Path.GetDirectoryName(projUri.LocalPath)
                    : Path.GetDirectoryName(_scene.Uri!.LocalPath)!;
                var dir = Path.Combine(projectDir!, "resources", "tts");
                Directory.CreateDirectory(dir);
                var id = Guid.NewGuid().ToString();
                var path = Path.Combine(dir, $"{id}.wav");
                await using (var stream = File.OpenWrite(path))
                await using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(outputWave);
                }

                _logger.LogInformation("Output wav file saved");

                var source = SoundSource.Open(path);
                var element = new Element
                {
                    Start = _currentTime.Value,
                    ZIndex = 1,
                    Uri = RandomFileNameGenerator.GenerateUri(_scene.Uri!, "belm"),
                    Name = Text.Value.ReplaceLineEndings(" "),
                    AccentColor = ColorGenerator.GenerateColor(null, typeof(TtsController).FullName!)
                };
                var obj1 = new SourceSound();
                var obj2 = new TtsController();
                element.AddObject(obj1);
                element.AddObject(obj2);
                obj1.Source.CurrentValue = source;
                obj2.Text.CurrentValue = Text.Value;
                if (element.TryGetOriginalDuration(out var duration))
                {
                    element.Length = duration;
                }
                CoreSerializer.StoreToUri(element, element.Uri);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _scene.AddChild(element, ElementOverlapHandling.ZIndex);
                    _historyManager.Commit();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate TTS");
                ShowError("音声生成に失敗しました。", ex.Message);
            }
            finally
            {
                IsGenerating.Value = false;
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    public Task Play()
    {
        return Task.Run(async () =>
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;

            try
            {
                IsGenerating.Value = true;

                // AudioQueryがない場合は先に作成
                var query = AudioQuery.Value;
                if (query == null)
                {
                    query = CreateQueryCore();
                    if (query == null) return;
                    UpdateAudioQuery(query);
                }

                var outputWave = SynthesisFromQuery(query);
                if (outputWave == null)
                {
                    return;
                }

                _logger.LogInformation("Playing TTS...");

                using (var player = new SimpleWavePlayer(outputWave))
                {
                    await player.Play(default);
                }

                _logger.LogInformation("TTS played");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to play TTS");
                ShowError("音声再生に失敗しました。", ex.Message);
            }
            finally
            {
                IsGenerating.Value = false;
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    private byte[]? SynthesisFromQuery(AudioQueryModel query)
    {
        try
        {
            var loader = TtsLoader.VoiceVoxLoader.Value;
            var synthesizer = loader?.Synthesizer;
            var voice = SelectedVoice.Value;
            var style = SelectedStyle.Value ?? voice?.Styles.FirstOrDefault();
            if (loader == null || synthesizer == null || style == null)
            {
                _logger.LogError("Synthesizer or style is not initialized");
                ShowWarning("VOICEVOX、話者、またはスタイルが初期化されていません。");
                return null;
            }

            if (!loader.EnsureVoiceModelLoaded(style.Id))
            {
                _logger.LogError("Failed to load voice model for style {StyleId}", style.Id);
                ShowError("音声モデルのロードに失敗しました。", $"Style ID: {style.Id}");
                return null;
            }

            var audioQueryJson = BuildAudioQueryJson(query);

            var result = synthesizer.Synthesis(
                audioQueryJson, style.Id, SynthesisOptions.Default(),
                out var outputWavSize, out var outputWav);
            if (result != ResultCode.RESULT_OK)
            {
                var message = result.ToMessage();
                _logger.LogError("Failed to synthesize: {Result}", message);
                ShowError("音声合成に失敗しました。", message);
                return null;
            }

            return outputWav;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to synthesize from AudioQuery");
            ShowError("音声合成に失敗しました。", ex.Message);
            return null;
        }
    }

    public void OnAccentPhrasesUpdated()
    {
        // AccentPhrasePanelからの変更通知を受け取る
        // AudioQuery.Valueの中身は直接更新されているので、UIの更新のみ
        AudioQuery.ForceNotify();
    }

    public void Dispose()
    {
    }

    public void WriteToJson(JsonObject json)
    {
        json[nameof(Text)] = Text.Value;
        json[nameof(SelectedVoice)] = SelectedVoice.Value?.Name;
        json[nameof(SelectedStyle)] = SelectedStyle.Value?.Name;
    }

    public async void ReadFromJson(JsonObject json)
    {
        Text.Value = (string?)json[nameof(Text)] ?? "";
        await _initTcs.Task;
        var selectedVoice = (string?)json[nameof(SelectedVoice)] ?? "";
        var selectedStyle = (string?)json[nameof(SelectedStyle)] ?? "";
        Dispatcher.UIThread.Post(() =>
        {
            SelectedVoice.Value = Voice.Value.FirstOrDefault(x => x.Name == selectedVoice);
            SelectedStyle.Value = SelectedVoice.Value?.Styles.FirstOrDefault(x => x.Name == selectedStyle);
        }, DispatcherPriority.SystemIdle);
    }

    public object? GetService(Type serviceType)
    {
        return null;
    }
}
