using System.Collections.ObjectModel;
using Beutl.Extensions.Voice.Models;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public class AccentPhraseViewModel : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    
    public AccentPhraseViewModel(AccentPhrase accentPhrase, int phraseIndex)
    {
        Model = accentPhrase;
        PhraseIndex = phraseIndex;
        Accent = new ReactiveProperty<int>(accentPhrase.Accent);
        IsInterrogative = new ReactiveProperty<bool>(accentPhrase.IsInterrogative);
        
        Moras = new ObservableCollection<MoraViewModel>(
            accentPhrase.Moras.Select((m, i) => new MoraViewModel(m, i)));

        // Update model when properties change
        _disposables.Add(Accent.Subscribe(value => Model.Accent = value));
        _disposables.Add(IsInterrogative.Subscribe(value => Model.IsInterrogative = value));
    }

    public AccentPhrase Model { get; }
    public int PhraseIndex { get; }
    public ReactiveProperty<int> Accent { get; }
    public ReactiveProperty<bool> IsInterrogative { get; }
    public ObservableCollection<MoraViewModel> Moras { get; }

    public string DisplayText => string.Join("", Moras.Select(m => m.Text.Value));
    
    public int MaxAccentPosition => Math.Max(1, Moras.Count);
    
    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
        _disposables.Clear();
        
        foreach (var mora in Moras)
        {
            mora.Dispose();
        }
    }
}

public class MoraViewModel : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    
    public MoraViewModel(Mora mora, int moraIndex)
    {
        Model = mora;
        MoraIndex = moraIndex;
        Text = new ReactiveProperty<string>(mora.Text);
        Pitch = new ReactiveProperty<float>(mora.Pitch);
        VowelLength = new ReactiveProperty<float>(mora.VowelLength);

        // Update model when properties change
        _disposables.Add(Pitch.Subscribe(value => Model.Pitch = value));
        _disposables.Add(VowelLength.Subscribe(value => Model.VowelLength = value));
    }

    public Mora Model { get; }
    public int MoraIndex { get; }
    public ReactiveProperty<string> Text { get; }
    public ReactiveProperty<float> Pitch { get; }
    public ReactiveProperty<float> VowelLength { get; }
    
    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
        _disposables.Clear();
    }
}
