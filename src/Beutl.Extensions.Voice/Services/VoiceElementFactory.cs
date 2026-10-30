using Beutl.Audio;
using Beutl.Editor.Services;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Media.Source;
using Beutl.ProjectSystem;
using Beutl.Utilities;

namespace Beutl.Extensions.Voice.Services;

internal static class VoiceElementFactory
{
    public static Element Create(Scene scene, string path, VoiceGeneration generation, MediaTime start, int layer)
    {
        var element = new Element
        {
            Start = { Value = start }, ZIndex = { Value = layer },
            Uri = RandomFileNameGenerator.GenerateUri(scene.Uri!, "belm"),
            Name = { Value = generation.Text.ReplaceLineEndings(" ") },
            AccentColor = { Value = ElementAccentColorGenerator.GenerateColor(typeof(TtsController).FullName!) }
        };
        element.AddObject(new SourceSound { Source = { Value = SoundSource.Open(path) } });
        element.AddObject(new TtsController { Text = { Value = generation.Text }, Generation = { Value = generation } });
        if (!element.TryGetOriginalDuration(out var duration) || duration <= MediaTime.Zero)
            throw new InvalidOperationException("生成した音声の長さを取得できませんでした。");
        element.Length.Value = duration;
        return element;
    }
}
