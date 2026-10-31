using System.Text.Json.Nodes;
using Beutl.Editor.Services;
using Beutl.Engine;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Media;
using Beutl.ProjectSystem;
using Beutl.Serialization;
using Beutl.Utilities;

namespace Beutl.Extensions.Voice.Services;

internal static class PsdLipSyncPresets
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string? SourcePath(EngineObject target) => target.Properties
        .OfType<IExpressionProperty<FileInfo?>>()
        .FirstOrDefault(x => x.Name == "Source")?.Value?.FullName;

    public static VoiceLipSyncPreset Capture(EngineObject target)
    {
        string path = SourcePath(target)
            ?? throw new InvalidOperationException("立ち絵のPSDファイルを指定してください。");
        ObjectRegenerator.Regenerate(target, target.GetType(), out var copy);
        if (!PsdLipSync.TryGetProperties((EngineObject)copy, out var audio, out var offset))
            throw new InvalidOperationException("立ち絵の設定を保存できませんでした。");
        // A preset carries the drawable settings, not a link to speech in the source project.
        if (audio.Expression is VoiceLipSyncAudioExpression audioExpression) audio.Expression = audioExpression.Replaced;
        if (offset.Expression is VoiceLipSyncOffsetExpression offsetExpression) offset.Expression = offsetExpression.Replaced;
        return new(path, CoreSerializer.SerializeToJsonString(copy));
    }

    // Call inside the same history transaction as audio placement, after adding all speech elements.
    public static void Connect(Scene scene, IEnumerable<(TtsController Controller, VoiceLipSyncPreset Preset)> bindings)
    {
        foreach (var group in bindings.GroupBy(x => x.Preset.PsdPath, PathComparer))
        {
            var targets = FindTargets(scene, group.Key);
            bool created = targets.Length == 0;
            if (created)
            {
                var range = TimeRange.FromRange(group.Min(x => x.Controller.TimeRange.Value.Start),
                    group.Max(x => x.Controller.TimeRange.Value.End));
                targets = [CreateCharacter(scene, group.First().Preset, range, FreeLayer(scene, range, 0))];
            }
            // Select per speech interval before extending any character's range.
            var assignments = group.GroupBy(binding => SelectTarget(targets, binding.Controller.TimeRange.Value)).ToArray();
            foreach (var assignment in assignments)
            {
                foreach (var binding in assignment) PsdLipSync.Connect(binding.Controller, assignment.Key, created);
                var range = TimeRange.FromRange(assignment.Min(x => x.Controller.TimeRange.Value.Start),
                    assignment.Max(x => x.Controller.TimeRange.Value.End));
                Extend(scene, assignment.Key, range);
            }
        }
    }

    // Plan paired layers before adding speech, so temporary overlaps never change its timing.
    // The caller includes this entire operation in the batch's history transaction.
    public static void PlaceScript(Scene scene, IReadOnlyList<(Element Element, VoiceLipSyncPreset? Preset)> speech, int firstLayer)
    {
        var scriptRange = TimeRange.FromRange(speech.Min(x => x.Element.Range.Start), speech.Max(x => x.Element.Range.End));
        var planned = speech.Where(x => x.Preset == null).Select(x => x.Element).ToList();
        var links = new List<(TtsController Controller, EngineObject Target, bool Created)>();
        int nextLayer = firstLayer;
        foreach (var group in speech.Where(x => x.Preset != null).GroupBy(x => x.Preset!.PsdPath, PathComparer))
        {
            var targets = FindTargets(scene, group.Key);
            bool created = targets.Length == 0;
            if (created)
                targets = [CreateCharacter(scene, group.First().Preset!, scriptRange, FreePair(scene, scriptRange, nextLayer, planned))];
            var assignments = group.GroupBy(x => SelectTarget(targets, x.Element.Range)).ToArray();
            foreach (var assignment in assignments)
            {
                var target = assignment.Key;
                PsdLipSync.ValidateTarget(scene, target);
                Extend(scene, target, scriptRange, planned);
                var range = TimeRange.FromRange(assignment.Min(x => x.Element.Range.Start), assignment.Max(x => x.Element.Range.End));
                int layer = FreeLayer(scene, range, checked(target.FindHierarchicalParent<Element>()!.ZIndex.Value + 1), planned: planned);
                foreach (var item in assignment)
                {
                    item.Element.ZIndex.Value = layer;
                    planned.Add(item.Element);
                    links.Add((item.Element.Objects.OfType<TtsController>().Single(), target, created));
                }
                nextLayer = Math.Max(nextLayer, checked(layer + 1));
            }
        }
        foreach (var item in speech) scene.AddChild(item.Element, ElementOverlapHandling.ThrowException);
        foreach (var link in links) PsdLipSync.Connect(link.Controller, link.Target, link.Created);
    }

    private static EngineObject[] FindTargets(Scene scene, string path) =>
        PsdLipSync.Targets(scene).Where(x => PathComparer.Equals(SourcePath(x), path)).ToArray();

    private static EngineObject SelectTarget(EngineObject[] targets, TimeRange range) => targets
        .OrderByDescending(x => x.TimeRange.Value.Intersects(range))
        .ThenBy(x => Math.Abs((x.TimeRange.Value.Start - range.Start).ToSeconds())).First();

    private static EngineObject CreateCharacter(Scene scene, VoiceLipSyncPreset preset, TimeRange range, int layer)
    {
        var json = JsonNode.Parse(preset.ObjectJson) as JsonObject
            ?? throw new InvalidOperationException("プリセットの立ち絵を読み込めませんでした。");
        var saved = CoreSerializer.DeserializeFromJsonObject(json, typeof(EngineObject)) as EngineObject;
        if (saved == null || !PsdLipSync.TryGetProperties(saved, out _, out _))
            throw new InvalidOperationException("プリセットの立ち絵を使うにはPsdTachie拡張をインストールしてください。");
        ObjectRegenerator.Regenerate(saved, saved.GetType(), out var copy);
        var target = (EngineObject)copy;
        var element = new Element
        {
            Name = { Value = Path.GetFileNameWithoutExtension(preset.PsdPath) },
            Start = { Value = range.Start }, Length = { Value = range.Duration },
            ZIndex = { Value = layer },
            Uri = RandomFileNameGenerator.GenerateUri(scene.Uri!, "belm"),
            AccentColor = { Value = ElementAccentColorGenerator.GenerateColor(target.GetType().FullName!) }
        };
        element.AddObject(target);
        scene.AddChild(element, ElementOverlapHandling.ThrowException);
        return target;
    }

    public static void Extend(Scene scene, EngineObject target, TimeRange range, IReadOnlyCollection<Element>? planned = null)
    {
        Guid group = PsdLipSync.GroupOf(target);
        // Split characters keep their interior boundaries; only extend the outer ends of the group.
        var elements = PsdLipSync.Targets(scene).Where(x => PsdLipSync.GroupOf(x) == group
                && PsdLipSync.TryGetProperties(x, out var audio, out _)
                && audio.Expression is VoiceLipSyncAudioExpression { ExtendToSpeech: true })
            .Select(x => x.FindHierarchicalParent<Element>()!).Distinct().OrderBy(x => x.Start.Value).ToArray();
        if (elements.Length == 0) return;
        ExtendElement(elements[0], elements[0].Start.Value < range.Start ? elements[0].Start.Value : range.Start, elements[0].Range.End);
        var last = elements.MaxBy(x => x.Range.End)!;
        ExtendElement(last, last.Start.Value, last.Range.End > range.End ? last.Range.End : range.End);

        void ExtendElement(Element element, MediaTime start, MediaTime end)
        {
            if (element.Start.Value == start && element.Range.End == end) return;
            if (scene.IsElementLocked(element))
                throw new InvalidOperationException("表示区間を延長する立ち絵の要素またはレイヤーがロックされています。");
            var expanded = TimeRange.FromRange(start, end);
            scene.MoveChild(FreeLayer(scene, expanded, element.ZIndex.Value, element, planned), start, expanded.Duration, element);
        }
    }

    private static bool IsFree(Scene scene, TimeRange range, int layer, Element? except, IReadOnlyCollection<Element>? planned) =>
        !scene.IsLayerLocked(layer) && !scene.Children.Concat(planned ?? []).Any(x => x != except
            && x.ZIndex.Value == layer && x.Range.Intersects(range));

    private static int FreeLayer(Scene scene, TimeRange range, int preferred, Element? except = null,
        IReadOnlyCollection<Element>? planned = null)
    {
        while (!IsFree(scene, range, preferred, except, planned)) preferred = checked(preferred + 1);
        return preferred;
    }

    private static int FreePair(Scene scene, TimeRange range, int preferred, IReadOnlyCollection<Element> planned)
    {
        while (!IsFree(scene, range, preferred, null, planned) || !IsFree(scene, range, checked(preferred + 1), null, planned))
            preferred = checked(preferred + 1);
        return preferred;
    }
}
