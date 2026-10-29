using Avalonia.Controls;
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
}
