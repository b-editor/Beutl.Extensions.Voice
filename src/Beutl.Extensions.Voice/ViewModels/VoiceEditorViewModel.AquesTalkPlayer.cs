using Beutl.Extensions.Voice.Models;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

// AquesTalkPlayer's own values, which start from the selected preset and are spoken through a temporary preset.
public partial class VoiceEditorViewModel
{
    // The voice type the values belong to, from the preset or from the saved settings.
    private AquesTalkPlayerVoice? _aquesTalkPlayerVoice;

    public ReactivePropertySlim<bool> AquesTalkFlatReading { get; } = new();
    public ReactivePropertySlim<double> AquesTalkSpeed { get; } = new(100);
    public ReactivePropertySlim<double> AquesTalkVolume { get; } = new(100);
    public ReactivePropertySlim<double> AquesTalkHeight { get; } = new(100);
    public ReactivePropertySlim<double> AquesTalkAccent { get; } = new(100);
    public ReactivePropertySlim<double> AquesTalkQuality { get; } = new(100);
    public ReactivePropertySlim<double> AquesTalkFrequencyScale { get; } = new(100);
    // Height, accent and voice quality apply to AquesTalk10 only.
    public ReactivePropertySlim<bool> IsAquesTalk10 { get; } = new();
    // Beutl's own choice rather than the preset's: reading VOICEVOX's analysis, whose accents can be edited.
    public ReactivePropertySlim<bool> AquesTalkAnalyzeWithVoiceVox { get; } = new();

    private AquesTalkPlayerVoice? CaptureAquesTalkPlayerVoice() => _aquesTalkPlayerVoice is not { } voice ? null : voice with
    {
        FlatReading = AquesTalkFlatReading.Value, Speed = Round(AquesTalkSpeed), Volume = Round(AquesTalkVolume),
        Height = Round(AquesTalkHeight), Accent = Round(AquesTalkAccent), Quality = Round(AquesTalkQuality),
        FrequencyScale = Round(AquesTalkFrequencyScale)
    };

    private static int Round(ReactivePropertySlim<double> property) => (int)Math.Round(property.Value);

    private void ShowAquesTalkPlayerVoice(AquesTalkPlayerVoice? voice)
    {
        _aquesTalkPlayerVoice = voice;
        IsAquesTalk10.Value = voice?.IsAquesTalk10 == true;
        if (voice == null) return;
        AquesTalkFlatReading.Value = voice.FlatReading;
        AquesTalkSpeed.Value = voice.Speed;
        AquesTalkVolume.Value = voice.Volume;
        AquesTalkHeight.Value = voice.Height;
        AquesTalkAccent.Value = voice.Accent;
        AquesTalkQuality.Value = voice.Quality;
        AquesTalkFrequencyScale.Value = voice.FrequencyScale;
    }
}
