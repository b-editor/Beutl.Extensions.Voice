using Beutl.Audio;
using Beutl.Audio.Effects;
using Beutl.Editor;
using Beutl.Editor.Services;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.ProjectSystem;
using Beutl.Services;

namespace Beutl.Extensions.Voice.Services;

// Places speech downloaded from a text-to-speech site as the speech tab's Add button places generated speech.
internal sealed class WebVoiceImport(
    Func<string> storePath,
    Func<WebVoiceAssignmentViewModel, CancellationToken, Task<WebVoiceAssignment?>> chooseAssignment)
{
    public static WebVoiceImport Default { get; } = new(() => WebVoiceAssignmentStore.GlobalPath, WebVoiceAssignmentDialog.ShowAsync);

    public static BrowserDownloadClaim? Claim(BrowserDownloadOffer offer) => WebVoiceSites.Find(offer) is { } site
        ? new BrowserDownloadClaim { PageScript = site.PageScript, Description = $"{site.Name}の音声を読み上げ要素として追加" }
        : null;

    public async ValueTask<bool> ImportAsync(BrowserDownloadImportContext context, CancellationToken cancellationToken)
    {
        IEditorContext editor = context.EditorContext;
        if (WebVoiceSites.Find(context.Offer) is not { } site
            || editor.GetService(typeof(Scene)) is not Scene { Uri: not null } scene
            || editor.GetService(typeof(HistoryManager)) is not HistoryManager history)
            return false;

        var page = WebVoiceSites.ReadPage(context.PageData);
        string path = storePath();
        var assignment = WebVoiceAssignmentStore.Find(path, site.Id, page.VoiceId);
        if (assignment == null)
        {
            var model = new WebVoiceAssignmentViewModel(scene, site, page.VoiceId, page.VoiceName,
                defaults: editor.FindToolTab<TtsTabViewModel>());
            assignment = await chooseAssignment(model, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Declining keeps the saved file out of the timeline, as when the user cancels a download.
            if (assignment == null) return true;
            WebVoiceAssignmentStore.Save(path, assignment);
            // The editor can close while the user chooses.
            if (!ReferenceEquals(editor.GetService(typeof(Scene)), scene)) return true;
        }

        var start = (editor.GetService(typeof(IEditorClock)) as IEditorClock)?.CurrentTime.Value ?? MediaTime.Zero;
        Place(scene, history, context.FileName, page.Text, start, assignment, site.Resampling);
        if (string.IsNullOrWhiteSpace(page.Text))
            NotificationService.ShowWarning("本文を読み取れませんでした",
                $"{site.Name}のページから本文を読み取れなかったため、字幕を追加していません。TtsControllerに本文を入力してください。");
        return true;
    }

    public static Element Place(Scene scene, HistoryManager history, string path, string text, MediaTime start,
        WebVoiceAssignment assignment, ResamplingMethod? resampling)
    {
        var element = VoiceElementFactory.Create(scene, path, text, generation: null, start, 1);
        if (resampling is { } method)
        {
            // First in the chain, so the effects added to the speech later run at the project's rate.
            var effects = (AudioEffectGroup)element.Objects.OfType<SourceSound>().Single().Effect.Value!;
            effects.Children.Insert(0, new ResamplingEffect { Method = { Value = method } });
        }
        // Without the text there is nothing to caption.
        var subtitle = string.IsNullOrWhiteSpace(text) ? null : VoiceSubtitles.Load(assignment.Subtitle);
        history.ExecuteInTransaction(() => VoicePlacement.Place(scene, element, null, assignment.LipSync, subtitle));
        return element;
    }
}
