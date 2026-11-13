using System.Reactive.Disposables;
using Avalonia.Threading;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

namespace Beutl.Extensions.Voice.ViewModels;

public partial class VoiceEditorViewModel
{
    private VoiceMetadata[] _coreVoices = [];
    private HashSet<string> _storedEngineIds = [];
    public ReactivePropertySlim<VoiceEngineConnection[]> Engines { get; } = new([VoiceEngineConnection.Core]);
    public ReactivePropertySlim<VoiceEngineConnection?> SelectedEngine { get; } = new(VoiceEngineConnection.Core);
    public ReactivePropertySlim<VoiceEngineCapabilities> EngineCapabilities { get; } = new(new());
    // Any engine other than the bundled Core: an HTTP engine or AquesTalkPlayer.
    public ReactivePropertySlim<bool> IsExternalEngine { get; } = new();
    public ReactivePropertySlim<bool> IsAquesTalkPlayer { get; } = new();
    // Only an HTTP engine has a URL to edit.
    public ReactivePropertySlim<bool> CanEditConnection { get; } = new();
    public ReactivePropertySlim<bool> IsConnecting { get; } = new();
    public ReactivePropertySlim<bool> ShowCoreInstallPrompt { get; } = new();
    public ReactivePropertySlim<string> ConnectionMessage { get; } = new("");

    internal bool IsMissingConnection(string id) => !_storedEngineIds.Contains(id);

    private void InitializeEngines()
    {
        RefreshEngines();
        SelectedEngine.Subscribe(engine =>
        {
            if (_updating || engine == null) return;
            UpdateSilently(() =>
            {
                _unavailableSettings = null;
                SelectedVoice.Value = null;
                SelectedStyle.Value = null;
                ShowAquesTalkPlayerVoice(null);
                UseEngine(engine);
            });
            InvalidateQuery();
            Edited?.Invoke();
            ConfirmEdit();
        }).AddTo(_subscriptions);
        IsVoiceVoxInstalled.Subscribe(_ => UpdateInstallPrompt()).AddTo(_subscriptions);
        TtsLoader.Preferences.PromptChanged += OnInstallPromptChanged;
        Disposable.Create(() => TtsLoader.Preferences.PromptChanged -= OnInstallPromptChanged).AddTo(_subscriptions);
        VoiceEngineStore.Changed += OnConnectionsChanged;
        Disposable.Create(() => VoiceEngineStore.Changed -= OnConnectionsChanged).AddTo(_subscriptions);
    }

    private void OnConnectionsChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshEngines();
        else Dispatcher.UIThread.Post(() => { if (!_disposed) RefreshEngines(); });
    }

    private void UpdateInstallPrompt() => ShowCoreInstallPrompt.Value =
        TtsLoader.Preferences.ShouldPrompt && !IsVoiceVoxInstalled.Value && !IsExternalEngine.Value;

    private void OnInstallPromptChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) UpdateInstallPrompt();
        else Dispatcher.UIThread.Post(() => { if (!_disposed) UpdateInstallPrompt(); });
    }

    private VoiceSettings SelectedEngineSettings()
    {
        var engine = SelectedEngine.Value ?? VoiceEngineConnection.Core;
        return new() { EngineId = engine.Id, EngineName = engine.Name, EngineUuid = engine.EngineUuid, EngineVersion = engine.Version };
    }

    private VoiceEngineConnection FindEngine(VoiceSettings settings)
    {
        var engine = Engines.Value.FirstOrDefault(x => x.Id == settings.EngineId);
        if (engine != null) return engine;
        engine = new()
        {
            Id = settings.EngineId, Name = settings.EngineName, EngineUuid = settings.EngineUuid,
            Capabilities = settings.IsAquesTalkPlayer ? AquesTalkPlayerEngine.Capabilities
                : new() { TempoDynamics = settings.TempoDynamicsScale.HasValue, EditAccent = !settings.TempoDynamicsScale.HasValue }
        };
        Engines.Value = [.. Engines.Value, engine];
        return engine;
    }

    private void UseEngine(VoiceEngineConnection engine)
    {
        if (!IsExternalEngine.Value && !engine.IsCore) _coreVoices = Voice.Value;
        SelectedEngine.Value = engine;
        // What AquesTalkPlayer can do is up to this extension rather than the version that registered it.
        EngineCapabilities.Value = engine.IsAquesTalkPlayer ? AquesTalkPlayerEngine.Capabilities : engine.Capabilities;
        IsExternalEngine.Value = !engine.IsCore;
        IsAquesTalkPlayer.Value = engine.IsAquesTalkPlayer;
        CanEditConnection.Value = !engine.IsCore && !engine.IsAquesTalkPlayer;
        UpdateInstallPrompt();
        if (engine.IsCore)
        {
            Voice.Value = _coreVoices;
            LoadCoreMetadata(restoreSettings: false);
        }
        else
        {
            Voice.Value = engine.Voices;
            IsEnabled.Value = true;
            if (engine.IsAquesTalkPlayer && engine.Voices.Any(x => x.AquesTalkPlayer == null))
                _ = ReadAquesTalkPlayerValues(engine);
        }
    }

    private bool _readingAquesTalkPlayerValues;

    // Presets registered by a version that did not keep their values cannot be tuned, so they are read again once.
    private async Task ReadAquesTalkPlayerValues(VoiceEngineConnection engine)
    {
        if (_readingAquesTalkPlayerValues || string.IsNullOrEmpty(engine.ApplicationPath)) return;
        _readingAquesTalkPlayerValues = true;
        try
        {
            var updated = await Task.Run(() => AquesTalkPlayerEngine.Discover(engine.ApplicationPath, engine));
            if (_disposed) return;
            VoiceEngineStore.UpdateMetadata(updated);
        }
        catch (Exception ex)
        {
            if (!_disposed) Message.Value = $"プリセットを読み込めませんでした: {ex.Message}";
        }
    }

    public void RefreshEngines()
    {
        if (_disposed) return;
        try
        {
            var settings = CaptureStoredSettings();
            var query = AudioQuery.Value;
            var selected = SelectedEngine.Value ?? VoiceEngineConnection.Core;
            var connections = VoiceEngineStore.Load();
            _storedEngineIds = connections.Select(x => x.Id).ToHashSet();
            var engines = new[] { VoiceEngineConnection.Core }.Concat(connections).ToArray();
            if (engines.All(x => x.Id != selected.Id))
                engines = [.. engines, selected with { Url = "", Voices = [] }];
            UpdateSilently(() =>
            {
                Engines.Value = engines;
                var next = engines.First(x => x.Id == selected.Id);
                if (next.IsCore) SelectedEngine.Value = next;
                else UseEngine(next);
                if (settings != null) ApplySettings(settings, report: false);
                AudioQuery.Value = query;
            });
        }
        catch (Exception ex) { Message.Value = $"接続先を読み込めませんでした: {ex.Message}"; }
    }

    public async Task<bool> SaveConnection(string name, string url, string? id = null, bool restoreMissing = false)
    {
        if (_disposed || IsConnecting.Value) return false;
        IsConnecting.Value = true;
        ConnectionMessage.Value = "接続しています…";
        try
        {
            var existing = Engines.Value.FirstOrDefault(x => x.Id == id);
            string? expectedUrl = existing?.Url;
            string normalizedUrl = HttpVoiceEngine.NormalizeUrl(url);
            var seed = existing?.Url == normalizedUrl ? existing : null;
            var connection = (seed ?? new VoiceEngineConnection()) with
            {
                Id = id ?? Guid.NewGuid().ToString("N"), Name = name, Url = normalizedUrl
            };
            connection = await HttpVoiceEngine.DiscoverAsync(connection);
            if (_disposed) return false;
            VoiceEngineStore.Save(connection, allowCreate: id == null || restoreMissing, expectedUrl: expectedUrl);
            SelectedEngine.Value = Engines.Value.First(x => x.Id == connection.Id);
            ConnectionMessage.Value = $"{connection.Name} {connection.Version} に接続しました。";
            Message.Value = ConnectionMessage.Value;
            return true;
        }
        catch (Exception ex) { ConnectionMessage.Value = $"接続できませんでした: {ex.Message}"; return false; }
        finally { IsConnecting.Value = false; }
    }

    public async Task ReloadEngine()
    {
        RefreshEngines();
        var engine = SelectedEngine.Value;
        if (engine == null || IsConnecting.Value || _disposed) return;
        if (engine.IsCore) { OnLoaded(); return; }
        IsConnecting.Value = true;
        try
        {
            // AquesTalkPlayer's voices are its presets, read again for presets added or renamed since.
            var updated = engine.IsAquesTalkPlayer
                ? await Task.Run(() => AquesTalkPlayerEngine.Discover(engine.ApplicationPath, engine))
                : await HttpVoiceEngine.DiscoverAsync(engine);
            if (_disposed) return;
            // Refreshing metadata keeps the selection and manual query edits in every open editor.
            VoiceEngineStore.UpdateMetadata(updated);
            if (SelectedEngine.Value?.Id == engine.Id)
                Message.Value = engine.IsAquesTalkPlayer
                    ? $"{SelectedEngine.Value.Name} {updated.Version} のプリセットを読み込みました（{updated.Voices.Length} 件）。"
                    : $"{SelectedEngine.Value.Name} {updated.Version} に接続しました。";
        }
        catch (Exception ex)
        {
            Message.Value = engine.IsAquesTalkPlayer ? $"プリセットを読み込めませんでした: {ex.Message}" : $"接続できませんでした: {ex.Message}";
        }
        finally { IsConnecting.Value = false; }
    }

    public void DeleteConnection()
    {
        if (SelectedEngine.Value is not { IsCore: false } engine || IsConnecting.Value) return;
        try
        {
            VoiceEngineStore.Delete(engine.Id);
            Message.Value = "接続先を削除しました。既存の音声とプリセットは保持されます。";
        }
        catch (Exception ex) { Message.Value = $"接続先を削除できませんでした: {ex.Message}"; }
    }
}
