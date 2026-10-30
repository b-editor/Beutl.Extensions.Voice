using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Beutl.Extensions.Voice.ViewModels;

namespace Beutl.Extensions.Voice.Views;

public partial class ScriptImportView : UserControl
{
    public ScriptImportView() => InitializeComponent();

    private void ImportScriptOnLostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is ScriptImportViewModel model) model.ImportText();
    }

    private async void OpenFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScriptImportViewModel model || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "台本を開く", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("テキスト") { Patterns = ["*.txt"] }, FilePickerFileTypes.All]
            });
            if (files.Count == 0) return;
            using var file = files[0];
            await using var stream = await file.OpenReadAsync();
            if (!ReferenceEquals(DataContext, model) || model.IsGenerating.Value) return;
            await model.ImportFile(stream);
        }
        catch (Exception ex) { model.Message.Value = $"台本を読み込めませんでした: {ex.Message}"; }
    }

}
