using System.Text.Json.Nodes;
using Avalonia.Threading;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Helpers;
using Beutl.Logging;
using Beutl.Media.Source;
using Beutl.Operators.Source;
using Beutl.ProjectSystem;
using Beutl.Utilities;
using Beutl.ViewModels;
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
    private CommandRecorder _commandRecorder;
    private Task _initTask;

    public TtsTabViewModel(TtsTabExtension extension, IEditorContext editorContext)
    {
        _editorContext = editorContext;
        _currentTime = ((EditViewModel)editorContext).CurrentTime;
        _scene = editorContext.GetRequiredService<Scene>();
        _commandRecorder = editorContext.GetRequiredService<CommandRecorder>();
        Extension = extension;
        _initTask = TtsLoader.VoiceVoxLoader.InitializationTcs.Task.ContinueWith(_ =>
        {
            OnLoaded();
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
    
    public ReactiveProperty<bool> IsVoiceVoxInstalled { get; } = new();

    public void OnLoaded()
    {
        IsVoiceVoxInstalled.Value = TtsLoader.VoiceVoxLoader.IsInstalled;
        if (TtsLoader.VoiceVoxLoader.IsLoaded)
        {
            IsEnabled.Value = true;
            var a = TtsLoader.VoiceVoxLoader.VoiceSets
                .SelectMany(x => x.Metadata.Select(y => new VoiceFlattenSet(x.Model, y)));

            var b = a.SelectMany(x => x.Metadata.Styles.Select(y => (x.Metadata, Style: y)));

            Voice.Value = b.GroupBy(x => x.Metadata.Name, x => x.Style,
                    (x, y) => new VoiceMetadata { Name = x, Styles = y.ToArray() })
                .ToArray();   
        }
    }

    public Task Generate()
    {
        return Task.Run(async () =>
        {
            try
            {
                IsGenerating.Value = true;
                var outputWave = await Tts();
                if (outputWave == null)
                {
                    return;
                }

                _logger.LogInformation("Writing output wav file...");

                var dir = Path.Combine(Path.GetDirectoryName(_scene.FileName)!, ".beutl", "tts");
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
                    Length = source.Duration,
                    ZIndex = 1,
                    FileName = RandomFileNameGenerator.Generate(Path.GetDirectoryName(_scene.FileName)!, "belm"),
                    Name = Text.Value.ReplaceLineEndings().Replace("\n", " "),
                    AccentColor = ColorGenerator.GenerateColor(typeof(TtsController).FullName!)
                };
                var op = new SourceSoundOperator();
                element.Operation.AddChild(op).Do();
                op.Value.Source = source;
                element.Save(element.FileName);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    _scene.AddChild(element, ElementOverlapHandling.ZIndex).DoAndRecord(_commandRecorder));
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
                var outputWave = await Tts();
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

    private Task<byte[]?> Tts()
    {
        return Task.Run(() =>
        {
            try
            {
                var synthesizer = TtsLoader.VoiceVoxLoader.Synthesizer;
                var voice = SelectedVoice.Value;
                var style = SelectedStyle.Value ?? voice?.Styles.FirstOrDefault();
                if (synthesizer == null)
                {
                    _logger.LogError("Synthesizer is not initialized");
                    return null;
                }

                if (style == null)
                {
                    _logger.LogError("Style is not selected");
                    return null;
                }

                var result = synthesizer.Tts(
                    Text.Value, style.Id, TtsOptions.Default(),
                    out var outputWavSize, out var outputWav);
                if (result != ResultCode.RESULT_OK)
                {
                    _logger.LogError("Failed to generate TTS: {Result}", result.ToMessage());
                    return null;
                }

                return outputWav;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate TTS");
                return null;
            }
        });
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
        await _initTask;
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