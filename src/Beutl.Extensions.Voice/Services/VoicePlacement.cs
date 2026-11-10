using Beutl.Editor.Services;
using Beutl.Engine;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.ProjectSystem;

namespace Beutl.Extensions.Voice.Services;

internal static class VoicePlacement
{
    // Places one speech element as the speech tab's Add button does: on the first free layer from its own, linked to
    // a character in the scene or one a preset brings, and captioned below it.
    // Call inside the history transaction that adds the speech.
    public static void Place(Scene scene, Element speech, EngineObject? lipSyncTarget, VoiceLipSyncPreset? lipSyncPreset,
        Element? subtitleTemplate)
    {
        speech.ZIndex.Value = PsdLipSyncPresets.FreeLayer(scene, speech.Range, speech.ZIndex.Value);
        scene.AddChild(speech, ElementOverlapHandling.ThrowException);
        var controller = speech.Objects.OfType<TtsController>().Single();
        if (lipSyncTarget != null)
        {
            PsdLipSync.Connect(controller, lipSyncTarget);
            PsdLipSyncPresets.Extend(scene, lipSyncTarget, speech.Range);
        }
        else if (lipSyncPreset != null)
            PsdLipSyncPresets.Connect(scene, [(controller, lipSyncPreset)]);
        VoiceSubtitles.Place(scene, speech, subtitleTemplate);
    }
}
