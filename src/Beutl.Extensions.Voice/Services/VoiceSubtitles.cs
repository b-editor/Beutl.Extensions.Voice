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

    // Call inside the same transaction as speech placement, after its final layers are known.
    public static void Place(Scene scene, IReadOnlyList<(Element Speech, VoiceSubtitleTemplate? Template)> items)
    {
        var captions = new List<Element>();
        foreach (var (speech, template) in items)
        {
            if (template == null) continue;
            var saved = CoreSerializer.DeserializeFromJsonObject(JsonNode.Parse(template.ElementJson)!.AsObject(), typeof(Element)) as Element
                ?? throw new InvalidOperationException("字幕テンプレートを読み込めませんでした。");
            ObjectRegenerator.Regenerate(saved, out Element caption);
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
            captions.Add(caption);
        }
        if (captions.Count == 0) return;
        int layer = checked(items.Max(x => x.Speech.ZIndex.Value) + 1);
        while (scene.IsLayerLocked(layer) || captions.Any(caption => scene.Children.Any(x => x.ZIndex.Value == layer && x.Range.Intersects(caption.Range))))
            layer = checked(layer + 1);
        foreach (var caption in captions)
        {
            caption.ZIndex.Value = layer;
            scene.AddChild(caption, ElementOverlapHandling.ThrowException);
        }
    }

    internal sealed record TextUpdate(SubtitleController Link, TextBlock Target, string? Previous, string Next);

    public static TextUpdate[] PrepareUpdate(TtsController controller, string text)
    {
        var scene = controller.FindHierarchicalParent<Scene>()!;
        var updates = scene.EnumerateAllChildren<SubtitleController>()
            .Where(x => x.IsEnabled.Value && x.SourceId.Value == controller.Id.Value)
            .SelectMany(link => link.FindHierarchicalParent<Element>()!.EnumerateAllChildren<TextBlock>()
                .Select(target => (Target: target, Binding: link.TextBindings.Value.FirstOrDefault(x => x.TextId == target.Id.Value)))
                .Where(x => x.Binding != null)
                .Select(x => new TextUpdate(link, x.Target, x.Target.Text.Value, x.Binding!.Pattern.Replace(Placeholder, text))))
            .Where(x => x.Previous != x.Next).ToArray();
        ValidateUpdates(scene, controller, updates);
        return updates;
    }

    public static void Update(TtsController controller, IReadOnlyList<TextUpdate> updates)
    {
        var scene = controller.FindHierarchicalParent<Scene>()!;
        ValidateUpdates(scene, controller, updates);
        foreach (var update in updates.Where(x => IsLinked(scene, controller, x))) update.Target.Text.Value = update.Next;
    }

    private static bool IsLinked(Scene scene, TtsController controller, TextUpdate update) => update.Link.IsEnabled.Value
        && update.Link.SourceId.Value == controller.Id.Value
        && update.Target.FindHierarchicalParent<Scene>() == scene
        && update.Link.FindHierarchicalParent<Element>() == update.Target.FindHierarchicalParent<Element>();

    private static void ValidateUpdates(Scene scene, TtsController controller, IReadOnlyList<TextUpdate> updates)
    {
        foreach (var update in updates.Where(x => IsLinked(scene, controller, x)))
        {
            if (scene.IsElementLocked(update.Target.FindHierarchicalParent<Element>()!))
                throw new InvalidOperationException("更新する字幕の要素またはレイヤーがロックされています。");
            if (update.Target.Text.Value != update.Previous)
                throw new InvalidOperationException("生成中に字幕の本文が変わりました。もう一度再生成してください。");
        }
    }
}
