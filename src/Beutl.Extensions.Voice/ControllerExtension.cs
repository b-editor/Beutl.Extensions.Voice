using Beutl.Extensibility;
using Beutl.Extensions.Voice.Operators;
using Beutl.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Beutl.Extensions.Voice;

[Export]
public class ControllerExtension : Extension
{
    public override string Name => "Text-to-Speech";

    public override string DisplayName => "テキスト読み上げ";

    public override void Load(IExtensionContext context)
    {
        base.Load(context);
        context.Services.GetRequiredService<LibraryService>().RegisterGroup("テキスト読み上げ", g => g
            .AddEngineObject<TtsController>("コントローラー"));
    }
}