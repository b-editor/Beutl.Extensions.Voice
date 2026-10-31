using System.Net.Http;
using System.Text.Json;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public sealed class LocalEngineCandidateViewModel(LocalVoiceEngine engine)
{
    public LocalVoiceEngine Engine { get; } = engine;
    public ReactivePropertySlim<string?> ApplicationPath { get; } = new();
    public ReactivePropertySlim<string> Status { get; } = new("確認前");
    public ReactivePropertySlim<string> ActionText { get; } = new("接続して追加");
    public ReactivePropertySlim<bool> CanUse { get; } = new();
    internal bool IsUserSelected { get; set; }
    internal VoiceEngineConnection? Connection { get; set; }
}

public sealed class LocalEngineSetupViewModel : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<LocalVoiceEngine, string?> _findApplication;
    private readonly Action<string> _startApplication;
    private bool _disposed;

    public LocalEngineSetupViewModel() : this(LocalVoiceEngines.Known) { }

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
            await Task.WhenAll(Engines.Select(async entry =>
            {
                entry.Status.Value = "確認中…";
                string? path = entry.IsUserSelected ? entry.ApplicationPath.Value
                    : await Task.Run(() => _findApplication(entry.Engine), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                entry.ApplicationPath.Value = path;
                var seed = FindStoredConnection(entry) ?? new VoiceEngineConnection { Url = entry.Engine.Url };
                await Check(entry, seed, cancellationToken);
            }));
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { if (!_disposed) Message.Value = $"確認できませんでした: {ex.Message}"; }
        finally { IsBusy.Value = false; }
    }

    private static VoiceEngineConnection? FindStoredConnection(LocalEngineCandidateViewModel entry) =>
        VoiceEngineStore.FindByUrl(VoiceEngineStore.Load(), entry.Engine.Url);

    private async Task<bool> Check(LocalEngineCandidateViewModel entry, VoiceEngineConnection seed,
        CancellationToken cancellationToken, bool quickCheck = true)
    {
        entry.Connection = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (quickCheck) timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var connection = await HttpVoiceEngine.DiscoverAsync(seed, timeout.Token);
            cancellationToken.ThrowIfCancellationRequested();
            entry.Connection = connection;
            entry.Status.Value = $"接続可能: {connection.Name} {connection.Version}".TrimEnd();
            entry.ActionText.Value = "接続して追加";
            entry.CanUse.Value = true;
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            entry.Status.Value = entry.ApplicationPath.Value != null ? "アプリを検出しました。" : "アプリを起動するか、選択してください。";
            entry.ActionText.Value = "起動して追加";
            entry.CanUse.Value = entry.ApplicationPath.Value != null;
            return false;
        }
    }

    public void SelectApplication(LocalEngineCandidateViewModel entry, string path)
    {
        if (_disposed || IsBusy.Value) return;
        entry.ApplicationPath.Value = path;
        entry.IsUserSelected = true;
        if (entry.Connection == null)
        {
            entry.Status.Value = "アプリを選択しました。";
            entry.ActionText.Value = "起動して追加";
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
            // Keep the original identity and creation permission throughout startup polling.
            var stored = FindStoredConnection(entry);
            var seed = stored ?? new VoiceEngineConnection { Url = entry.Engine.Url };
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            waiting.CancelAfter(TimeSpan.FromSeconds(60));
            // Recheck before launch: another application may have started the same engine since the search.
            // Allow the normal discovery timeout for slow engines before launching another process.
            if (!await Check(entry, seed, waiting.Token, quickCheck: false))
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
