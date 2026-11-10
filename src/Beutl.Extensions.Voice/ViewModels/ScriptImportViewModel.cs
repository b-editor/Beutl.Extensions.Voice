using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Beutl.Editor;
using Beutl.Editor.Services;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Beutl.Logging;
using Beutl.ProjectSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public sealed class ScriptImportViewModel : IDisposable
{
    private readonly Scene _scene;
    private readonly HistoryManager _history;
    private readonly IEditorClock _clock;
    private readonly Func<string, VoiceSettings, CancellationToken, Task<VoiceSynthesisResult>> _synthesize;
    private readonly ILogger _logger = Log.CreateLogger<ScriptImportViewModel>();
    private readonly IDisposable _loaderSubscription;
    private readonly IDisposable _connectionSubscription;
    private readonly CompositeDisposable _speakerSubscriptions = new();
    // Keep assignments when the source script is edited and imported again.
    private readonly Dictionary<string, ScriptSpeakerViewModel> _assignments = new(StringComparer.Ordinal);
    private VoicePreset[] _presets = [];
    private CancellationTokenSource? _cancellation;
    private bool _disposed;

    public ScriptImportViewModel(IEditorContext editor)
        : this(editor, (text, settings, token) => VoiceSynthesisService.SynthesizeAsync(text, settings, null, token)) { }

    internal ScriptImportViewModel(IEditorContext editor,
        Func<string, VoiceSettings, Task<VoiceSynthesisResult>> synthesize)
        : this(editor, (text, settings, _) => synthesize(text, settings)) { }

    private ScriptImportViewModel(IEditorContext editor,
        Func<string, VoiceSettings, CancellationToken, Task<VoiceSynthesisResult>> synthesize)
    {
        _scene = editor.GetRequiredService<Scene>();
        _history = editor.GetRequiredService<HistoryManager>();
        _clock = editor.GetRequiredService<IEditorClock>();
        _synthesize = synthesize;
        UseCurrentPosition();
        FirstLayer.Value = editor.GetService<IEditorSelection>()?.SelectedLayerNumber.Value ?? 1;
        RefreshPresets();
        _connectionSubscription = IsVoiceVoxInstalled.Subscribe(_ => UpdateInstallPrompt());
        VoiceEngineStore.Changed += OnConnectionsChanged;
        TtsLoader.Preferences.PromptChanged += OnConnectionsChanged;
        _loaderSubscription = TtsLoader.VoiceVoxLoader.Subscribe(async loader =>
        {
            if (loader == null) return;
            await loader.InitializationTcs.Task;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_disposed && ReferenceEquals(loader, TtsLoader.VoiceVoxLoader.Value))
                    IsVoiceVoxInstalled.Value = loader.IsInstalled;
            });
        });
    }

    public ReactivePropertySlim<string> RawText { get; } = new("");
    public ReactivePropertySlim<bool> HasSpeakerNames { get; } = new(true);
    public ObservableCollection<ScriptLine> Lines { get; } = [];
    public ObservableCollection<ScriptSpeakerViewModel> Speakers { get; } = [];
    public ReactivePropertySlim<double> StartSeconds { get; } = new();
    public ReactivePropertySlim<double> GapSeconds { get; } = new(0.2);
    public ReactivePropertySlim<int> FirstLayer { get; } = new(1);
    public ReactivePropertySlim<bool> SeparateLayers { get; } = new();
    public ReactivePropertySlim<bool> IsGenerating { get; } = new();
    public ReactivePropertySlim<bool> IsVoiceVoxInstalled { get; } = new(TtsLoader.VoiceVoxLoader.Value?.IsInstalled == true);
    public ReactivePropertySlim<bool> ShowCoreInstallPrompt { get; } = new();
    public ReactivePropertySlim<string> Message { get; } = new("");

    public void UseCurrentPosition() => StartSeconds.Value = _clock.CurrentTime.Value.ToSeconds();

    public void RefreshPresets()
    {
        try
        {
            _presets = VoicePresetStore.Load(VoicePresetStore.GlobalPath);
            foreach (var speaker in _assignments.Values) speaker.SetPresets(_presets);
        }
        catch (Exception ex) { Message.Value = $"プリセットを読み込めませんでした: {ex.Message}"; }
    }

    internal async Task ImportFile(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        string text = await reader.ReadToEndAsync();
        if (_disposed || IsGenerating.Value) return;
        RawText.Value = text;
        ImportText();
    }

    public void ImportText()
    {
        if (_disposed || IsGenerating.Value) return;
        SetLines(ScriptLine.Parse(RawText.Value, HasSpeakerNames.Value));
        Message.Value = Lines.Count == 0 ? "台本を入力してください。" : $"{Lines.Count} 件のセリフを読み込みました。";
    }

    private void SetLines(IEnumerable<ScriptLine> lines)
    {
        Lines.Clear();
        foreach (var line in lines) Lines.Add(line);
        string[] names = Lines.Select(x => x.Speaker).Distinct(StringComparer.Ordinal).ToArray();
        if (Speakers.Select(x => x.Name).SequenceEqual(names))
        {
            UpdateInstallPrompt();
            return;
        }
        _speakerSubscriptions.Clear();
        Speakers.Clear();
        foreach (string name in names)
        {
            if (!_assignments.TryGetValue(name, out var speaker))
            {
                speaker = new ScriptSpeakerViewModel(name, _presets);
                _assignments.Add(name, speaker);
            }
            Speakers.Add(speaker);
            _speakerSubscriptions.Add(speaker.SelectedPreset.Subscribe(_ => UpdateInstallPrompt()));
        }
        UpdateInstallPrompt();
    }

    public void Cancel() => _cancellation?.Cancel();

    public async Task Generate()
    {
        if (_disposed || IsGenerating.Value) return;
        string failureContext = "台本と配置設定を確認してください。";
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var requests = CaptureRequests();
            IsGenerating.Value = true;
            var elements = await VoiceScriptService.GenerateAsync(_scene, _history, requests,
                StartSeconds.Value, GapSeconds.Value, FirstLayer.Value, SeparateLayers.Value, _synthesize, token,
                (index, speaker) =>
                {
                    if (index == requests.Length)
                    {
                        failureContext = "配置先の重なりやレイヤーのロックを確認してください。";
                        return;
                    }
                    string displayName = _assignments[speaker].DisplayName;
                    Message.Value = $"音声を生成中… {index + 1} / {requests.Length}（{displayName}）";
                    failureContext = $"{index + 1} 件目（{displayName}）の音声生成を確認してください。";
                });
            Message.Value = $"{elements.Length} 件の音声を配置しました。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Message.Value = "生成を中止しました。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import speech script");
            Message.Value = $"音声を配置できませんでした: {ex.Message} {failureContext}";
        }
        finally
        {
            _cancellation = null;
            IsGenerating.Value = false;
        }
    }

    private VoiceScriptLine[] CaptureRequests()
    {
        // Generate the text currently shown in the input, including edits made after importing speakers.
        SetLines(ScriptLine.Parse(RawText.Value, HasSpeakerNames.Value));
        return Lines.Select(line =>
        {
            var speaker = _assignments[line.Speaker];
            var preset = speaker.SelectedPreset.Value
                ?? throw new InvalidOperationException($"「{speaker.DisplayName}」のプリセットを選択してください。");
            return new VoiceScriptLine(line.Text, speaker.Name, preset);
        }).ToArray();
    }

    public void WriteToJson(JsonObject json)
    {
        json["Script"] = JsonSerializer.SerializeToNode(new State
        {
            RawText = RawText.Value, HasSpeakerNames = HasSpeakerNames.Value,
            Lines = Lines.ToArray(),
            Speakers = _assignments.Values.Select(x => new SpeakerState(x.Name, x.AssignedPreset)).ToArray(),
            StartSeconds = StartSeconds.Value, GapSeconds = GapSeconds.Value,
            FirstLayer = FirstLayer.Value, SeparateLayers = SeparateLayers.Value
        });
    }

    public void ReadFromJson(JsonObject json)
    {
        if (_disposed || IsGenerating.Value || json["Script"]?.Deserialize<State>() is not { } state) return;
        RawText.Value = state.RawText;
        HasSpeakerNames.Value = state.HasSpeakerNames;
        StartSeconds.Value = state.StartSeconds;
        GapSeconds.Value = state.GapSeconds;
        FirstLayer.Value = state.FirstLayer;
        SeparateLayers.Value = state.SeparateLayers;
        _speakerSubscriptions.Clear();
        _assignments.Clear();
        Speakers.Clear();
        foreach (var saved in state.Speakers)
        {
            var speaker = new ScriptSpeakerViewModel(saved.Name, _presets, saved.Preset);
            _assignments.Add(saved.Name, speaker);
        }
        SetLines(state.Lines);
    }

    private void UpdateInstallPrompt()
    {
        bool canPrompt = TtsLoader.Preferences.ShouldPrompt && !IsVoiceVoxInstalled.Value;
        try
        {
            ShowCoreInstallPrompt.Value = canPrompt &&
                (Speakers.Any(x => x.SelectedPreset.Value?.Settings.EngineId == VoiceSettings.CoreEngineId)
                    || VoiceEngineStore.Load().Length == 0);
        }
        catch (Exception ex)
        {
            ShowCoreInstallPrompt.Value = canPrompt;
            Message.Value = $"接続先を読み込めませんでした: {ex.Message}";
        }
    }

    private void OnConnectionsChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) UpdateInstallPrompt();
        else Dispatcher.UIThread.Post(() => { if (!_disposed) UpdateInstallPrompt(); });
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
        _loaderSubscription.Dispose();
        _connectionSubscription.Dispose();
        _speakerSubscriptions.Dispose();
        VoiceEngineStore.Changed -= OnConnectionsChanged;
        TtsLoader.Preferences.PromptChanged -= OnConnectionsChanged;
    }

    private sealed record SpeakerState(string Name, VoicePreset? Preset);
    private sealed record State
    {
        public string RawText { get; init; } = "";
        public bool HasSpeakerNames { get; init; } = true;
        public ScriptLine[] Lines { get; init; } = [];
        public SpeakerState[] Speakers { get; init; } = [];
        public double StartSeconds { get; init; }
        public double GapSeconds { get; init; } = 0.2;
        public int FirstLayer { get; init; } = 1;
        public bool SeparateLayers { get; init; }
    }
}
