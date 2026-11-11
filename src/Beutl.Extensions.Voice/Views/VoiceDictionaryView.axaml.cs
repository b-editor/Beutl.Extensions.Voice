using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.ViewModels;

namespace Beutl.Extensions.Voice.Views;

public partial class VoiceDictionaryView : UserControl
{
    private bool _restoringSelection;
    public VoiceDictionaryView() => InitializeComponent();

    private async void EngineChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection || sender is not ComboBox selector || DataContext is not VoiceDictionaryViewModel model
            || selector.SelectedItem is not VoiceEngineConnection engine) return;
        await model.ChooseEngine(engine);
        _restoringSelection = true;
        try { selector.SelectedItem = model.SelectedEngine.Value; }
        finally { _restoringSelection = false; }
    }

    private async void WordTapped(object? sender, TappedEventArgs e)
    {
        var source = e.Source as Control;
        if (source is Button || source?.FindAncestorOfType<Button>() != null) return;
        var item = source as ListBoxItem ?? source?.FindAncestorOfType<ListBoxItem>();
        if (DataContext is VoiceDictionaryViewModel model && item?.DataContext is VoiceDictionaryWord word)
        {
            await model.ChooseWord(word);
            e.Handled = true;
        }
    }

    private async void WordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is Control source && (source is Button || source.FindAncestorOfType<Button>() != null)) return;
        if (e.Key is not (Key.Enter or Key.Space) || sender is not ListBox { SelectedItem: VoiceDictionaryWord word }
            || DataContext is not VoiceDictionaryViewModel model) return;
        await model.ChooseWord(word);
        e.Handled = true;
    }

    private void RemovePhrase(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VoiceDictionaryViewModel model && sender is Control { DataContext: DictionaryPhraseViewModel phrase })
            model.RemovePhrase(phrase);
    }

    private void DictionaryAccentChanged(object? sender, AccentChangedEventArgs e)
    {
        if (sender is Control { DataContext: DictionaryPhraseViewModel phrase }) phrase.Accent.Value = e.Accent;
    }

    private void AskDelete(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VoiceDictionaryViewModel model && sender is Control { DataContext: VoiceDictionaryWord word })
            model.AskDelete(word);
        e.Handled = true;
    }

    private async void OpenImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VoiceDictionaryViewModel model || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var engine = model.SelectedEngine.Value;
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "ユーザー辞書をインポート", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("ユーザー辞書（JSON）") { Patterns = ["*.json"] }]
            });
            if (files.Count == 0) return;
            using var file = files[0];
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string json = await reader.ReadToEndAsync();
            if (ReferenceEquals(DataContext, model) && model.SelectedEngine.Value.Id == engine.Id && model.SelectedEngine.Value.Url == engine.Url)
                await model.PrepareImport(json);
        }
        catch (Exception ex) { model.Message.Value = $"辞書を読み込めませんでした: {ex.Message}"; }
    }

    private async void ExportFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VoiceDictionaryViewModel model || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        if (await model.Export() is not { } json) return;
        try
        {
            using var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "ユーザー辞書をエクスポート", SuggestedFileName = "user-dictionary.json", DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("ユーザー辞書（JSON）") { Patterns = ["*.json"] }]
            });
            if (file == null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(json);
        }
        catch (Exception ex) { model.Message.Value = $"辞書を保存できませんでした: {ex.Message}"; }
    }
}
