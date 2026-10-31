using Avalonia.Controls;
using Beutl.Extensions.Voice.ViewModels;

namespace Beutl.Extensions.Voice.Views;

public partial class LipSyncSelectionView : UserControl
{
    public LipSyncSelectionView() => InitializeComponent();

    private void RefreshTargets(object? sender, EventArgs e)
    {
        if (DataContext is LipSyncSelectionViewModel model) model.Refresh();
    }
}
