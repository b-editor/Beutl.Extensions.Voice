using Beutl.Editor.Services;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Beutl.ProjectSystem;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public sealed record SubtitleChoice(Guid? Id, string Name, VoiceSubtitleTemplate? Template = null);

public sealed class SubtitleSelectionViewModel(Scene scene, Func<IEnumerable<ObjectTemplateItem>>? loadTemplates = null)
{
    public ReactivePropertySlim<SubtitleChoice[]> Choices { get; } = new([]);
    public ReactivePropertySlim<SubtitleChoice?> Selected { get; } = new();

    public Guid? SelectedId
    {
        get => Selected.Value?.Id;
        set => Refresh(value, null);
    }

    public VoiceSubtitleTemplate? Template
    {
        get => Selected.Value?.Template;
        set => Refresh(null, value);
    }

    public void Refresh() => Refresh(SelectedId, Template);

    private void Refresh(Guid? selectedId, VoiceSubtitleTemplate? template)
    {
        List<SubtitleChoice> choices = [new(null, "字幕なし"), new(Guid.Empty, "標準字幕")];
        if (template != null) choices.Add(new(null, $"プリセット: {template.Name}", template));
        choices.AddRange(GetTemplates().Where(CanUseTemplate)
            .Select(x => new SubtitleChoice(x.Id, x.Name.Value)));
        if (selectedId != null && choices.All(x => x.Id != selectedId)) choices.Add(new(selectedId, "テンプレートを使用できません"));
        Choices.Value = choices.ToArray();
        Selected.Value = choices.First(x => x.Id == selectedId && x.Template == template);
    }

    internal VoiceSubtitleTemplate? CaptureTemplate()
    {
        if (Template != null) return Template;
        if (SelectedId is not { } id) return null;
        if (id == Guid.Empty) return VoiceSubtitles.Default(scene);
        var template = GetTemplates().FirstOrDefault(x => x.Id == id)
            ?? throw new InvalidOperationException("保存された字幕テンプレートが見つかりません。選び直してください。");
        var element = VoiceTemplates.CreateElement(template)
            ?? throw new InvalidOperationException("字幕テンプレートを読み込めませんでした。");
        return VoiceSubtitles.Capture(element);
    }

    private IEnumerable<ObjectTemplateItem> GetTemplates() => (loadTemplates ?? VoiceTemplates.Load)();

    private static bool CanUseTemplate(ObjectTemplateItem template)
    {
        if (VoiceTemplates.CreateElement(template) is not { } element) return false;
        try
        {
            // Capture also restores placeholders from a generated caption's saved bindings.
            VoiceSubtitles.Capture(element);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }
}
