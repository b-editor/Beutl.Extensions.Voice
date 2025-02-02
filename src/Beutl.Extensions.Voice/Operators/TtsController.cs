using System.ComponentModel.DataAnnotations;
using Beutl.Graphics.Shapes;
using Beutl.Operation;

namespace Beutl.Extensions.Voice.Operators;

public class TtsController : SourceOperator
{
    public static readonly CoreProperty<string> TextProperty;

    static TtsController()
    {
        TextProperty = ConfigureProperty<string, TtsController>(nameof(Text))
            .Register();
    }

    public TtsController()
    {
        Properties.Add(new CorePropertyAdapter<string>(TextProperty, this));
    }

    [DataType(DataType.MultilineText)]
    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}