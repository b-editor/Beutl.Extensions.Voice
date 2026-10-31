using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Beutl.Graphics.Shapes;
using Beutl.ProjectSystem;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public sealed record SubtitleChoice(Guid? Id, string Name, VoiceSubtitleTemplate? Template = null);

public sealed class SubtitleSelectionViewModel(Scene scene)
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
        choices.AddRange(scene.Children.Where(x => x.EnumerateAllChildren<TextBlock>().Any())
            .Select(x => new SubtitleChoice(x.Id.Value, string.IsNullOrWhiteSpace(x.Name.Value) ? "テキスト要素" : x.Name.Value)));
        if (selectedId != null && choices.All(x => x.Id != selectedId)) choices.Add(new(selectedId, "テンプレートが見つかりません"));
        Choices.Value = choices.ToArray();
        Selected.Value = choices.First(x => x.Id == selectedId && x.Template == template);
    }

    internal VoiceSubtitleTemplate? CaptureTemplate()
    {
        if (Template != null) return Template;
        if (SelectedId is not { } id) return null;
        if (id == Guid.Empty) return VoiceSubtitles.Default(scene);
        var element = scene.Children.FirstOrDefault(x => x.Id.Value == id)
            ?? throw new InvalidOperationException("字幕テンプレートの要素が見つかりません。選び直してください。");
        return VoiceSubtitles.Capture(element);
    }
}
