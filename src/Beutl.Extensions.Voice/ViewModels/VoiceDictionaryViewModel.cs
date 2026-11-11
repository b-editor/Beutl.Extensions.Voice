using System.Collections.ObjectModel;
using System.Net;
using System.Reactive.Disposables;
using System.Text.Json;
using Avalonia.Threading;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

namespace Beutl.Extensions.Voice.ViewModels;

internal sealed class DictionaryPhraseViewModel : IDisposable
{
    private readonly CompositeDisposable _subscriptions = new();
    public ReactivePropertySlim<string> Surface { get; }
    public ReactivePropertySlim<string> Pronunciation { get; }
    public ReactivePropertySlim<int> Accent { get; }
    public ReactivePropertySlim<int> MoraCount { get; } = new();
    public ReactivePropertySlim<List<AccentPhraseModel>> AccentPhrases { get; } = new([]);
    public VoiceDictionaryPhrase Capture() => new(Surface.Value, Pronunciation.Value, Accent.Value);

    public DictionaryPhraseViewModel(VoiceDictionaryPhrase phrase, Action changed)
    {
        Surface = new(phrase.Surface);
        Pronunciation = new(phrase.Pronunciation);
        Accent = new(phrase.AccentType);
        Surface.Subscribe(_ => changed()).AddTo(_subscriptions);
        Pronunciation.Subscribe(_ =>
        {
            UpdateAccentPhrases();
            changed();
        }).AddTo(_subscriptions);
        Accent.Subscribe(_ =>
        {
            UpdateAccentPhrases();
            changed();
        }).AddTo(_subscriptions);
    }

    private void UpdateAccentPhrases()
    {
        var moras = DictionaryKana.Moras(Pronunciation.Value);
        MoraCount.Value = moras.Length;
        AccentPhrases.Value = moras.Length == 0 ? [] :
            [new() { Accent = Accent.Value, Moras = moras.Select(text => new MoraModel { Text = text }).ToList() }];
    }

    public void Dispose() => _subscriptions.Dispose();
}

internal sealed class DictionaryImportItem(DictionaryImportEntry entry)
{
    public DictionaryImportEntry Entry { get; } = entry;
    public bool IsCompatible => Entry.Error == null;
    public ReactivePropertySlim<bool> Selected { get; } = new(entry.Error == null);
    public string Name => Entry.Name;
    public string Status => Entry.Error ?? (Entry.Duplicate ? "登録済み" : "");
}

internal sealed class VoiceDictionaryViewModel : IDisposable
{
    private readonly CompositeDisposable _subscriptions = new();
    private VoiceDictionaryWord[] _words = [];
    private CancellationTokenSource? _request;
    private int _requestId;
    private bool _disposed;
    private bool _updating;
    private bool _editorOpen;
    private string _baseline = "";
    private Func<Task>? _pendingChange;

    public ReactivePropertySlim<VoiceEngineConnection[]> Engines { get; } = new([]);
    public ReactivePropertySlim<VoiceEngineConnection> SelectedEngine { get; } = new(VoiceEngineConnection.Core);
    public ReactivePropertySlim<VoiceDictionaryWord[]> VisibleWords { get; } = new([]);
    public ReactivePropertySlim<VoiceDictionaryWord?> SelectedWord { get; } = new();
    public ReactivePropertySlim<string> Search { get; } = new("");
    public ReactivePropertySlim<string> EmptyListMessage { get; } = new("");
    public ReactivePropertySlim<string> Message { get; } = new("");
    public ReactivePropertySlim<bool> IsBusy { get; } = new();
    public ReactivePropertySlim<bool> HasDictionary { get; } = new();
    public ReactivePropertySlim<bool> IsReadOnly { get; } = new();
    public ReactivePropertySlim<bool> CanEdit { get; } = new();
    public ReactivePropertySlim<bool> CanSave { get; } = new();
    public ReactivePropertySlim<bool> IsEditing { get; } = new();
    public ReactivePropertySlim<bool> IsListing { get; } = new();
    public ReactivePropertySlim<bool> IsDirty { get; } = new();
    public ReactivePropertySlim<bool> ConfirmChange { get; } = new();
    public ReactivePropertySlim<VoiceDictionaryWord?> WordToDelete { get; } = new();
    public ReactivePropertySlim<bool> CanUseCompound { get; } = new();
    public ReactivePropertySlim<bool> CanRemovePhrase { get; } = new();
    public ReactivePropertySlim<DictionaryWordType[]> WordTypes { get; } = new(DictionaryWordType.All[..5]);
    public ReactivePropertySlim<DictionaryWordType?> SelectedWordType { get; } = new(DictionaryWordType.All[0]);
    public ReactivePropertySlim<int> Priority { get; } = new(5);
    public ObservableCollection<DictionaryPhraseViewModel> Phrases { get; } = [];
    public ReactivePropertySlim<VoiceMetadata[]> Voices { get; } = new([]);
    public ReactivePropertySlim<VoiceMetadata?> SelectedVoice { get; } = new();
    public ReactivePropertySlim<VoiceStyle[]> Styles { get; } = new([]);
    public ReactivePropertySlim<VoiceStyle?> SelectedStyle { get; } = new();
    public ReactivePropertySlim<DictionaryImportItem[]> ImportItems { get; } = new([]);
    public ReactivePropertySlim<bool> HasImport { get; } = new();
    public ReactivePropertySlim<string> ImportSummary { get; } = new("");
    public ReactivePropertySlim<bool> OverwriteImport { get; } = new();

    public VoiceDictionaryViewModel()
    {
        RefreshEngines();
        Search.Subscribe(_ => FilterWords()).AddTo(_subscriptions);
        SelectedWordType.Subscribe(_ => UpdateDirty()).AddTo(_subscriptions);
        Priority.Subscribe(_ => UpdateDirty()).AddTo(_subscriptions);
        IsBusy.Subscribe(_ => UpdateCanEdit()).AddTo(_subscriptions);
        HasDictionary.Subscribe(_ => UpdateCanEdit()).AddTo(_subscriptions);
        IsReadOnly.Subscribe(_ => UpdateCanEdit()).AddTo(_subscriptions);
        IsDirty.Subscribe(_ => UpdateCanEdit()).AddTo(_subscriptions);
        HasImport.Subscribe(_ => UpdateCanEdit()).AddTo(_subscriptions);
        SelectedVoice.Subscribe(voice =>
        {
            long? style = SelectedStyle.Value?.Id;
            Styles.Value = voice?.Styles ?? [];
            SelectedStyle.Value = Styles.Value.FirstOrDefault(x => x.Id == style) ?? Styles.Value.FirstOrDefault();
        }).AddTo(_subscriptions);
        VoiceEngineStore.Changed += ConnectionsChanged;
        Disposable.Create(() => VoiceEngineStore.Changed -= ConnectionsChanged).AddTo(_subscriptions);
        ResetDraft(null);
    }

    private void UpdateCanEdit()
    {
        CanEdit.Value = HasDictionary.Value && !IsReadOnly.Value && !IsBusy.Value;
        CanSave.Value = CanEdit.Value && IsDirty.Value;
        IsEditing.Value = HasDictionary.Value && _editorOpen && !HasImport.Value;
        IsListing.Value = HasDictionary.Value && !_editorOpen && !HasImport.Value;
    }

    private void ConnectionsChanged() => Dispatcher.UIThread.Post(() => { if (!_disposed) RefreshEngines(); });

    private void RefreshEngines()
    {
        try
        {
            var selected = SelectedEngine.Value;
            var engines = new[] { VoiceEngineConnection.Core }.Concat(VoiceEngineStore.Load()).ToArray();
            var refreshed = engines.FirstOrDefault(x => x.Id == selected.Id && x.Url == selected.Url);
            // Retain a removed selection long enough to save/export or discard its open draft.
            if (refreshed == null) engines = [.. engines, selected];
            Engines.Value = engines;
            SelectedEngine.Value = refreshed ?? selected;
        }
        catch (Exception ex) { Message.Value = ex.Message; }
    }

    public Task Activate()
    {
        RefreshEngines();
        return HasDictionary.Value && (_editorOpen || HasImport.Value) ? Task.CompletedTask : Refresh();
    }

    private void SetEditorOpen(bool open)
    {
        _editorOpen = open;
        CancelDelete();
        UpdateCanEdit();
    }

    public Task ChooseEngine(VoiceEngineConnection engine)
    {
        if (_disposed || engine.Id == SelectedEngine.Value.Id && engine.Url == SelectedEngine.Value.Url) return Task.CompletedTask;
        return RequestChange(async () =>
        {
            SetEditorOpen(false);
            SelectedEngine.Value = engine;
            _words = [];
            FilterWords();
            HasDictionary.Value = false;
            ClearImport();
            ResetDraft(null);
            await Load();
        });
    }

    public Task ChooseWord(VoiceDictionaryWord? word) => word?.Id == SelectedWord.Value?.Id && IsEditing.Value
        ? Task.CompletedTask : RequestChange(() => { ResetDraft(word); SetEditorOpen(true); return Task.CompletedTask; });

    public Task NewWord() => RequestChange(() => { ResetDraft(null); SetEditorOpen(true); return Task.CompletedTask; });
    public Task BackToList() => RequestChange(() => { SetEditorOpen(false); return Task.CompletedTask; });
    public Task Refresh() => RequestChange(Load);

    private Task RequestChange(Func<Task> action)
    {
        if (_disposed) return Task.CompletedTask;
        if (IsDirty.Value)
        {
            _pendingChange = action;
            ConfirmChange.Value = true;
            return Task.CompletedTask;
        }
        return action();
    }

    public async Task SaveAndContinue()
    {
        if (await Save()) await DiscardAndContinue();
    }

    public async Task DiscardAndContinue()
    {
        var pending = _pendingChange;
        CancelChange();
        if (pending != null)
        {
            ResetDraft(SelectedWord.Value);
            await pending();
        }
    }

    public void CancelChange() { _pendingChange = null; ConfirmChange.Value = false; }

    private void ResetDraft(VoiceDictionaryWord? word)
    {
        _updating = true;
        try
        {
            foreach (var phrase in Phrases) phrase.Dispose();
            Phrases.Clear();
            SelectedWord.Value = word;
            SelectedWordType.Value = word == null ? DictionaryWordType.All[0] : word.WordType;
            Priority.Value = word?.Priority ?? 5;
            foreach (var phrase in word?.Phrases ?? [new("", "", 0)]) Phrases.Add(new(phrase, UpdateDirty));
            CancelDelete();
            CanRemovePhrase.Value = Phrases.Count > 1;
            _baseline = DraftSnapshot();
            IsDirty.Value = false;
        }
        finally { _updating = false; }
    }

    private string DraftSnapshot() => JsonSerializer.Serialize(new
    {
        Type = SelectedWordType.Value?.Id, Priority = Priority.Value, Phrases = Phrases.Select(x => x.Capture()).ToArray()
    });

    private void UpdateDirty() { if (!_updating) IsDirty.Value = DraftSnapshot() != _baseline; }

    public void AddPhrase()
    {
        if (!CanUseCompound.Value || !CanEdit.Value) return;
        Phrases.Add(new(new("", "", 0), UpdateDirty));
        CanRemovePhrase.Value = Phrases.Count > 1;
        UpdateDirty();
    }

    public void RemovePhrase(DictionaryPhraseViewModel phrase)
    {
        if (!CanEdit.Value || Phrases.Count <= 1 || !Phrases.Remove(phrase)) return;
        phrase.Dispose();
        CanRemovePhrase.Value = Phrases.Count > 1;
        UpdateDirty();
    }

    private VoiceDictionaryWord Capture() => VoiceDictionaryWord.Create(SelectedWord.Value?.Id ?? Guid.NewGuid(),
        Phrases.Select(x => x.Capture()).ToArray(), SelectedWordType.Value ?? throw new ArgumentException("品詞を選択してください。"),
        Priority.Value, CanUseCompound.Value, SelectedWord.Value?.Data);

    private void FilterWords()
    {
        string search = Search.Value.Trim();
        VisibleWords.Value = _words.Where(x => x.Surface.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || x.Pronunciation.Contains(search, StringComparison.CurrentCultureIgnoreCase)).OrderBy(x => x.Surface).ToArray();
        EmptyListMessage.Value = VisibleWords.Value.Length > 0 ? "" : _words.Length == 0 ? "単語がありません" : "見つかりません";
    }

    public void ClearSearch() => Search.Value = "";

    private Task Load() => Run(async (engine, token) => await LoadWithin(engine, token, notifyExternalChanges: true));

    private async Task LoadWithin(VoiceEngineConnection engine, CancellationToken token, Guid? select = null, bool notifyExternalChanges = false)
    {
        if (!engine.IsCore) engine = await HttpVoiceEngine.DiscoverAsync(engine, token);
        var words = await VoiceDictionaryService.LoadAsync(engine, token);
        token.ThrowIfCancellationRequested();
        if (_disposed) return;
        if (notifyExternalChanges && HasDictionary.Value && engine.Id == SelectedEngine.Value.Id
            && !System.Text.Json.Nodes.JsonNode.DeepEquals(VoiceDictionaryService.ToJson(_words), VoiceDictionaryService.ToJson(words)))
            VoiceDictionaryService.NotifyChanged(engine.Id);
        Engines.Value = Engines.Value.Select(x => x.Id == engine.Id ? engine : x).ToArray();
        SelectedEngine.Value = engine;
        CanUseCompound.Value = VoiceDictionaryService.IsAivis(engine);
        WordTypes.Value = CanUseCompound.Value ? DictionaryWordType.All : DictionaryWordType.All[..5];
        _words = words;
        FilterWords();
        HasDictionary.Value = true;
        IsReadOnly.Value = false;
        var voices = engine.IsCore ? TtsLoader.VoiceVoxLoader.Value!.GetVoices() : engine.Voices;
        string? speaker = SelectedVoice.Value?.SpeakerUuid;
        long? selectedStyle = SelectedStyle.Value?.Id;
        Voices.Value = voices;
        SelectedVoice.Value = voices.FirstOrDefault(x => x.SpeakerUuid == speaker) ?? voices.FirstOrDefault();
        SelectedStyle.Value = Styles.Value.FirstOrDefault(x => x.Id == selectedStyle) ?? Styles.Value.FirstOrDefault();
        Guid? selectedId = select ?? SelectedWord.Value?.Id;
        var selectedWord = words.FirstOrDefault(x => x.Id == selectedId);
        ResetDraft(selectedWord);
        if (_editorOpen && selectedId.HasValue && selectedWord == null) SetEditorOpen(false);
        Message.Value = "";
    }

    public async Task<bool> Save()
    {
        if (!CanEdit.Value) return false;
        try
        {
            var word = Capture();
            Guid? id = SelectedWord.Value?.Id;
            return await Run(async (engine, token) =>
            {
                var saved = await VoiceDictionaryService.SaveAsync(engine, id, word, token);
                // Saving succeeded even if a subsequent refresh fails: retain the saved identity.
                ResetDraft(word with { Id = saved });
                await LoadWithin(engine, token, saved);
                SetEditorOpen(false);
            });
        }
        catch (Exception ex) { Message.Value = ex.Message; return false; }
    }

    public void AskDelete(VoiceDictionaryWord word)
    {
        if (CanEdit.Value) WordToDelete.Value = word;
    }

    public void CancelDelete() => WordToDelete.Value = null;

    public async Task Delete()
    {
        if (!CanEdit.Value || WordToDelete.Value is not { } word) return;
        await Run(async (engine, token) =>
        {
            await VoiceDictionaryService.DeleteAsync(engine, word.Id, token);
            ResetDraft(null);
            await LoadWithin(engine, token);
            SetEditorOpen(false);
        });
    }

    public Task PrepareImport(string json) => RequestChange(() =>
    {
        try
        {
            var entries = VoiceDictionaryService.PrepareImport(json, SelectedEngine.Value, _words.Select(x => x.Id));
            ImportItems.Value = entries.Select(x => new DictionaryImportItem(x)).ToArray();
            OverwriteImport.Value = false;
            HasImport.Value = true;
            var summary = new List<string> { $"{entries.Length}件" };
            int duplicates = entries.Count(x => x.Duplicate);
            int incompatible = entries.Count(x => x.Error != null);
            if (duplicates > 0) summary.Add($"重複 {duplicates}件");
            if (incompatible > 0) summary.Add($"非対応 {incompatible}件");
            ImportSummary.Value = string.Join(" · ", summary);
            Message.Value = "";
        }
        catch (Exception ex) { Message.Value = $"辞書を読み込めませんでした: {ex.Message}"; }
        return Task.CompletedTask;
    });

    public Task Import() => RequestChange(() => Run(async (engine, token) =>
    {
        var words = ImportItems.Value.Where(x => x.IsCompatible && x.Selected.Value).Select(x => x.Entry.Word!).ToArray();
        if (words.Length == 0) { Message.Value = "取り込む単語を選択してください。"; return; }
        await VoiceDictionaryService.ImportAsync(engine, words, OverwriteImport.Value, token);
        SetEditorOpen(false);
        ClearImport();
        await LoadWithin(engine, token);
    }));

    public void ClearImport() { ImportItems.Value = []; HasImport.Value = false; Message.Value = ""; }

    public async Task<string?> Export()
    {
        string? json = null;
        await Run(async (engine, token) => json = VoiceDictionaryService.Export(await VoiceDictionaryService.LoadAsync(engine, token)));
        return json;
    }

    public Task Preview() => Run(async (engine, token) =>
    {
        if (SelectedVoice.Value is not { } voice || SelectedStyle.Value is not { } style)
            throw new ArgumentException("試聴する話者とスタイルを選択してください。");
        var settings = new VoiceSettings
        {
            EngineId = engine.Id, EngineName = engine.Name, EngineUuid = engine.EngineUuid, EngineVersion = engine.Version,
            SpeakerUuid = voice.SpeakerUuid, SpeakerName = voice.Name, StyleId = style.Id, StyleName = style.Name,
            ModelVersion = voice.Version, TempoDynamicsScale = CanUseCompound.Value ? 1 : null
        };
        var result = await VoiceDictionaryService.PreviewAsync(engine, Capture(), settings, token);
        token.ThrowIfCancellationRequested();
        using var player = new SimpleWavePlayer(result.Wave);
        await player.Play(token);
    });

    private async Task<bool> Run(Func<VoiceEngineConnection, CancellationToken, Task> action)
    {
        if (_disposed) return false;
        _request?.Cancel();
        var source = _request = new();
        int requestId = ++_requestId;
        IsBusy.Value = true;
        Message.Value = "";
        try
        {
            await action(SelectedEngine.Value, source.Token);
            return !_disposed && requestId == _requestId;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (!_disposed && requestId == _requestId)
            {
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized })
                {
                    IsReadOnly.Value = true;
                    Message.Value = "辞書へのアクセスが許可されていません。";
                }
                else Message.Value = ex is OperationCanceledException ? "エンジンから応答がありません。" : ex.Message;
            }
            return false;
        }
        finally
        {
            if (requestId == _requestId) { IsBusy.Value = false; _request = null; }
            source.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _request?.Cancel();
        _subscriptions.Dispose();
        foreach (var phrase in Phrases) phrase.Dispose();
        _pendingChange = null;
    }
}
