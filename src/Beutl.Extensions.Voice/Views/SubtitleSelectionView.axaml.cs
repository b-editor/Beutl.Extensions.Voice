using Avalonia.Controls;
using Beutl.Extensions.Voice.ViewModels;

namespace Beutl.Extensions.Voice.Views;

public partial class SubtitleSelectionView : UserControl
{
    public SubtitleSelectionView() => InitializeComponent();

    private void RefreshTemplates(object? sender, EventArgs e)
    {
        if (DataContext is SubtitleSelectionViewModel model) model.Refresh();
    }
}
