#if DEBUG

using Avalonia;
using Avalonia.Diagnostics;
using Avalonia.Threading;
using Beutl.Extensibility;

namespace Beutl.Extensions.Voice;

[Export]
public class DevToolsAttacher : Extension
{
    public override void Load(IExtensionContext context)
    {
        base.Load(context);
        Dispatcher.UIThread.Post(() =>
        {
            Application.Current?.AttachDeveloperTools();
        });
    }

    public override string Name => "DevToolsAttacher";

    public override string DisplayName => Name;
}
#endif