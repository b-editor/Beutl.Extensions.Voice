using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Beutl.Extensibility;

namespace Beutl.Extensions.Voice;

[Export]
public class DevToolsAttacher : Extension
{
    public override void Load()
    {
        base.Load();
        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.MainWindow?.AttachDevTools();
            }
        });
    }

    public override string Name => "DevToolsAttacher";

    public override string DisplayName => Name;
}