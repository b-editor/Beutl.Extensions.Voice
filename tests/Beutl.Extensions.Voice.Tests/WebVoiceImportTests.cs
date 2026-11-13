using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Beutl.Audio;
using Beutl.Audio.Effects;
using Beutl.Editor.Observers;
using Beutl.Extensibility;
using Beutl.Extensions.PsdTachie.Graphics;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.ProjectSystem;
using FluentAvalonia.UI.Controls;
using TextBlock = Beutl.Graphics.Shapes.TextBlock;

namespace Beutl.Extensions.Voice.Tests;

// Speech downloaded from a Yukkuri voice site in the browser tab is placed as the speech tab's Add button places
// generated speech, with the character and caption remembered for its voice.
public class WebVoiceImportTests
{
    private const string Line = "ゆっくりしていってね！";
    private string _directory = null!;
    private string? _previousHome;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "voice-web-" + Guid.NewGuid());
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

    private static string StorePath => WebVoiceAssignmentStore.GlobalPath;

    private TestEditor Editor()
    {
        var editor = new TestEditor(Path.Combine(_directory, "scene.bescene"));
        editor.Scene.FrameSize.Value = new Beutl.Media.PixelSize(320, 240);
        return editor;
    }

    private static BrowserDownloadOffer Offer(WebVoiceSite site, string name = "yvm_20261110120000_ゆっくりし.mp3")
    {
        var page = new Uri($"https://{site.Hosts[0]}/maker");
        return new BrowserDownloadOffer(page, page, name, isGenerated: true);
    }

    private static string PageData(string text, string voice, string voiceName) =>
        JsonSerializer.Serialize(new { text, voice, voiceName });

    // The file the browser saved: a second of tone, as WAV so the test host can decode it.
    private string Download()
    {
        string directory = Path.Combine(_directory, "resources", "downloads");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid() + ".wav");
        const int rate = 24000, count = rate;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
        writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
        for (int i = 0; i < count; i++) writer.Write((short)(16000 * Math.Sin(2 * Math.PI * 220 * i / rate)));
        return path;
    }

    private static PsdTachieDrawable Character(Scene scene)
    {
        // Fixture from b-editor/Beutl.Extensions.PsdTachie at 8648bf6; see TestData/PsdTachie.LICENSE.
        var target = new PsdTachieDrawable
        {
            Source = { Value = new FileInfo(Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "tachie_rle.psd")) },
            MouthClosed = { Value = "口/*閉じ" }, MouthHalfOpen = { Value = "口/*半開き" }, MouthOpen = { Value = "口/*開き" },
            Layers = { Value = "-非表示" }
        };
        var element = new Element { Name = { Value = "霊夢" }, Length = { Value = MediaTime.FromSeconds(10) }, ZIndex = { Value = 0 } };
        element.AddObject(target);
        scene.AddChild(element, ElementOverlapHandling.Allow);
        return target;
    }

    private static Task<WebVoiceAssignment?> NeverAsk(WebVoiceAssignmentViewModel model, CancellationToken token) =>
        throw new AssertionException($"{model.VoiceLabel} already has an assignment.");

    private static Element Caption(Scene scene) =>
        scene.EnumerateAllChildren<SubtitleController>().Single().FindHierarchicalParent<Element>()!;

    [Test]
    public void Sites_take_over_only_the_audio_their_own_pages_offer()
    {
        var maker = WebVoiceSites.YukkuriVoiceMaker;
        var yukumo = WebVoiceSites.Yukumo;
        var claim = WebVoiceImport.Claim(Offer(maker));
        Assert.Multiple(() =>
        {
            Assert.That(claim?.PageScript, Is.EqualTo(maker.PageScript));
            Assert.That(claim?.Description, Does.Contain("ゆっくりボイスメーカー"));
            Assert.That(WebVoiceSites.Find(Offer(yukumo, "yukumo_0001.mp3")), Is.SameAs(yukumo));
            Assert.That(WebVoiceSites.Find(new BrowserDownloadOffer(new Uri("https://yukumo.net/pro"),
                new Uri("https://yukumo.net/pro"), "yukumo_0002.mp3", true)), Is.SameAs(yukumo));
            Assert.That(WebVoiceImport.Claim(Offer(maker, "notes.txt")), Is.Null);
            Assert.That(WebVoiceImport.Claim(new BrowserDownloadOffer(new Uri("https://example.com/"),
                new Uri("https://example.com/"), "speech.mp3", true)), Is.Null);
            // A file an embedded document from another site made is not the page's own speech.
            Assert.That(WebVoiceImport.Claim(new BrowserDownloadOffer(new Uri("https://ads.example/"),
                new Uri("https://tts.siyukatu.com/maker"), "ad.mp3", true)), Is.Null);
            // A link to the file names it by its address when the server gives no name.
            Assert.That(WebVoiceSites.Find(new BrowserDownloadOffer(new Uri("https://www.yukumo.net/api/v2/aqtk1/koe.mp3?kanji=a"),
                new Uri("https://www.yukumo.net/"), null, false)), Is.SameAs(yukumo));
        });
    }

    [Test]
    public void Page_data_is_read_leniently()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WebVoiceSites.ReadPage(PageData($" {Line} ", "f1", "女声1 (f1)")),
                Is.EqualTo(new WebVoicePage(Line, "f1", "女声1 (f1)")));
            Assert.That(WebVoiceSites.ReadPage("""{"text":"本文だけ"}"""), Is.EqualTo(new WebVoicePage("本文だけ", "", "")));
            foreach (var data in new[] { null, "", "not json", "[1]", """{"text":1}""" })
                Assert.That(WebVoiceSites.ReadPage(data).Text, Is.Empty, data);
        });
    }

    [AvaloniaTest]
    public async Task A_remembered_voice_is_placed_at_the_playhead_with_its_character_and_caption_in_one_step()
    {
        using var editor = Editor();
        var target = Character(editor.Scene);
        WebVoiceAssignmentStore.Save(StorePath, new WebVoiceAssignment
        {
            SiteId = "siyukatu", VoiceId = "f1", VoiceName = "女声1 (f1)",
            LipSync = PsdLipSyncPresets.Capture(target), Subtitle = VoiceSubtitles.Default(editor.Scene)
        });
        editor.Seek(MediaTime.FromSeconds(2));
        string file = Download();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);

        bool handled = await new WebVoiceImport(() => StorePath, NeverAsk).ImportAsync(new BrowserDownloadImportContext(editor,
            file, Offer(WebVoiceSites.YukkuriVoiceMaker), PageData(Line, "f1", "女声1 (f1)")), CancellationToken.None);

        var speech = editor.Scene.Children.Single(x => x.Objects.OfType<TtsController>().Any());
        var controller = speech.Objects.OfType<TtsController>().Single();
        var caption = Caption(editor.Scene);
        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(speech.Start.Value, Is.EqualTo(MediaTime.FromSeconds(2)));
            Assert.That(speech.Name.Value, Is.EqualTo(Line));
            Assert.That(speech.Objects.OfType<SourceSound>().Single().Source.Value!.Uri.LocalPath, Is.EqualTo(file));
            Assert.That(controller.Text.Value, Is.EqualTo(Line));
            Assert.That(controller.Generation.Value, Is.Null);
            // The character already in the scene speaks the line, and the speech goes below it.
            Assert.That(controller.LipSyncGroupId.Value, Is.Not.EqualTo(Guid.Empty));
            Assert.That(PsdLipSync.GroupOf(target), Is.EqualTo(controller.LipSyncGroupId.Value));
            Assert.That(speech.ZIndex.Value, Is.GreaterThan(target.FindHierarchicalParent<Element>()!.ZIndex.Value));
            Assert.That(caption.ZIndex.Value, Is.EqualTo(speech.ZIndex.Value + 1));
            Assert.That(caption.Range, Is.EqualTo(speech.Range));
            Assert.That(caption.EnumerateAllChildren<TextBlock>().Single().Text.Value, Is.EqualTo(Line));
            Assert.That(editor.History.UndoCount, Is.EqualTo(1));
        });

        Assert.That(editor.History.Undo(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(editor.Scene.Children.Where(x => x.Objects.OfType<TtsController>().Any()), Is.Empty);
            Assert.That(editor.Scene.EnumerateAllChildren<SubtitleController>(), Is.Empty);
            Assert.That(target.LipSyncAudio.Expression, Is.Null);
        });
    }

    [AvaloniaTest]
    public async Task Speech_from_Yukumo_is_resampled_with_Lagrange_before_any_other_effect()
    {
        using var editor = Editor();
        WebVoiceAssignmentStore.Save(StorePath, new WebVoiceAssignment { SiteId = "yukumo", VoiceId = "aqtk1-f1", VoiceName = "AT1-F1(ゆっくり)" });
        WebVoiceAssignmentStore.Save(StorePath, new WebVoiceAssignment { SiteId = "siyukatu", VoiceId = "f1", VoiceName = "女声1 (f1)" });
        var import = new WebVoiceImport(() => StorePath, NeverAsk);

        await import.ImportAsync(new BrowserDownloadImportContext(editor, Download(), Offer(WebVoiceSites.Yukumo, "yukumo_0001.mp3"),
            PageData(Line, "aqtk1-f1", "AT1-F1(ゆっくり)")), CancellationToken.None);
        editor.Seek(MediaTime.FromSeconds(3));
        await import.ImportAsync(new BrowserDownloadImportContext(editor, Download(), Offer(WebVoiceSites.YukkuriVoiceMaker),
            PageData(Line, "f1", "女声1 (f1)")), CancellationToken.None);

        var effects = editor.Scene.Children.OrderBy(x => x.Start.Value)
            .Select(x => ((AudioEffectGroup)x.Objects.OfType<SourceSound>().Single().Effect.Value!).Children.ToArray())
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(effects, Has.Length.EqualTo(2));
            Assert.That(effects[0].Select(x => (x as ResamplingEffect)?.Method.Value),
                Is.EqualTo(new ResamplingMethod?[] { ResamplingMethod.Lagrange }));
            // The maker's speech keeps the conversion that follows the effects.
            Assert.That(effects[1], Is.Empty);
        });
    }

    [AvaloniaTest]
    public async Task A_new_voice_is_asked_about_once_starting_from_the_speech_tab()
    {
        using var editor = Editor();
        var target = Character(editor.Scene);
        var template = TestTemplates.Save(_directory, target, "霊夢の立ち絵");
        using var tab = new TtsTabViewModel(new(), editor,
            (_, _, _) => throw new AssertionException("The tab synthesizes nothing here."), () => [template]);
        tab.LipSync.SelectedId = template.Id;
        tab.Subtitles.SelectedId = Guid.Empty;
        editor.ToolTabs.Add(tab);
        var asked = new List<WebVoiceAssignmentViewModel>();
        var import = new WebVoiceImport(() => StorePath, (model, _) =>
        {
            asked.Add(model);
            return Task.FromResult(model.TryCapture());
        }, () => [template]);

        foreach (var (voice, name, at) in new[] { ("f1", "女声1 (f1)", 0.0), ("f1", "女声1 (f1)", 3.0), ("f2", "女声2 (f2)", 6.0) })
        {
            editor.Seek(MediaTime.FromSeconds(at));
            Assert.That(await import.ImportAsync(new BrowserDownloadImportContext(editor, Download(),
                Offer(WebVoiceSites.YukkuriVoiceMaker), PageData(Line, voice, name)), CancellationToken.None), Is.True);
        }

        var stored = WebVoiceAssignmentStore.Load(StorePath);
        Assert.Multiple(() =>
        {
            // The second line of the same voice needs no answer.
            Assert.That(asked.Select(x => x.VoiceLabel), Is.EqualTo(new[] { "女声1 (f1)", "女声2 (f2)" }));
            Assert.That(asked[0].Description, Does.Contain("ゆっくりボイスメーカー").And.Contain("女声1 (f1)"));
            Assert.That(asked[0].LipSync.SelectedId, Is.EqualTo(template.Id));
            Assert.That(asked[0].Subtitles.SelectedId, Is.EqualTo(Guid.Empty));
            Assert.That(stored.Select(x => (x.SiteId, x.VoiceId)), Is.EquivalentTo(new[] { ("siyukatu", "f1"), ("siyukatu", "f2") }));
            Assert.That(stored.All(x => x.LipSync?.PsdPath == target.Source.Value!.FullName && x.Subtitle?.Name == "標準字幕"), Is.True);
            Assert.That(editor.Scene.EnumerateAllChildren<TtsController>().Count(), Is.EqualTo(3));
            Assert.That(editor.Scene.EnumerateAllChildren<TtsController>().Select(x => x.LipSyncGroupId.Value).Distinct().Single(),
                Is.EqualTo(PsdLipSync.GroupOf(target)));
            Assert.That(editor.Scene.EnumerateAllChildren<SubtitleController>().Count(), Is.EqualTo(3));
        });
    }

    [AvaloniaTest]
    public async Task Declining_the_choice_adds_and_remembers_nothing()
    {
        using var editor = Editor();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        bool handled = await new WebVoiceImport(() => StorePath, (_, _) => Task.FromResult<WebVoiceAssignment?>(null))
            .ImportAsync(new BrowserDownloadImportContext(editor, Download(), Offer(WebVoiceSites.Yukumo, "yukumo_0001.mp3"),
                PageData(Line, "aqtk1-f1", "AT1-F1(ゆっくり)")), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(editor.Scene.Children, Is.Empty);
            Assert.That(editor.History.UndoCount, Is.Zero);
            Assert.That(WebVoiceAssignmentStore.Load(StorePath), Is.Empty);
        });
    }

    [AvaloniaTest]
    public async Task Speech_whose_page_could_not_be_read_is_placed_without_a_caption()
    {
        using var editor = Editor();
        WebVoiceAssignmentViewModel? asked = null;
        var import = new WebVoiceImport(() => StorePath, (model, _) =>
        {
            asked = model;
            model.Subtitles.SelectedId = Guid.Empty;
            return Task.FromResult(model.TryCapture());
        });
        string file = Download();

        await import.ImportAsync(new BrowserDownloadImportContext(editor, file, Offer(WebVoiceSites.Yukumo, "yukumo_0001.mp3"), null),
            CancellationToken.None);

        var speech = editor.Scene.Children.Single();
        Assert.Multiple(() =>
        {
            Assert.That(asked?.VoiceLabel, Is.EqualTo("声を判別できませんでした"));
            Assert.That(speech.Name.Value, Is.EqualTo(Path.GetFileNameWithoutExtension(file)));
            Assert.That(speech.Objects.OfType<TtsController>().Single().Text.Value, Is.Empty);
            Assert.That(editor.Scene.EnumerateAllChildren<SubtitleController>(), Is.Empty);
            Assert.That(WebVoiceAssignmentStore.Find(StorePath, "yukumo", "")?.Subtitle?.Name, Is.EqualTo("標準字幕"));
        });
    }

    [AvaloniaTest]
    public async Task A_file_from_another_site_or_an_unsaved_scene_is_left_to_the_browser()
    {
        using var editor = Editor();
        var import = new WebVoiceImport(() => StorePath, NeverAsk);
        using var unsaved = new TestEditor(Path.Combine(_directory, "unused.bescene"));
        unsaved.Scene.Uri = null;

        bool otherSite = await import.ImportAsync(new BrowserDownloadImportContext(editor, Download(),
            new BrowserDownloadOffer(new Uri("https://example.com/"), new Uri("https://example.com/"), "a.mp3", true), null),
            CancellationToken.None);
        bool unsavedScene = await import.ImportAsync(new BrowserDownloadImportContext(unsaved, Download(),
            Offer(WebVoiceSites.YukkuriVoiceMaker), PageData(Line, "f1", "女声1 (f1)")), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(otherSite, Is.False);
            Assert.That(unsavedScene, Is.False);
            Assert.That(editor.Scene.Children, Is.Empty);
            Assert.That(unsaved.Scene.Children, Is.Empty);
        });
    }

    [AvaloniaTest]
    [TestCase(360, false)]
    [TestCase(480, true)]
    public async Task The_dialog_offers_character_templates_presets_and_captions_and_keeps_an_invalid_choice_open(int width, bool dark)
    {
        using var editor = Editor();
        var target = Character(editor.Scene);
        var template = TestTemplates.Save(_directory, target, "霊夢の立ち絵");
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, new VoicePreset
        {
            Name = "魔理沙", LipSync = PsdLipSyncPresets.Capture(target), Subtitle = VoiceSubtitles.Default(editor.Scene)
        });
        var model = new WebVoiceAssignmentViewModel(editor.Scene, WebVoiceSites.YukkuriVoiceMaker, "f2", "女声2 (f2)",
            templates: () => [template]);
        var window = new Window
        {
            Width = width, Height = 560, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
            Content = new Border()
        };
        try
        {
            window.Show();
            var shown = WebVoiceAssignmentDialog.ShowAsync(model, "追加", window, CancellationToken.None);
            await Settle();
            var dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single();
            var view = dialog.GetVisualDescendants().OfType<WebVoiceAssignmentView>().Single();
            Assert.Multiple(() =>
            {
                Assert.That(view.FindControl<ComboBox>("AssignmentPresetSelector")!.IsEffectivelyVisible, Is.True);
                Assert.That(model.LipSync.Choices.Value.Select(x => x.Name), Is.EqualTo(new[] { "連携しない", "霊夢の立ち絵" }),
                    "The character on the timeline is offered only through its template.");
                Assert.That(model.Subtitles.Choices.Value.Select(x => x.Name), Does.Contain("標準字幕"));
            });
            model.SelectedPreset.Value = model.Presets.Single();
            await Settle();
            Capture(window, $"web-voice-dialog-{width}");

            // A template that has gone from the library keeps the dialog open with the reason.
            model.LipSync.SelectedId = Guid.NewGuid();
            Primary(dialog);
            await Settle();
            Assert.That(shown.IsCompleted, Is.False);
            Assert.That(view.FindControl<Avalonia.Controls.TextBlock>("AssignmentMessage")!.IsEffectivelyVisible, Is.True);
            Capture(window, $"web-voice-dialog-invalid-{width}");

            model.LipSync.SelectedId = template.Id;
            Primary(dialog);
            var assignment = await shown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(assignment?.SiteId, Is.EqualTo("siyukatu"));
                Assert.That(assignment?.VoiceId, Is.EqualTo("f2"));
                Assert.That(assignment?.LipSync?.PsdPath, Is.EqualTo(target.Source.Value!.FullName));
                Assert.That(assignment?.Subtitle?.Name, Is.EqualTo("標準字幕"));
            });
        }
        finally
        {
            foreach (var dialog in window.GetVisualDescendants().OfType<FAContentDialog>().ToArray()) dialog.Hide();
            await Settle();
            window.Close();
        }

        static void Primary(FAContentDialog dialog) => dialog.GetVisualDescendants().OfType<Button>()
            .Single(x => x.Name == "PrimaryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    [AvaloniaTest]
    [TestCase(320, false)]
    [TestCase(460, true)]
    public async Task The_speech_tab_settings_list_changes_and_forgets_remembered_voices(int width, bool dark)
    {
        using var editor = Editor();
        WebVoiceAssignmentStore.Save(StorePath, new WebVoiceAssignment
        {
            SiteId = "siyukatu", VoiceId = "f1", VoiceName = "女声1 (f1)",
            LipSync = new VoiceLipSyncPreset("/characters/reimu.psd", "{}"), Subtitle = VoiceSubtitles.Default(editor.Scene)
        });
        WebVoiceAssignmentStore.Save(StorePath, new WebVoiceAssignment { SiteId = "yukumo", VoiceId = "aqtk1-f2", VoiceName = "AT1-F2" });
        using var tab = new TtsTabViewModel(new(), editor);
        tab.SelectedPage.Value = 3;
        var view = new TtsTabView { DataContext = tab };
        var window = new Window
        {
            Width = width, Height = 700, Content = view, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        try
        {
            window.Show();
            await Settle();
            var list = view.FindControl<WebVoiceAssignmentsView>("WebVoiceAssignments")!;
            Assert.That(list.IsEffectivelyVisible, Is.True);
            Assert.That(tab.WebVoices.Items.Value.Select(x => (x.Title, x.Details)), Is.EqualTo(new[]
            {
                ("ゆくも!: AT1-F2", "立ち絵: 連携しない / 字幕: 字幕なし"),
                ("ゆっくりボイスメーカー: 女声1 (f1)", "立ち絵: reimu / 字幕: 標準字幕")
            }));
            view.FindControl<ScrollViewer>("SettingsScroll")!.ScrollToEnd();
            await Settle();
            Capture(window, $"web-voice-settings-{width}");

            // Changing reopens the choice; the list follows the store.
            using var changes = new WebVoiceAssignmentsViewModel(editor.Scene, () => StorePath, (model, primary, _) =>
            {
                Assert.That(primary, Is.EqualTo("保存"));
                Assert.That(model.Subtitles.Selected.Value?.Template?.Name, Is.EqualTo("標準字幕"));
                model.Subtitles.SelectedId = null;
                return Task.FromResult(model.TryCapture());
            });
            await changes.Change(changes.Items.Value[1]);
            Assert.That(WebVoiceAssignmentStore.Find(StorePath, "siyukatu", "f1")?.Subtitle, Is.Null);
            Assert.That(tab.WebVoices.Items.Value[1].Details, Is.EqualTo("立ち絵: reimu / 字幕: 字幕なし"));
            // The list rebuilt its cards for the change.
            await Settle();

            var forget = list.GetVisualDescendants().OfType<Button>().First(x => x.Content as string == "削除");
            forget.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Assert.That(tab.WebVoices.Items.Value.Select(x => x.Title), Is.EqualTo(new[] { "ゆっくりボイスメーカー: 女声1 (f1)" }));
            Assert.That(WebVoiceAssignmentStore.Find(StorePath, "yukumo", "aqtk1-f2"), Is.Null);
        }
        finally { window.Close(); }
    }

    private static async Task Settle()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(250);
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is not { } directory) return;
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame()!;
        frame.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions());
    }
}
