using Beutl.Extensions.Voice.Services;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public class VoiceVoxInstallDialogViewModel
{
    private readonly VoiceVoxInstaller _installer = new();
    private CancellationTokenSource? _cts;

    public ReactiveProperty<bool> IsInstalling { get; } = new(false);

    public ReactiveProperty<double> Progress => _installer.Progress;

    public ReactiveProperty<double> ProgressMax => _installer.ProgressMax;

    public ReactiveProperty<bool> IsIndeterminate => _installer.IsIndeterminate;

    public ReactiveProperty<string> Status => _installer.Status;

    public ReactiveProperty<string> Error => _installer.Error;

    public ReactiveProperty<bool> IsCompleted => _installer.IsCompleted;

    public async Task Install()
    {
        _cts = new CancellationTokenSource();
        IsInstalling.Value = true;
        try
        {
            await _installer.Install(_cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsInstalling.Value = false;
            _cts = null;
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }
}