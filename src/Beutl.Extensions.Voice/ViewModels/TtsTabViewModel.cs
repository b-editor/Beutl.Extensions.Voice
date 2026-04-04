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
using Beutl.ViewModels;
using Beutl.Editor;
using Beutl.Extensions.Voice.Internals;
using Beutl.Serialization;
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

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public TtsTabViewModel(TtsTabExtension extension, IEditorContext editorContext)
    {
        _editorContext = editorContext;
        _currentTime = ((EditViewModel)editorContext).CurrentTime;
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

    public IReactiveProperty<ToolTabExtension.TabPlacement> Placement { get; } =
        new ReactiveProperty<ToolTabExtension.TabPlacement>(ToolTabExtension.TabPlacement.RightLowerTop);

    public IReactiveProperty<ToolTabExtension.TabDisplayMode> DisplayMode { get; }
        = new ReactiveProperty<ToolTabExtension.TabDisplayMode>(ToolTabExtension.TabDisplayMode.Docked);

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
        var loader = TtsLoader.VoiceVoxLoader.Value;
        if (loader == null) return;

        IsVoiceVoxInstalled.Value = loader.IsInstalled;
        if (!loader.IsLoaded) return;

        IsEnabled.Value = true;
        var a = loader.VoiceSets
            .SelectMany(x => x.Metadata.Select(y => new VoiceFlattenSet(x.Model, y)));

        var b = a.SelectMany(x => x.Metadata.Styles.Select(y => (x.Metadata, Style: y)));

        Voice.Value = b.GroupBy(x => x.Metadata.Name, x => x.Style,
                (x, y) => new VoiceMetadata { Name = x, Styles = y.ToArray() })
            .ToArray();
        _initTcs.SetResult();
    }

    public Task CreateQuery()
    {
        return Task.Run(() =>
        {
            try
            {
                IsGenerating.Value = true;
                var loader = TtsLoader.VoiceVoxLoader.Value;
                var synthesizer = loader?.Synthesizer;
                var voice = SelectedVoice.Value;
                var style = SelectedStyle.Value ?? voice?.Styles.FirstOrDefault();
                if (loader == null || synthesizer == null || style == null || string.IsNullOrWhiteSpace(Text.Value))
                {
                    _logger.LogError("Synthesizer/style/text is not ready");
                    return;
                }

                if (!loader.EnsureVoiceModelLoaded(style.Id))
                {
                    _logger.LogError("Failed to load voice model for style {StyleId}", style.Id);
                    return;
                }

                var result = synthesizer.CreateAudioQuery(Text.Value, style.Id, out var audioQueryJson);
                if (result != ResultCode.RESULT_OK || audioQueryJson == null)
                {
                    _logger.LogError("Failed to create AudioQuery: {Result}", result.ToMessage());
                    return;
                }

                var query = JsonSerializer.Deserialize<AudioQueryModel>(audioQueryJson, s_jsonOptions);
                if (query == null)
                {
                    _logger.LogError("Failed to deserialize AudioQuery");
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    AudioQuery.Value = query;
                    HasAudioQuery.Value = true;
                });

                _logger.LogInformation("AudioQuery created with {Count} accent phrases",
                    query.AccentPhrases.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create AudioQuery");
            }
            finally
            {
                IsGenerating.Value = false;
            }
        });
    }

    private string BuildAudioQueryJson()
    {
        var query = AudioQuery.Value!;
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
            try
            {
                IsGenerating.Value = true;

                // AudioQueryがない場合は先に作成
                if (AudioQuery.Value == null)
                {
                    await CreateQuery();
                    if (AudioQuery.Value == null) return;
                }

                var outputWave = SynthesisFromQuery();
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
                    Name = Text.Value.ReplaceLineEndings().Replace("\n", " "),
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
            }
            finally
            {
                IsGenerating.Value = false;
            }
        });
    }

    public Task Play()
    {
        return Task.Run(async () =>
        {
            try
            {
                IsGenerating.Value = true;

                // AudioQueryがない場合は先に作成
                if (AudioQuery.Value == null)
                {
                    await CreateQuery();
                    if (AudioQuery.Value == null) return;
                }

                var outputWave = SynthesisFromQuery();
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
            }
            finally
            {
                IsGenerating.Value = false;
            }
        });
    }

    private byte[]? SynthesisFromQuery()
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
                return null;
            }

            if (!loader.EnsureVoiceModelLoaded(style.Id))
            {
                _logger.LogError("Failed to load voice model for style {StyleId}", style.Id);
                return null;
            }

            var audioQueryJson = BuildAudioQueryJson();

            var result = synthesizer.Synthesis(
                audioQueryJson, style.Id, SynthesisOptions.Default(),
                out var outputWavSize, out var outputWav);
            if (result != ResultCode.RESULT_OK)
            {
                _logger.LogError("Failed to synthesize: {Result}", result.ToMessage());
                return null;
            }

            return outputWav;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to synthesize from AudioQuery");
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