using Beutl.Editor.Services;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.ProjectSystem;

namespace Beutl.Extensions.Voice.Services;

internal static class VoicePlacement
{
    // Places one speech element as the speech tab's Add button does: on the first free layer from its own, linked to
    // the scene's character showing the same PSD or to one the template or preset brings, and captioned below it.
    // Call inside the history transaction that adds the speech.
    public static void Place(Scene scene, Element speech, VoiceLipSyncPreset? lipSync, Element? subtitleTemplate)
    {
        speech.ZIndex.Value = PsdLipSyncPresets.FreeLayer(scene, speech.Range, speech.ZIndex.Value);
        scene.AddChild(speech, ElementOverlapHandling.ThrowException);
        if (lipSync != null)
            PsdLipSyncPresets.Connect(scene, [(speech.Objects.OfType<TtsController>().Single(), lipSync)]);
        VoiceSubtitles.Place(scene, speech, subtitleTemplate);
    }
}
