using System.Text.Json.Nodes;
using Beutl.Audio;
using Beutl.Editor.Services;
using Beutl.Editor.Services.Captions;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Graphics.Transformation;
using Beutl.Media;
using Beutl.ProjectSystem;
using Beutl.Serialization;
using Beutl.Utilities;
using TextBlock = Beutl.Graphics.Shapes.TextBlock;

namespace Beutl.Extensions.Voice.Services;

internal static class VoiceSubtitles
{
    public const string Placeholder = "{text}";

    public static VoiceSubtitleTemplate Default(Scene scene)
    {
        var factory = new DefaultTextCaptionElementFactory(FontFamily.Default);
        var description = factory.CreateElements(new CaptionCue(TimeSpan.Zero, TimeSpan.FromSeconds(1), Placeholder),
            new CaptionElementContext(0, "標準字幕")).Single();
        var text = (TextBlock)((ElementSource.EngineObject)description.Source).Factory();
        text.Transform.Value = new TranslateTransform { Y = { Value = scene.FrameSize.Value.Height * 0.35f } };
        var element = new Element { Name = { Value = "標準字幕" }, Length = { Value = MediaTime.FromSeconds(1) } };
        element.AddObject(text);
        return Capture(element);
    }

    public static VoiceSubtitleTemplate Capture(Element source)
    {
        ObjectRegenerator.Regenerate(source, out Element copy);
        // A generated caption can itself become a template, including fixed speaker labels.
        foreach (var binding in copy.Objects.OfType<SubtitleController>().SelectMany(x => x.TextBindings.Value))
        {
            var text = copy.EnumerateAllChildren<TextBlock>().FirstOrDefault(x => x.Id.Value == binding.TextId);
            if (text != null && text.Text.Value?.Contains(Placeholder, StringComparison.Ordinal) != true)
                text.Text.Value = binding.Pattern;
        }
        // A caption copies the visual template without replaying its sound or keeping old speech links.
        foreach (var obj in copy.Objects.Where(x => x is Sound or TtsController or SubtitleController).ToArray())
            copy.Objects.Remove(obj);
        GetTextTargets(copy);
        copy.Uri = null;
        return new(string.IsNullOrWhiteSpace(source.Name.Value) ? "字幕テンプレート" : source.Name.Value,
            CoreSerializer.SerializeToJsonString(copy));
    }

    private static TextBlock[] GetTextTargets(Element element)
    {
        var texts = element.EnumerateAllChildren<TextBlock>().ToArray();
        var marked = texts.Where(x => x.Text.Value?.Contains(Placeholder, StringComparison.Ordinal) == true).ToArray();
        if (marked.Length > 0) return marked;
        if (texts.Length == 1) return texts;
        throw new InvalidOperationException("字幕に使うテキストへ {text} を入力してください。");
    }

    public static Element? Load(VoiceSubtitleTemplate? template)
    {
        if (template == null) return null;
        var saved = CoreSerializer.DeserializeFromJsonObject(JsonNode.Parse(template.ElementJson)!.AsObject(), typeof(Element)) as Element
            ?? throw new InvalidOperationException("字幕テンプレートを読み込めませんでした。");
        GetTextTargets(saved);
        return saved;
    }

    public static Element? Create(Scene scene, Element speech, Element? template)
    {
        if (template == null) return null;
        ObjectRegenerator.Regenerate(template, out Element caption);
        var controller = speech.Objects.OfType<TtsController>().Single();
        var targets = GetTextTargets(caption);
        var bindings = targets.Select(target => new SubtitleTextBinding(target.Id.Value,
            target.Text.Value?.Contains(Placeholder, StringComparison.Ordinal) == true ? target.Text.Value : Placeholder)).ToArray();
        foreach (var target in targets)
        {
            target.Text.Expression = null;
            target.Text.Value = bindings.First(x => x.TextId == target.Id.Value).Pattern.Replace(Placeholder, controller.Generation.Value!.Text);
        }
        caption.AddObject(new SubtitleController { SourceId = { Value = controller.Id.Value }, TextBindings = { Value = bindings } });
        caption.Uri = RandomFileNameGenerator.GenerateUri(scene.Uri!, "belm");
        caption.Name.Value = "字幕: " + controller.Generation.Value!.Text.ReplaceLineEndings(" ");
        caption.IsLocked.Value = false;
        caption.IsEnabled.Value = true;
        caption.Start.Value = speech.Start.Value;
        caption.Length.Value = speech.Length.Value;
        return caption;
    }

    // Single speech placement shares the batch planner's contiguous-layer search.
    public static void Place(Scene scene, Element speech, Element? template)
    {
        var caption = Create(scene, speech, template);
        if (caption == null) return;
        Guid group = speech.Objects.OfType<TtsController>().Single().LipSyncGroupId.Value;
        var character = group == Guid.Empty ? null : PsdLipSync.Targets(scene)
            .Where(x => PsdLipSync.GroupOf(x) == group)
            .OrderByDescending(x => x.TimeRange.Value.Intersects(speech.Range))
            .ThenBy(x => Math.Abs((x.TimeRange.Value.Start - speech.Start.Value).ToSeconds())).FirstOrDefault();
        int preferred = character == null ? speech.ZIndex.Value
            : Math.Max(speech.ZIndex.Value, checked(character.FindHierarchicalParent<Element>()!.ZIndex.Value + 1));
        int layer = PsdLipSyncPresets.FreeLayer(scene, speech.Range, preferred, speech, layerCount: 2);
        if (layer != speech.ZIndex.Value) scene.MoveChild(layer, speech.Start.Value, speech.Length.Value, speech);
        caption.ZIndex.Value = checked(layer + 1);
        scene.AddChild(caption, ElementOverlapHandling.ThrowException);
    }

    internal sealed record TextUpdate(SubtitleController Link, TextBlock Target, string? Previous, string Next);
    internal sealed record UpdateSnapshot(string Text, TextUpdate[] Targets);

    private static IEnumerable<(SubtitleController Link, TextBlock Target, SubtitleTextBinding Binding)> LinkedTargets(TtsController controller) =>
        controller.FindHierarchicalParent<Scene>()!.EnumerateAllChildren<SubtitleController>()
            .Where(x => x.IsEnabled.Value && x.SourceId.Value == controller.Id.Value)
            .SelectMany(link => link.FindHierarchicalParent<Element>()!.EnumerateAllChildren<TextBlock>()
                .Select(target => (Target: target, Binding: link.TextBindings.Value.FirstOrDefault(x => x.TextId == target.Id.Value)))
                .Where(x => x.Binding != null)
                .Select(x => (link, x.Target, x.Binding!)));

    public static UpdateSnapshot PrepareUpdate(TtsController controller, string text)
    {
        var scene = controller.FindHierarchicalParent<Scene>()!;
        var updates = LinkedTargets(controller)
            .Select(x => new TextUpdate(x.Link, x.Target, x.Target.Text.Value, x.Binding.Pattern.Replace(Placeholder, text))).ToArray();
        ValidateUpdates(scene, controller, updates);
        return new(text, updates);
    }

    public static void Update(TtsController controller, UpdateSnapshot snapshot)
    {
        var scene = controller.FindHierarchicalParent<Scene>()!;
        // A newly linked target may need an update that was not captured before synthesis.
        // Keep the original values for existing targets so intervening edits are never overwritten.
        if (LinkedTargets(controller).Any(x => x.Target.Text.Value != x.Binding.Pattern.Replace(Placeholder, snapshot.Text)
                && !snapshot.Targets.Any(saved => saved.Link == x.Link && saved.Target == x.Target)))
            throw new InvalidOperationException("生成中に更新する字幕が追加されました。もう一度再生成してください。");
        ValidateUpdates(scene, controller, snapshot.Targets);
        foreach (var update in snapshot.Targets.Where(x => ShouldUpdate(scene, controller, x))) update.Target.Text.Value = update.Next;
    }

    private static bool ShouldUpdate(Scene scene, TtsController controller, TextUpdate update) => update.Previous != update.Next
        && update.Link.IsEnabled.Value
        && update.Link.SourceId.Value == controller.Id.Value
        && update.Target.FindHierarchicalParent<Scene>() == scene
        && update.Link.FindHierarchicalParent<Element>() == update.Target.FindHierarchicalParent<Element>();

    private static void ValidateUpdates(Scene scene, TtsController controller, IReadOnlyList<TextUpdate> updates)
    {
        foreach (var update in updates.Where(x => ShouldUpdate(scene, controller, x)))
        {
            if (scene.IsElementLocked(update.Target.FindHierarchicalParent<Element>()!))
                throw new InvalidOperationException("更新する字幕の要素またはレイヤーがロックされています。");
            if (update.Target.Text.Expression != null)
                throw new InvalidOperationException("更新する字幕の本文に式が設定されています。式を解除するか字幕連携を無効にしてください。");
            if (update.Target.Text.Value != update.Previous)
                throw new InvalidOperationException("生成中に字幕の本文が変わりました。もう一度再生成してください。");
        }
    }
}
