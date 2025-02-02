using Beutl.Extensibility;
using Beutl.Extensions.Voice.Operators;
using Beutl.Services;

namespace Beutl.Extensions.Voice;

[Export]
public class ControllerExtension : Extension
{
    public override string Name => "Text-to-Speech";

    public override string DisplayName => "テキスト読み上げ";

    public override void Load()
    {
        base.Load();
        LibraryService.Current.RegisterGroup("テキスト読み上げ", g => g
            .AddSourceOperator<TtsController>("コントローラー"));
    }
}