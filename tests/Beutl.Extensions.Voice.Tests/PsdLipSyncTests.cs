using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Beutl.Animation;
using Beutl.Audio;
using Beutl.Editor.Observers;
using Beutl.Editor.Services;
using Beutl.Engine.Expressions;
using Beutl.Extensions.PsdTachie.Graphics;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.Graphics;
using Beutl.Graphics.Backend;
using Beutl.Graphics.Rendering;
using Beutl.Media;
using Beutl.Media.Source;
using Beutl.ProjectSystem;
using Beutl.Serialization;

namespace Beutl.Extensions.Voice.Tests;

public partial class PsdLipSyncTests
{
    private string _directory = null!;
    private string? _previousHome;
    private static MediaTime At(double seconds) => MediaTime.FromSeconds(seconds);

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "voice-lipsync-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _previousHome = Environment.GetEnvironmentVariable("BEUTL_HOME");
        Environment.SetEnvironmentVariable("BEUTL_HOME", _directory);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("BEUTL_HOME", _previousHome);
        Directory.Delete(_directory, true);
    }

    private TestEditor Editor()
    {
        var editor = new TestEditor(Path.Combine(_directory, "scene.bescene"));
        editor.Scene.FrameSize.Value = new Beutl.Media.PixelSize(320, 240);
        return editor;
    }

    private (Element Element, SourceSound Sound, TtsController Controller) Speech(Scene scene, double start, int layer = 1)
    {
        var element = new Element { Start = { Value = At(start) }, Length = { Value = At(2) }, ZIndex = { Value = layer } };
        var sound = new SourceSound { Source = { Value = Wave() } };
        var controller = new TtsController { Text = { Value = "口パクのテスト" } };
        element.AddObject(sound);
        element.AddObject(controller);
        scene.AddChild(element, ElementOverlapHandling.Allow);
        return (element, sound, controller);
    }

    private static PsdTachieDrawable Character(Scene scene, int layer = 0)
    {
        // Fixture from b-editor/Beutl.Extensions.PsdTachie at 8648bf6; see TestData/PsdTachie.LICENSE.
        var target = new PsdTachieDrawable
        {
            Source = { Value = new FileInfo(Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "tachie_rle.psd")) },
            MouthClosed = { Value = "口/*閉じ" }, MouthHalfOpen = { Value = "口/*半開き" }, MouthOpen = { Value = "口/*開き" },
            EyeOpen = { Value = "目/*開き" }, BlinkInterval = { Value = 20f },
            Layers = { Value = "-非表示" }
        };
        var element = new Element { Name = { Value = "会話の立ち絵" }, Length = { Value = At(10) }, ZIndex = { Value = layer } };
        element.AddObject(target);
        scene.AddChild(element, ElementOverlapHandling.Allow);
        return target;
    }

    private SoundSource Wave(bool silent = false)
    {
        string path = Path.Combine(_directory, Guid.NewGuid() + ".wav");
        const int rate = 24000, count = rate * 2;
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
            for (int i = 0; i < count; i++)
                writer.Write((short)(!silent && i < rate ? 16000 * Math.Sin(2 * Math.PI * 220 * i / rate) : 0));
        }
        return SoundSource.Open(path);
    }

    [AvaloniaTest]
    public void Render_follows_lines_gaps_edits_and_audible_layers_without_recreating_the_renderer()
    {
        using var editor = Editor();
        var target = Character(editor.Scene);
        // One continuous character above successive speech clips on the same audio layer.
        editor.Scene.MoveChild(0, At(0.25), At(8), target.FindHierarchicalParent<Element>()!);
        var first = Speech(editor.Scene, 1);
        var second = Speech(editor.Scene, 3.5);
        PsdLipSync.Connect(first.Controller, target);
        PsdLipSync.Connect(second.Controller, target);
        using var render = new RenderProbe(editor.Scene);
        render.Mouth(0.5, false, "lipsync-before-lines");
        render.Mouth(1.5, true, "lipsync-speaking");
        render.Mouth(2.5, false, "lipsync-silent");
        render.Mouth(3, false); // Clip end is excluded.
        render.Mouth(3.25, false, "lipsync-between-lines");
        render.Mouth(4, true, "lipsync-second-line");
        render.Mouth(5.5, false, "lipsync-after-lines");
        editor.Scene.MoveChild(2, At(2), At(2), second.Element);
        second.Sound.Source.Value = Wave(silent: true);
        render.Mouth(2.25, false); // The newest line wins when clips overlap.
        second.Sound.Source.Value = Wave();
        render.Mouth(2.25, true); // A take replacement is visible at the same frame.
        second.Sound.OffsetPosition.Value = At(1);
        render.Mouth(2.25, false);
        second.Sound.OffsetPosition.Value = At(0);
        second.Sound.Speed.Value = 200;
        render.Mouth(2.75, false);
        second.Sound.Speed.Value = 100;
        render.Mouth(2.75, true);
        var layer = new TimelineLayer { ZIndex = { Value = 2 }, IsAudioMuted = { Value = true } };
        editor.Scene.Layers.Add(layer);
        render.Mouth(2.75, false);
        layer.IsAudioMuted.Value = false;
        render.Mouth(2.75, true);
        editor.Scene.Layers.Add(new TimelineLayer { ZIndex = { Value = 0 }, IsSolo = { Value = true } });
        render.Mouth(2.75, false);
        layer.IsSolo.Value = true;
        render.Mouth(2.75, true);
        second.Sound.IsEnabled.Value = false;
        render.Mouth(2.75, false); // The older clip is already in its silent second.
        second.Sound.IsEnabled.Value = true;
        editor.Scene.RemoveChild(second.Element);
        render.Mouth(2.75, false);
        Assert.That(target.MouthOpen.Value, Is.EqualTo("口/*開き"));
        Assert.That(target.EyeOpen.Value, Is.EqualTo("目/*開き"));
        Assert.That(target.Layers.Value, Is.EqualTo("-非表示"));
    }

    [AvaloniaTest]
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void Duplicating_dialogue_rebinds_only_the_copied_pair_and_survives_split_and_reload(bool splitPortraitFirst, bool clipboard)
    {
        using var editor = Editor();
        var scene = editor.Scene;
        var target = Character(scene);
        var first = Speech(scene, 1);
        var second = Speech(scene, 3.5);
        PsdLipSync.Connect(first.Controller, target);
        PsdLipSync.Connect(second.Controller, target);
        var group = first.Controller.LipSyncGroupId.Value;
        var split = new ElementStructureService(editor.History);
        if (splitPortraitFirst) split.Split(scene, [target.FindHierarchicalParent<Element>()!], At(2.5));
        var originals = scene.Children.ToArray();
        using var observer = new CoreObjectOperationObserver(null, scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        var sources = clipboard ? originals.Select(x => (Element)CoreSerializer.DeserializeFromJsonObject(
            CoreSerializer.SerializeToJsonObject(x), typeof(Element))).ToArray() : originals;
        Assert.That(new ElementDuplicateService(editor.History).DuplicateAtPosition(scene, sources, At(0), 3), Is.True);
        var copies = scene.Children.Except(originals).ToArray();
        var copiedTargets = copies.SelectMany(x => x.Objects).OfType<PsdTachieDrawable>().ToArray();
        var copiedControllers = copies.SelectMany(x => x.Objects).OfType<TtsController>().ToArray();
        var copyGroup = copiedControllers[0].LipSyncGroupId.Value;
        Assert.That(copyGroup, Is.Not.EqualTo(group).And.Not.EqualTo(Guid.Empty));
        Assert.That(copiedControllers.All(x => x.LipSyncGroupId.Value == copyGroup), Is.True);
        Assert.That(copiedTargets.Select(PsdLipSync.GroupOf).Distinct(), Is.EqualTo(new[] { copyGroup }));
        Assert.That(first.Controller.LipSyncGroupId.Value, Is.EqualTo(group));
        Assert.That(PsdLipSync.GroupOf(target), Is.EqualTo(group));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(scene.Children, Is.EquivalentTo(originals));
        Assert.That(editor.History.Redo(), Is.True);
        var copiedFirst = copies.Single(x => x.Objects.OfType<TtsController>().Any() && x.Start.Value == At(1));
        var copiedSound = copiedFirst.Objects.OfType<SourceSound>().Single();
        copiedSound.Source.Value = Wave(silent: true);
        copiedSound.OffsetPosition.Value = At(0.3);
        Assert.That(target.LipSyncAudio.Evaluate(new EvaluationContext(At(1.25)))!.Uri, Is.EqualTo(first.Sound.Source.Value!.Uri));
        Assert.That(copiedTargets[0].LipSyncAudio.Evaluate(new EvaluationContext(At(1.25)))!.Uri, Is.EqualTo(copiedSound.Source.Value.Uri));
        Assert.That(copiedTargets[0].LipSyncOffset.Evaluate(new EvaluationContext(At(1.25))),
            Is.Not.EqualTo(target.LipSyncOffset.Evaluate(new EvaluationContext(At(1.25)))));
        editor.History.Commit();
        split.Split(scene, [copiedFirst], At(1.75));
        split.Split(scene, [copiedTargets[0].FindHierarchicalParent<Element>()!], At(1.5));
        Assert.That(PsdLipSync.Targets(scene).Where(x => x.ZIndex.Value >= 3).Select(PsdLipSync.GroupOf).Distinct(), Is.EqualTo(new[] { copyGroup }));
        Assert.That(scene.EnumerateAllChildren<TtsController>().Where(x => x.ZIndex.Value >= 3).Select(x => x.LipSyncGroupId.Value).Distinct(), Is.EqualTo(new[] { copyGroup }));
        using var reopened = Editor();
        foreach (var element in scene.Children)
        {
            var json = CoreSerializer.SerializeToJsonObject(element);
            Assert.That(json.ToJsonString(), Does.Not.Contain("LipSyncCopyTargets"));
            reopened.Scene.AddChild((Element)CoreSerializer.DeserializeFromJsonObject(json, typeof(Element)), ElementOverlapHandling.Allow);
        }
        Assert.That(PsdLipSync.Targets(reopened.Scene).Select(PsdLipSync.GroupOf).Distinct(), Is.EquivalentTo(new[] { group, copyGroup }));
        // Copy only a later portrait segment, without the original segment or the group's first voice.
        var laterPortrait = reopened.Scene.Children.Single(x => x.ZIndex.Value == 3 && x.Start.Value == At(1.5));
        var laterVoice = reopened.Scene.Children.Single(x => x.ZIndex.Value == 4 && x.Start.Value == At(1.75));
        ObjectRegenerator.Regenerate(new[] { laterVoice, laterPortrait }, out var nextCopy);
        var nextGroup = nextCopy[0].Objects.OfType<TtsController>().Single().LipSyncGroupId.Value;
        Assert.That(nextGroup, Is.Not.EqualTo(copyGroup).And.Not.EqualTo(group));
        Assert.That(PsdLipSync.GroupOf(nextCopy[1].Objects.OfType<PsdTachieDrawable>().Single()), Is.EqualTo(nextGroup));

        // A standalone voice copy still drives its existing portrait.
        ObjectRegenerator.Regenerate(first.Element, out var voiceOnly);
        Assert.That(voiceOnly.Objects.OfType<TtsController>().Single().LipSyncGroupId.Value, Is.EqualTo(group));
    }

    [AvaloniaTest]
    public void Splitting_an_element_with_both_voice_and_portrait_preserves_the_group()
    {
        using var editor = Editor();
        var target = Character(editor.Scene);
        var portrait = target.FindHierarchicalParent<Element>()!;
        var speech = Speech(editor.Scene, 1);
        portrait.RemoveObject(target);
        speech.Element.AddObject(target);
        editor.Scene.RemoveChild(portrait);
        PsdLipSync.Connect(speech.Controller, target);
        var group = speech.Controller.LipSyncGroupId.Value;
        new ElementStructureService(editor.History).Split(editor.Scene, [speech.Element], At(1.75));
        Assert.That(editor.Scene.EnumerateAllChildren<TtsController>().Select(x => x.LipSyncGroupId.Value).Distinct(), Is.EqualTo(new[] { group }));
        Assert.That(PsdLipSync.Targets(editor.Scene).Select(PsdLipSync.GroupOf).Distinct(), Is.EqualTo(new[] { group }));
    }

    [AvaloniaTest]
    public void Splitting_both_elements_and_reopening_preserves_the_audio_clock()
    {
        using var editor = Editor();
        var target = Character(editor.Scene);
        var speech = Speech(editor.Scene, 1);
        PsdLipSync.Connect(speech.Controller, target);
        var split = new ElementStructureService(editor.History);
        split.Split(editor.Scene, [speech.Element], At(1.75));
        split.Split(editor.Scene, [target.FindHierarchicalParent<Element>()!], At(1.5));
        Assert.That(editor.Scene.EnumerateAllChildren<TtsController>().Select(x => x.LipSyncGroupId.Value).Distinct(),
            Is.EqualTo(new[] { speech.Controller.LipSyncGroupId.Value }));
        using (var render = new RenderProbe(editor.Scene))
        {
            render.Mouth(1.25, true);
            render.Mouth(1.8, true);
            render.Mouth(2.25, false);
        }
        using var reopened = Editor();
        foreach (var element in editor.Scene.Children)
            reopened.Scene.AddChild((Element)CoreSerializer.DeserializeFromJsonObject(
                CoreSerializer.SerializeToJsonObject(element), typeof(Element)), ElementOverlapHandling.Allow);
        using var reopenedRender = new RenderProbe(reopened.Scene);
        reopenedRender.Mouth(1.8, true);
        reopenedRender.Mouth(2.25, false);
        var backCharacter = reopened.Scene.Children.Single(x => x.Start.Value == At(1.5));
        reopened.Scene.MoveChild(0, At(1.6), backCharacter.Length.Value, backCharacter);
        reopenedRender.Mouth(1.8, true);
        reopenedRender.Mouth(2.25, false);
    }

    [AvaloniaTest]
    public void Speed_animation_uses_the_local_or_global_audio_clock_and_invalidates_after_edits()
    {
        using var editor = Editor();
        var target = Character(editor.Scene);
        var speech = Speech(editor.Scene, 1);
        PsdLipSync.Connect(speech.Controller, target);
        var animation = new KeyFrameAnimation<float>();
        animation.KeyFrames.Add(new KeyFrame<float> { KeyTime = { Value = At(0) }, Value = { Value = 100 } });
        animation.KeyFrames.Add(new KeyFrame<float> { KeyTime = { Value = At(2) }, Value = { Value = 200 } });
        speech.Sound.Speed.SetKeyFrameAnimation(animation);
        AssertSourceTime(2, 1.25);
        animation.UseGlobalClock.Value = true;
        AssertSourceTime(2, 1.75);
        ((KeyFrame<float>)animation.KeyFrames[1]).Value.Value = 100;
        AssertSourceTime(2, 1);

        void AssertSourceTime(double time, double expected)
        {
            var offset = target.LipSyncOffset.Evaluate(new EvaluationContext(At(time)));
            Assert.That((At(time) - offset).ToSeconds(), Is.EqualTo(expected).Within(0.001));
        }
    }

    [AvaloniaTest]
    public void Linking_switching_and_unlinking_are_undoable_and_restore_existing_inputs()
    {
        using var editor = Editor();
        var target = Character(editor.Scene);
        var other = Character(editor.Scene, 3);
        var first = Speech(editor.Scene, 1);
        var second = Speech(editor.Scene, 3.5);
        target.LipSyncAudio.Value = Wave();
        target.LipSyncOffset.Value = At(0.3);
        target.LipSyncOffset.Expression = Expression.CreateReference<MediaTime>(first.Sound.Id.Value, nameof(SourceSound.OffsetPosition));
        string originalExpression = target.LipSyncOffset.SerializeExpression()!.ToJsonString();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        using var model = TtsControllerTests.CreateModel(editor, first.Controller);
        model.SelectedLipSyncTarget.Value = model.LipSyncTargets.Value.Single(x => x.Object == target);
        model.ConnectLipSync();
        Assert.That(model.HasLipSync.Value, Is.True, model.Message.Value);
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(model.HasLipSync.Value, Is.False);
        Assert.That(target.LipSyncOffset.SerializeExpression()!.ToJsonString(), Is.EqualTo(originalExpression));
        Assert.That(editor.History.Redo(), Is.True);
        PsdLipSync.Connect(second.Controller, target);
        editor.History.Commit();
        model.SelectedLipSyncTarget.Value = model.LipSyncTargets.Value.Single(x => x.Object == other);
        model.ConnectLipSync();
        Assert.That(PsdLipSync.GroupOf(target), Is.EqualTo(second.Controller.LipSyncGroupId.Value));
        model.DisconnectLipSync();
        Assert.That(other.LipSyncAudio.Expression, Is.Null);
        using var secondModel = TtsControllerTests.CreateModel(editor, second.Controller);
        var element = target.FindHierarchicalParent<Element>()!;
        element.IsLocked.Value = true;
        secondModel.DisconnectLipSync();
        Assert.That(secondModel.Message.Value, Does.Contain("ロック"));
        Assert.That(second.Controller.LipSyncGroupId.Value, Is.Not.EqualTo(Guid.Empty));
        element.IsLocked.Value = false;
        secondModel.SelectedLipSyncTarget.Value = secondModel.LipSyncTargets.Value.Single(x => x.Object == other);
        secondModel.ConnectLipSync(); // Moving the last voice also restores the previous character.
        Assert.That(target.LipSyncAudio.Expression, Is.Null);
        Assert.That(target.LipSyncAudio.Value, Is.Not.Null);
        Assert.That(target.LipSyncOffset.Value, Is.EqualTo(At(0.3)));
        Assert.That(target.LipSyncOffset.SerializeExpression()!.ToJsonString(), Is.EqualTo(originalExpression));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(PsdLipSync.GroupOf(target), Is.EqualTo(second.Controller.LipSyncGroupId.Value));
        Assert.That(other.LipSyncAudio.Expression, Is.Null);
        Assert.That(secondModel.SelectedLipSyncTarget.Value?.Object, Is.SameAs(target));
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(secondModel.SelectedLipSyncTarget.Value?.Object, Is.SameAs(other));
        var manualAudio = Expression.CreateReference<SoundSource?>(first.Sound.Id.Value, nameof(SourceSound.Source));
        other.LipSyncAudio.Expression = manualAudio;
        secondModel.DisconnectLipSync();
        Assert.That(other.LipSyncAudio.Expression, Is.SameAs(manualAudio), "Keep an input the user changed while linked.");
        Assert.That(other.LipSyncOffset.Expression, Is.Null, "Remove the remaining part of the voice link.");
    }

    [AvaloniaTest]
    public async Task Regeneration_and_its_undo_replace_the_linked_audio()
    {
        using var server = new HttpVoiceEngineTests.EngineServer();
        using var editor = Editor();
        var target = Character(editor.Scene);
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        using var tab = new TtsTabViewModel(new(), editor);
        Assert.That(await tab.SaveConnection("Test", server.Url), Is.True, tab.ConnectionMessage.Value);
        tab.SelectedVoice.Value = tab.Voice.Value.Single();
        tab.Text.Value = "読み上げ";
        await tab.Generate();
        var controller = editor.Scene.EnumerateAllChildren<TtsController>().Single();
        using var model = TtsControllerTests.CreateModel(editor, controller);
        model.SelectedLipSyncTarget.Value = model.LipSyncTargets.Value.Single();
        model.ConnectLipSync();
        var time = new EvaluationContext(controller.TimeRange.Value.Start);
        string before = target.LipSyncAudio.Evaluate(time)!.Uri.LocalPath;
        await model.Regenerate();
        Assert.That(model.Message.Value, Is.EqualTo("音声を再生成しました。"));
        string after = target.LipSyncAudio.Evaluate(time)!.Uri.LocalPath;
        Assert.That(after, Is.Not.EqualTo(before));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(target.LipSyncAudio.Evaluate(time)!.Uri.LocalPath, Is.EqualTo(before));
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(target.LipSyncAudio.Evaluate(time)!.Uri.LocalPath, Is.EqualTo(after));
    }

    [AvaloniaTest]
    [TestCase(320, false)]
    [TestCase(460, true)]
    public async Task Controller_offers_optional_targets_without_clipping(int width, bool light)
    {
        using var editor = Editor();
        var speech = Speech(editor.Scene, 1);
        using var model = TtsControllerTests.CreateModel(editor, speech.Controller);
        Assert.That(typeof(TtsController).Assembly.GetReferencedAssemblies().Select(x => x.Name),
            Does.Not.Contain("Beutl.Extensions.PsdTachie"));
        var view = new TtsControllerView { DataContext = model };
        var scroll = new ScrollViewer { Content = view };
        var window = new Window { Width = width, Height = 600, Content = scroll,
            RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        try
        {
            window.Show();
            var expander = view.FindControl<Expander>("LipSyncExpander")!;
            expander.IsExpanded = true;
            await Settle();
            var connect = view.FindControl<Button>("ConnectLipSyncButton")!;
            Assert.That(connect.IsEffectivelyEnabled, Is.False);
            Character(editor.Scene);
            model.RefreshLipSyncTargets();
            view.FindControl<ComboBox>("LipSyncTargetSelector")!.SelectedIndex = 0;
            connect.Command!.Execute(null);
            await Settle();
            expander.BringIntoView();
            await Settle();
            Assert.That(model.HasLipSync.Value, Is.True, model.Message.Value);
            Assert.That(scroll.Extent.Width, Is.LessThanOrEqualTo(scroll.Viewport.Width));
            var selector = view.FindControl<ComboBox>("LipSyncTargetSelector")!;
            Assert.That(selector.Bounds.Width, Is.GreaterThan(100));
            Assert.That(connect.TranslatePoint(new Avalonia.Point(0, connect.Bounds.Height), window)!.Value.Y,
                Is.LessThanOrEqualTo(window.ClientSize.Height));
            if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is { } directory)
            {
                Directory.CreateDirectory(directory);
                using var frame = window.CaptureRenderedFrame()!;
                frame.Save(Path.Combine(directory, $"lipsync-controller-{width}.png"), new PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); }
    }

    private static async Task Settle()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(150);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
    }

    private sealed class RenderProbe : IDisposable
    {
        private readonly SceneRenderer _renderer;

        public RenderProbe(Scene scene)
        {
            if (!RenderThread.Dispatcher.Invoke(() => GraphicsContextFactory.GetOrCreateShared() is not null))
                Assert.Ignore("No graphics context is available on this host.");
            _renderer = RenderThread.Dispatcher.Invoke(() => new SceneRenderer(scene, RenderIntent.Delivery));
        }

        public void Mouth(double seconds, bool open, string? capture = null, int offsetX = 0) => RenderThread.Dispatcher.Invoke(() =>
        {
            _renderer.Render(_renderer.Compositor.EvaluateGraphics(At(seconds)));
            using var snapshot = _renderer.Snapshot();
            using var frame = snapshot.Convert(BitmapColorType.Bgra8888, BitmapAlphaType.Premul, BitmapColorSpace.Srgb);
            // The 64x48 PSD is centered in a 320x240 frame. Open mouth fills row 12; closed is row 14 only.
            var row = frame.GetRow(96 + (open ? 13 : 12));
            int pixel = (128 + 43 + offsetX) * 4;
            var expected = open ? (R: 190, G: 0, B: 0) : (R: 200, G: 200, B: 200);
            Assert.That(Math.Abs(row[pixel + 2] - expected.R) < 12 && Math.Abs(row[pixel + 1] - expected.G) < 12
                && Math.Abs(row[pixel] - expected.B) < 12, Is.True, $"Mouth at {seconds}s should be {(open ? "open" : "closed")}");
            if (capture != null && Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is { } directory)
            {
                Directory.CreateDirectory(directory);
                frame.Save(Path.Combine(directory, capture + ".png"), EncodedImageFormat.Png);
            }
        });

        public void Dispose() => RenderThread.Dispatcher.Invoke(_renderer.Dispose);
    }
}
