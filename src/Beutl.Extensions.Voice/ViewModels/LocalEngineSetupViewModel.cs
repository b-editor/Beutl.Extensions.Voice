using System.Net.Http;
using System.Text.Json;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public sealed class LocalEngineCandidateViewModel(LocalVoiceEngine engine)
{
    internal const string UseText = "読み上げで使う";
    internal const string StartText = "起動して使う";

    public LocalVoiceEngine Engine { get; } = engine;
    public ReactivePropertySlim<string?> ApplicationPath { get; } = new();
    public ReactivePropertySlim<string> Status { get; } = new("確認前");
    // Using an engine registers it, or brings its registration up to date, and chooses it in the speech tab; an engine
    // that is not running is started first.
    public ReactivePropertySlim<string> ActionText { get; } = new(UseText);
    public ReactivePropertySlim<bool> CanUse { get; } = new();
    public ReactivePropertySlim<bool> IsRegistered { get; } = new();
    internal bool IsUserSelected { get; set; }
    internal VoiceEngineConnection? Connection { get; set; }
}

public sealed class LocalEngineSetupViewModel : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<LocalVoiceEngine, string?> _findApplication;
    private readonly Action<string> _startApplication;
    private bool _disposed;

    public LocalEngineSetupViewModel() : this(LocalVoiceEngines.Available) { }

    internal LocalEngineSetupViewModel(IEnumerable<LocalVoiceEngine> engines,
        Func<LocalVoiceEngine, string?>? findApplication = null, Action<string>? startApplication = null)
    {
        Engines = engines.Select(x => new LocalEngineCandidateViewModel(x)).ToArray();
        _findApplication = findApplication ?? LocalVoiceEngines.FindApplication;
        _startApplication = startApplication ?? LocalVoiceEngines.StartApplication;
    }

    public LocalEngineCandidateViewModel[] Engines { get; }
    public ReactivePropertySlim<bool> IsBusy { get; } = new();
    public ReactivePropertySlim<string> Message { get; } = new("");
    public VoiceEngineConnection? SelectedConnection { get; private set; }

    public async Task Refresh()
    {
        if (_disposed || IsBusy.Value) return;
        IsBusy.Value = true;
        Message.Value = "";
        var cancellationToken = _lifetime.Token;
        try
        {
            var aquesTalkPlayer = AquesTalkPlayerEngine.Registered();
            await Task.WhenAll(Engines.Select(async entry =>
            {
                entry.Status.Value = "確認中…";
                // A registered AquesTalkPlayer stays where the user put it, even when another copy is found.
                string? path = entry.IsUserSelected ? entry.ApplicationPath.Value
                    : entry.Engine.IsAquesTalkPlayer && aquesTalkPlayer?.ApplicationPath is { } stored
                        && (File.Exists(stored) || Directory.Exists(stored)) ? stored
                    : await Task.Run(() => _findApplication(entry.Engine), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                entry.ApplicationPath.Value = path;
                if (entry.Engine.IsAquesTalkPlayer)
                {
                    entry.IsRegistered.Value = aquesTalkPlayer != null;
                    await CheckAquesTalkPlayer(entry, aquesTalkPlayer, cancellationToken);
                    return;
                }
                var registered = FindStoredConnection(entry);
                entry.IsRegistered.Value = registered != null;
                await Check(entry, registered ?? new VoiceEngineConnection { Url = entry.Engine.Url }, cancellationToken);
            }));
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { if (!_disposed) Message.Value = $"確認できませんでした: {ex.Message}"; }
        finally { IsBusy.Value = false; }
    }

    private static VoiceEngineConnection? FindStoredConnection(LocalEngineCandidateViewModel entry) =>
        VoiceEngineStore.FindByUrl(VoiceEngineStore.Load(), entry.Engine.Url);

    // AquesTalkPlayer runs only to speak, so finding it and reading its presets is all there is to check.
    private static async Task CheckAquesTalkPlayer(LocalEngineCandidateViewModel entry, VoiceEngineConnection? stored,
        CancellationToken cancellationToken)
    {
        entry.ActionText.Value = LocalEngineCandidateViewModel.UseText;
        if (entry.ApplicationPath.Value is not { } path)
        {
            entry.Status.Value = "アプリが見つかりません。場所を指定してください。";
            entry.CanUse.Value = false;
            return;
        }
        try
        {
            var connection = await Task.Run(() => AquesTalkPlayerEngine.Discover(path, stored), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            entry.Status.Value = $"使用できます（{Version(connection)}プリセット {connection.Voices.Length} 件）";
            entry.CanUse.Value = true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            entry.Status.Value = ex.Message;
            entry.CanUse.Value = false;
        }
    }

    private async Task<bool> Check(LocalEngineCandidateViewModel entry, VoiceEngineConnection seed,
        CancellationToken cancellationToken)
    {
        entry.Connection = null;
        try
        {
            var connection = await HttpVoiceEngine.DiscoverAsync(seed, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            entry.Connection = connection;
            entry.Status.Value = connection.Version.Length > 0 ? $"起動しています（バージョン {connection.Version}）" : "起動しています。";
            entry.ActionText.Value = LocalEngineCandidateViewModel.UseText;
            entry.CanUse.Value = true;
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            entry.Status.Value = entry.ApplicationPath.Value != null
                ? "起動していません。" : "起動していません。アプリの場所を指定するか、起動してから再検索してください。";
            entry.ActionText.Value = LocalEngineCandidateViewModel.StartText;
            entry.CanUse.Value = entry.ApplicationPath.Value != null;
            return false;
        }
    }

    // The card already names the engine.
    private static string Version(VoiceEngineConnection connection) =>
        connection.Version.Length > 0 ? $"バージョン {connection.Version}・" : "";

    public void SelectApplication(LocalEngineCandidateViewModel entry, string path)
    {
        if (_disposed || IsBusy.Value) return;
        entry.ApplicationPath.Value = path;
        entry.IsUserSelected = true;
        if (entry.Engine.IsAquesTalkPlayer)
        {
            // The presets are read when it is used.
            entry.Status.Value = "アプリを指定しました。";
            entry.CanUse.Value = true;
        }
        else if (entry.Connection == null)
        {
            entry.Status.Value = "アプリを指定しました。";
            entry.ActionText.Value = LocalEngineCandidateViewModel.StartText;
            entry.CanUse.Value = true;
        }
    }

    public async Task<bool> Use(LocalEngineCandidateViewModel entry)
    {
        if (_disposed || IsBusy.Value) return false;
        IsBusy.Value = true;
        Message.Value = "";
        try
        {
            if (entry.Engine.IsAquesTalkPlayer) return await UseAquesTalkPlayer(entry);
            // Keep the original identity and creation permission throughout startup polling.
            var stored = FindStoredConnection(entry);
            var seed = stored ?? new VoiceEngineConnection { Url = entry.Engine.Url };
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            waiting.CancelAfter(TimeSpan.FromSeconds(60));
            // Recheck before launch: another application may have started the same engine since the search.
            if (!await Check(entry, seed, waiting.Token))
            {
                if (entry.ApplicationPath.Value is not { } path)
                    throw new InvalidOperationException("アプリを起動してから再検索してください。");
                _startApplication(path);
                while (true)
                {
                    entry.Status.Value = "エンジンの起動を待っています…";
                    await Task.Delay(1000, waiting.Token);
                    if (await Check(entry, seed, waiting.Token)) break;
                }
            }
            waiting.Token.ThrowIfCancellationRequested();
            SelectedConnection = VoiceEngineStore.RegisterDiscovered(entry.Connection!, allowCreate: stored == null);
            entry.IsRegistered.Value = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) Message.Value = entry.Status.Value = "アプリの初期設定と起動状態を確認して、再検索してください。";
        }
        catch (Exception ex) { if (!_disposed) Message.Value = entry.Status.Value = $"接続できませんでした: {ex.Message}"; }
        finally { IsBusy.Value = false; }
        return false;
    }

    private async Task<bool> UseAquesTalkPlayer(LocalEngineCandidateViewModel entry)
    {
        try
        {
            string path = entry.ApplicationPath.Value
                ?? throw new InvalidOperationException("AquesTalkPlayerの場所を「アプリの場所を指定…」で指定してください。");
            // Read the presets again: they may have changed in the app since the search. The fixed ID replaces an
            // earlier registration, so there is one AquesTalkPlayer.
            var connection = await Task.Run(() => AquesTalkPlayerEngine.Discover(path, AquesTalkPlayerEngine.Registered()), _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            VoiceEngineStore.Save(connection);
            SelectedConnection = connection;
            entry.IsRegistered.Value = true;
            return true;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { if (!_disposed) Message.Value = entry.Status.Value = $"追加できませんでした: {ex.Message}"; }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
