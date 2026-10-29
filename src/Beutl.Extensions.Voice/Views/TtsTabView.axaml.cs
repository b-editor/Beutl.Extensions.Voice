using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Beutl.Controls;
using Beutl.Extensions.Voice.ViewModels;

namespace Beutl.Extensions.Voice.Views;

public partial class TtsTabView : UserControl
{
    public TtsTabView()
    {
        InitializeComponent();
    }

    private async void DownloadVoiceVox(object? sender, RoutedEventArgs e)
    {
        var dialogViewModel = new VoiceVoxInstallDialogViewModel();
        var dialog = new VoiceVoxInstallDialog
        {
            DataContext = dialogViewModel
        };
        await dialog.ShowAsync();
    }

    private void OnAccentPhrasesChanged(object? sender, EventArgs e)
    {
        if (DataContext is TtsTabViewModel vm)
        {
            vm.OnAccentPhrasesUpdated();
        }
    }

    private void SavePresetClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TtsTabViewModel vm) return;
        var name = new TextBox
        {
            Name = "PresetNameInput",
            PlaceholderText = "プリセット名",
            Text = vm.PresetName.Value
        };
        var save = new Button { Name = "SavePresetButton", Content = "保存", Classes = { "accent" } };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Width = 240, Spacing = 8,
                Children = { new TextBlock { Text = "現在の設定をプリセットに保存" }, name, save }
            }
        };
        void Save()
        {
            vm.PresetName.Value = name.Text ?? "";
            vm.AddPreset();
            flyout.Hide();
        }
        save.Click += (_, _) => Save();
        name.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            Save();
            args.Handled = true;
        };
        flyout.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        FlyoutBase.SetAttachedFlyout(PresetMenuButton, flyout);
        FlyoutBase.ShowAttachedFlyout(PresetMenuButton);
    }

    private void RenamePresetClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TtsTabViewModel { SelectedPreset.Value: { } preset } vm) return;
        var flyout = new RenameFlyout { Text = preset.Name };
        flyout.Confirmed += (_, name) =>
        {
            vm.PresetName.Value = name ?? "";
            vm.RenamePreset();
        };
        flyout.ShowAt(PresetMenuButton);
    }

    private void ShowPresetDetails(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TtsTabViewModel vm)
            ShowDetails(PresetMenuButton, "保存値と現在値の差分", nameof(vm.PresetDetails) + ".Value");
    }

    private void ShowDetails(Control anchor, string title, string propertyPath)
    {
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Width = 260, Spacing = 8,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new ScrollViewer
                    {
                        MaxHeight = 320,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = new TextBlock
                        {
                            [!TextBlock.TextProperty] = new Binding(propertyPath) { Source = DataContext },
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        }
                    }
                }
            }
        };
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        FlyoutBase.ShowAttachedFlyout(anchor);
    }
}
