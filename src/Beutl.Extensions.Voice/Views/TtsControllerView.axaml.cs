using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Beutl.Extensions.Voice.ViewModels;

namespace Beutl.Extensions.Voice.Views;

public partial class TtsControllerView : UserControl
{
    public TtsControllerView()
    {
        InitializeComponent();
    }

    private void RefreshTemplates(object? sender, EventArgs e)
    {
        if (DataContext is TtsControllerViewModel model) model.RefreshLipSyncTemplates();
    }
}
