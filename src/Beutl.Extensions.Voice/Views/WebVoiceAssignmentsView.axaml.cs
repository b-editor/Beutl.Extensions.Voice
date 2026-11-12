using Avalonia.Controls;
using Avalonia.Interactivity;
using Beutl.Extensions.Voice.ViewModels;

namespace Beutl.Extensions.Voice.Views;

public partial class WebVoiceAssignmentsView : UserControl
{
    public WebVoiceAssignmentsView() => InitializeComponent();

    private async void ChangeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WebVoiceAssignmentsViewModel model && sender is Control { DataContext: WebVoiceAssignmentItem item })
            await model.Change(item);
    }

    private void DeleteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WebVoiceAssignmentsViewModel model && sender is Control { DataContext: WebVoiceAssignmentItem item })
            model.Delete(item);
    }
}
