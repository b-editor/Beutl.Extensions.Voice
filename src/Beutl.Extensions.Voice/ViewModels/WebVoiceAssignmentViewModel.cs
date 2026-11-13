using Beutl.Editor.Services;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Beutl.ProjectSystem;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

// Chooses the character and caption for the speech that one voice of a site makes.
public sealed class WebVoiceAssignmentViewModel
{
    private readonly WebVoiceSite _site;

    internal WebVoiceAssignmentViewModel(Scene scene, WebVoiceSite site, string voiceId, string voiceName,
        WebVoiceAssignment? current = null, TtsTabViewModel? defaults = null,
        Func<IEnumerable<ObjectTemplateItem>>? templates = null, string? presetPath = null)
    {
        _site = site;
        VoiceId = voiceId;
        VoiceName = voiceName;
        LipSync = new LipSyncSelectionViewModel(templates);
        Subtitles = new SubtitleSelectionViewModel(scene, templates);
        Subtitles.Refresh();
        if (current != null)
        {
            LipSync.Preset = current.LipSync;
            Subtitles.Template = current.Subtitle;
        }
        else if (defaults != null)
        {
            // Start from what the speech tab's Add button would use.
            if (defaults.LipSync.SelectedId is { } characterTemplate) LipSync.SelectedId = characterTemplate;
            else LipSync.Preset = defaults.LipSync.Preset;
            if (defaults.Subtitles.SelectedId is { } template) Subtitles.SelectedId = template;
            else Subtitles.Template = defaults.Subtitles.Template;
        }

        try
        {
            Presets = VoicePresetStore.Load(presetPath ?? VoicePresetStore.GlobalPath)
                .Where(x => x.LipSync != null || x.Subtitle != null).ToArray();
        }
        catch (Exception ex)
        {
            Presets = [];
            Message.Value = $"プリセットを読み込めませんでした: {ex.Message}";
        }
        SelectedPreset.Subscribe(preset =>
        {
            if (preset == null) return;
            LipSync.Preset = preset.LipSync;
            Subtitles.Template = preset.Subtitle;
        });
    }

    public string VoiceId { get; }
    public string VoiceName { get; }
    public string SiteName => _site.Name;
    public string VoiceLabel => WebVoiceSites.DescribeVoice(VoiceName, VoiceId);

    public string Description =>
        $"{_site.Name}の「{VoiceLabel}」で作った音声に使う立ち絵と字幕を選んでください。" +
        "次からはこの声の音声に自動で使います。テキスト読み上げタブの「設定」で変更できます。";

    public LipSyncSelectionViewModel LipSync { get; }
    public SubtitleSelectionViewModel Subtitles { get; }

    // Presets carrying a character or a caption, to copy them from.
    public VoicePreset[] Presets { get; }
    public bool HasPresets => Presets.Length > 0;
    public ReactivePropertySlim<VoicePreset?> SelectedPreset { get; } = new();
    public ReactivePropertySlim<string> Message { get; } = new("");

    internal WebVoiceAssignment? TryCapture()
    {
        try
        {
            var assignment = new WebVoiceAssignment
            {
                SiteId = _site.Id, VoiceId = VoiceId, VoiceName = VoiceName,
                LipSync = LipSync.CapturePreset(), Subtitle = Subtitles.CaptureTemplate()
            };
            Message.Value = "";
            return assignment;
        }
        catch (Exception ex)
        {
            Message.Value = ex.Message;
            return null;
        }
    }
}
