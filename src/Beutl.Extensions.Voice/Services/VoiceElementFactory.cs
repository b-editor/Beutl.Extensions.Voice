using Beutl.Audio;
using Beutl.Audio.Effects;
using Beutl.Editor.Services;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Media.Source;
using Beutl.ProjectSystem;
using Beutl.Utilities;

namespace Beutl.Extensions.Voice.Services;

internal static class VoiceElementFactory
{
    // The resampling is the one the speech came with, from its engine or the site that made it.
    public static Element Create(Scene scene, string path, VoiceGeneration generation, MediaTime start, int layer,
        ResamplingMethod? resampling) =>
        Create(scene, path, generation.Text, generation, start, layer, resampling);

    // Speech made elsewhere, such as on a text-to-speech site, has no settings to regenerate it with.
    public static Element Create(Scene scene, string path, string text, VoiceGeneration? generation, MediaTime start, int layer,
        ResamplingMethod? resampling)
    {
        var element = new Element
        {
            Start = { Value = start }, ZIndex = { Value = layer },
            Uri = RandomFileNameGenerator.GenerateUri(scene.Uri!, "belm"),
            Name = { Value = string.IsNullOrWhiteSpace(text) ? Path.GetFileNameWithoutExtension(path) : text.ReplaceLineEndings(" ") },
            AccentColor = { Value = ElementAccentColorGenerator.GenerateColor(typeof(TtsController).FullName!) }
        };
        var sound = new SourceSound { Source = { Value = SoundSource.Open(path) } };
        element.AddObject(sound);
        element.AddObject(new TtsController { Text = { Value = text }, Generation = { Value = generation } });
        if (!element.TryGetOriginalDuration(out var duration) || duration <= MediaTime.Zero)
            throw new InvalidOperationException(generation == null
                ? "音声の長さを取得できませんでした。" : "生成した音声の長さを取得できませんでした。");
        element.Length.Value = duration;
        if (resampling is { } method) AddResampling(sound, method);
        return element;
    }

    // First in the chain, so the effects added to the speech later run at the project's rate.
    internal static void AddResampling(SourceSound sound, ResamplingMethod method) =>
        ((AudioEffectGroup)sound.Effect.Value!).Children.Insert(0, new ResamplingEffect { Method = { Value = method } });
}
