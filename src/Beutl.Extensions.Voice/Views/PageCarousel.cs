using Avalonia.Controls;
using Avalonia.Input;

namespace Beutl.Extensions.Voice.Views;

// Carousel switches pages on arrow, Home and End keys that its children leave unhandled,
// such as Right with the caret at the end of a TextBox. Pages here change only through the tabs.
public class PageCarousel : Carousel
{
    protected override Type StyleKeyOverride => typeof(Carousel);

    protected override void OnKeyDown(KeyEventArgs e)
    {
    }
}
