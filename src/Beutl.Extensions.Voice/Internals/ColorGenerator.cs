using System.Runtime.CompilerServices;

namespace Beutl.Extensions.Voice.Internals;

// https://github.com/b-editor/beutl/blob/1a6e618d7e55e08199a4b71cd4f451b3401c42ef/src/Beutl.Editor.Components/Helpers/ColorGenerator.cs#L12
public static class ColorGenerator
{
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "GenerateColor")]
    public static extern Media.Color GenerateColor(
        [UnsafeAccessorType("Beutl.Editor.Components.Helpers.ColorGenerator, Beutl.Editor.Components")] object? _,
        string str);
}