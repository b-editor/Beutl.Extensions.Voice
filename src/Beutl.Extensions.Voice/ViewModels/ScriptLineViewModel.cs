using Beutl.Extensions.Voice.Models;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public sealed class ScriptLineViewModel : IDisposable
{
    private readonly IDisposable _speakerSubscription;
    private readonly Action<ScriptLineViewModel> _remove;

    public ScriptLineViewModel(ScriptLine line, Action changed, Action<ScriptLineViewModel> remove)
    {
        Speaker = new(line.Speaker);
        Text = new(line.Text);
        _remove = remove;
        _speakerSubscription = Speaker.Subscribe(_ => changed());
    }

    public ReactivePropertySlim<int> Number { get; } = new();
    public ReactivePropertySlim<string> Speaker { get; }
    public ReactivePropertySlim<string> Text { get; }
    public ScriptLine Capture() => new(Speaker.Value.Trim(), Text.Value);
    public void Remove() => _remove(this);
    public void Dispose() => _speakerSubscription.Dispose();
}
