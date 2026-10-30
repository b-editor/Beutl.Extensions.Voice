using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Beutl.Extensions.Voice.ViewModels;

namespace Beutl.Extensions.Voice.Views;

public partial class VoiceEditorView : UserControl
{
    public VoiceEditorView()
    {
        InitializeComponent();
        // One history entry per slider gesture, including keyboard adjustments.
        AddHandler(PointerReleasedEvent, (_, _) => (DataContext as VoiceEditorViewModel)?.ConfirmEdit(),
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, _) => (DataContext as VoiceEditorViewModel)?.ConfirmEdit(),
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnAccentPhrasesChanged(object? sender, EventArgs e) =>
        (DataContext as VoiceEditorViewModel)?.OnAccentPhrasesUpdated();

    private void AddConnectionClick(object? sender, RoutedEventArgs e) => ShowConnectionEditor(edit: false);
    private void EditConnectionClick(object? sender, RoutedEventArgs e) => ShowConnectionEditor(edit: true);
    private async void InstallCoreClick(object? sender, RoutedEventArgs e) => await VoiceVoxInstallDialog.ShowFor(this);

    private void ShowConnectionEditor(bool edit)
    {
        if (DataContext is not VoiceEditorViewModel model) return;
        var connection = edit ? model.SelectedEngine.Value : null;
        if (connection?.IsCore == true) return;
        bool restoreMissing = connection != null && model.IsMissingConnection(connection.Id);
        var name = new TextBox { Name = "ConnectionNameInput", Text = connection?.Name, PlaceholderText = "表示名（省略可）" };
        var url = new TextBox { Name = "ConnectionUrlInput", Text = connection?.Url, PlaceholderText = "http://127.0.0.1:50021" };
        var save = new Button { Name = "SaveConnectionButton", Content = "接続して保存", Classes = { "accent" } };
        model.ConnectionMessage.Value = "";
        var status = new TextBlock
        {
            [!TextBlock.TextProperty] = new Binding("ConnectionMessage.Value") { Source = model },
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12
        };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Width = 260, Spacing = 8,
                Children = { new TextBlock { Text = "VOICEVOX互換エンジンの接続先" }, name,
                    new TextBlock { Text = "送信先URL" }, url, save, status }
            }
        };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            bool saved = await model.SaveConnection(name.Text ?? "", url.Text ?? "", connection?.Id, restoreMissing);
            save.IsEnabled = true;
            if (saved) flyout.Hide();
        };
        flyout.Opened += (_, _) => url.Focus();
        FlyoutBase.SetAttachedFlyout(EngineMenuButton, flyout);
        FlyoutBase.ShowAttachedFlyout(EngineMenuButton);
    }
}
