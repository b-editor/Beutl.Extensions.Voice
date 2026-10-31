using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Beutl.Audio;
using Beutl.Editor.Observers;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using FluentAvalonia.UI.Controls;

namespace Beutl.Extensions.Voice.Tests;

public class HttpVoiceEngineTests
{
    private string _directory = null!;
    private string? _previousHome;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "voice-http-" + Guid.NewGuid());
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

    private TestEditor Editor() => new(Path.Combine(_directory, "scene.bescene"));

    [TestCase(true, 200, false)]
    [TestCase(false, 404, false)]
    [TestCase(false, 501, false)]
    [TestCase(false, 200, true)]
    public async Task Aivis_identity_and_tuning_survive_optional_manifest_changes(bool uppercaseUuid, int manifestStatus, bool omitUuid)
    {
        using var server = new EngineServer(aivis: true) { AdjustPitch = false };
        if (uppercaseUuid) server.EngineUuid = server.EngineUuid.ToUpperInvariant();
        var engine = await HttpVoiceEngine.DiscoverAsync(new() { Url = server.Url });
        Assert.That(engine.Capabilities.TempoDynamics, Is.True);
        VoiceEngineStore.Save(engine);
        server.ManifestStatus = manifestStatus;
        server.VersionStatus = 404;
        server.OmitUuid = omitUuid;

        var refreshed = await HttpVoiceEngine.DiscoverAsync(engine);
        Assert.That(refreshed.EngineUuid, Is.EqualTo(engine.EngineUuid));
        Assert.That(refreshed.Capabilities, Is.EqualTo(engine.Capabilities));
        Assert.That(refreshed.Version, Is.EqualTo(engine.Version));
        var output = await VoiceSynthesisService.SynthesizeAsync("テスト。",
            Settings(engine) with { PitchScale = 0.1, TempoDynamicsScale = 1.6 }, null);
        Assert.That(output.Settings.EngineUuid, Is.EqualTo(engine.EngineUuid));
        Assert.That(output.Settings.EngineVersion, Is.EqualTo(engine.Version));
        Assert.That(output.Settings.PitchScale, Is.Zero);
        Assert.That((double?)server.LastQuery!["tempoDynamicsScale"], Is.EqualTo(1.6));
        Assert.That(refreshed.Capabilities.EditAccent, Is.False);
    }

    [AvaloniaTest]
    [TestCase(404, false)]
    [TestCase(404, true)]
    [TestCase(501, false)]
    [TestCase(501, true)]
    public async Task Editing_connection_retains_metadata_only_for_the_same_endpoint(int optionalStatus, bool trailingSlash)
    {
        using var server = new EngineServer(aivis: true) { AdjustPitch = false };
        using var editor = Editor();
        using var model = new TtsTabViewModel(new(), editor);
        Assert.That(await model.SaveConnection("Original", server.Url.Replace("http:", "HTTP:")), Is.True, model.ConnectionMessage.Value);
        var original = VoiceEngineStore.Load().Single();
        Assert.That(original.Url, Is.EqualTo(server.Url), "Connection URLs are normalized before being stored.");
        server.ManifestStatus = server.VersionStatus = optionalStatus;

        string url = trailingSlash ? server.Url : server.Url.TrimEnd('/');
        Assert.That(await model.SaveConnection("Renamed", url, original.Id), Is.True, model.ConnectionMessage.Value);
        var renamed = VoiceEngineStore.Load().Single();
        Assert.That(renamed.Id, Is.EqualTo(original.Id));
        Assert.That(renamed.Name, Is.EqualTo("Renamed"));
        Assert.That(renamed.Url, Is.EqualTo(original.Url));
        Assert.That(renamed.EngineUuid, Is.EqualTo(original.EngineUuid));
        Assert.That(renamed.Version, Is.EqualTo(original.Version));
        Assert.That(renamed.Capabilities, Is.EqualTo(original.Capabilities));
        await VoiceSynthesisService.SynthesizeAsync("テスト。", Settings(renamed) with { TempoDynamicsScale = 1.6 }, null);
        Assert.That((double?)server.LastQuery!["tempoDynamicsScale"], Is.EqualTo(1.6));

        using var other = new EngineServer { ManifestStatus = optionalStatus, VersionStatus = optionalStatus };
        Assert.That(await model.SaveConnection("Moved", other.Url, original.Id), Is.True, model.ConnectionMessage.Value);
        var moved = VoiceEngineStore.Load().Single();
        Assert.That(moved.Id, Is.EqualTo(original.Id));
        Assert.That(moved.Url, Is.EqualTo(other.Url));
        Assert.That(moved.EngineUuid, Is.Empty);
        Assert.That(moved.Version, Is.Empty);
        Assert.That(moved.Capabilities, Is.EqualTo(new VoiceEngineCapabilities()));
        await VoiceSynthesisService.SynthesizeAsync("テスト。", Settings(moved), null);
        Assert.That(other.SynthesisCount, Is.EqualTo(1));
    }

    [AvaloniaTest]
    [TestCase("delete", false)]
    [TestCase("url", false)]
    [TestCase("name", false)]
    [TestCase("delete", true)]
    [TestCase("url", true)]
    public async Task Connection_updates_preserve_concurrent_changes(string change, bool save)
    {
        using var server = new EngineServer();
        using var editor = Editor();
        using var model = new TtsTabViewModel(new(), editor);
        Assert.That(await model.SaveConnection("Original", server.Url), Is.True);
        var original = model.SelectedEngine.Value!;
        server.EngineUuid = "refreshed-engine";
        server.PauseAt = "/tts/speakers";
        Task update = save ? model.SaveConnection("Edited", server.Url, original.Id) : model.ReloadEngine();
        await server.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "delete") VoiceEngineStore.Delete(original.Id);
        else VoiceEngineStore.Save(original with
        {
            Name = "Renamed", Url = change == "url" ? "http://localhost:50032/" : original.Url
        });
        server.Resume.TrySetResult();
        await update;

        var stored = VoiceEngineStore.Load();
        if (change == "delete") Assert.That(stored, Is.Empty);
        else
        {
            Assert.That(stored.Single().Name, Is.EqualTo("Renamed"));
            Assert.That(stored[0].Url, Is.EqualTo(change == "url" ? "http://localhost:50032/" : original.Url));
            Assert.That(stored[0].EngineUuid, Is.EqualTo(change == "url" ? original.EngineUuid : server.EngineUuid));
        }
        Assert.That(save ? model.ConnectionMessage.Value : model.Message.Value,
            Does.Contain(change == "name" ? "に接続しました" : "接続できませんでした"));
        if (save && change == "url")
        {
            Assert.That(await model.SaveConnection("Retried", server.Url, original.Id), Is.True);
            Assert.That(VoiceEngineStore.Load().Single().Url, Is.EqualTo(server.Url), "A fresh explicit edit can still change the URL.");
        }
    }

    [Test]
    [TestCase(false, 7L, true)]
    [TestCase(true, -2147483648L, true)]
    [TestCase(false, 4294967295L, false)]
    public async Task Compatible_protocol_preserves_identity_query_extensions_and_supported_tuning(bool aivis, long style, bool manifest)
    {
        using var first = new EngineServer(aivis, style, manifest);
        using var second = new EngineServer(aivis, style, manifest) { AdjustPitch = false };
        var engineA = await HttpVoiceEngine.DiscoverAsync(new() { Name = "First", Url = first.Url });
        var engineB = await HttpVoiceEngine.DiscoverAsync(new() { Name = "Second", Url = second.Url });
        VoiceEngineStore.Save(engineA);
        VoiceEngineStore.Save(engineB);
        Assert.That(engineB.Voices[0].Styles, Has.Length.EqualTo(1));
        var settings = Settings(engineB) with { SpeedScale = 1.3, PitchScale = 0.1, TempoDynamicsScale = aivis ? 1.6 : null };
        const string text = "こんにちは & + ? # / 世界。";
        var output = await VoiceSynthesisService.SynthesizeAsync(text, settings, null);
        Assert.That(first.SynthesisCount, Is.Zero);
        Assert.That(second.SynthesisCount, Is.EqualTo(1));
        Assert.That(second.LastText, Is.EqualTo(text));
        Assert.That(second.LastStyle, Is.EqualTo(style.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.That(output.Settings.EngineId, Is.EqualTo(engineB.Id));
        Assert.That(output.Settings.EngineVersion, Is.EqualTo("test-1"));
        Assert.That(output.Settings.UsesSameVoice(Settings(engineA)), Is.False);
        Assert.That(output.Settings.SpeedScale, Is.EqualTo(1.3));
        Assert.That(output.Settings.PitchScale, Is.EqualTo(manifest ? 0 : 0.1));
        var sent = second.LastQuery!;
        Assert.That((string?)sent["kana"], Is.EqualTo(text));
        Assert.That((int?)sent["outputSamplingRate"], Is.EqualTo(24000));
        Assert.That((string?)sent["future_parameter"], Is.EqualTo("preserve"));
        Assert.That((int?)sent["accent_phrases"]![0]!["moras"]![0]!["future_mora_parameter"], Is.EqualTo(123));
        Assert.That(output.Settings.TempoDynamicsScale, Is.EqualTo(aivis ? 1.6 : null));
        Assert.That(engineB.Capabilities.EditAccent, Is.EqualTo(!aivis));
        Assert.That(VoiceEngineStore.Load().Select(x => x.Id), Is.EqualTo(new[] { engineA.Id, engineB.Id }));
    }

    [AvaloniaTest]
    public async Task Http_presets_restore_and_regeneration_failure_keeps_audio_and_manual_edits()
    {
        using var server = new EngineServer();
        using var editor = Editor();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        using var model = new TtsTabViewModel(new(), editor);
        Assert.That(await model.SaveConnection("Local", server.Url), Is.True, model.ConnectionMessage.Value);
        model.SelectedVoice.Value = model.Voice.Value.Single();
        model.Text.Value = "こんにちは。";
        model.PresetName.Value = "外部エンジンの声";
        model.AddPreset();
        model.ApplyPreset();
        var state = new JsonObject();
        model.WriteToJson(state);
        using var restored = new TtsTabViewModel(new(), editor);
        restored.ReadFromJson(state);
        await Settle();
        Assert.That(restored.SelectedEngine.Value!.Id, Is.EqualTo(model.SelectedEngine.Value!.Id));
        Assert.That(restored.SelectedStyle.Value!.Id, Is.EqualTo(model.SelectedStyle.Value!.Id));
        await restored.Generate();
        var element = editor.Scene.Children.Single();
        var controller = element.Objects.OfType<TtsController>().Single();
        var sound = element.Objects.OfType<SourceSound>().Single();
        string original = sound.Source.Value!.Uri.LocalPath;
        using var details = TtsControllerTests.CreateModel(editor, controller);
        details.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch = 5.9;
        details.OnAccentPhrasesUpdated();
        var draft = controller.Draft.Value;
        await details.ReloadEngine();
        Assert.That(details.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch, Is.EqualTo(5.9));
        Assert.That(controller.Draft.Value, Is.EqualTo(draft));
        server.FailSynthesis = true;
        await details.Regenerate();
        Assert.That(details.Message.Value, Does.Contain("HTTP 503"));
        Assert.That(sound.Source.Value!.Uri.LocalPath, Is.EqualTo(original));
        Assert.That(File.Exists(original), Is.True);
        Assert.That(controller.Draft.Value, Is.EqualTo(draft));
        server.FailSynthesis = false;
        server.PauseAt = "/tts/synthesis";
        var regenerating = details.Regenerate();
        await server.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        element.IsLocked.Value = true;
        server.Resume.TrySetResult();
        await regenerating;
        Assert.That(details.Message.Value, Does.Contain("ロック"));
        Assert.That(sound.Source.Value!.Uri.LocalPath, Is.EqualTo(original));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(original)!, "*.wav"), Is.EqualTo(new[] { original }));
        Assert.That(controller.Draft.Value, Is.EqualTo(draft));
        element.IsLocked.Value = false;
        server.PauseAt = null;
        await details.Regenerate();
        Assert.That(details.Message.Value, Is.EqualTo("音声を再生成しました。"));
        Assert.That(File.Exists(sound.Source.Value!.Uri.LocalPath), Is.True);
        Assert.That((double?)server.LastQuery!["accent_phrases"]![0]!["moras"]![0]!["pitch"], Is.EqualTo(5.9));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(sound.Source.Value!.Uri.LocalPath, Is.EqualTo(original));
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(sound.Source.Value!.Uri.LocalPath, Is.Not.EqualTo(original));
        VoiceEngineStore.Delete(model.SelectedEngine.Value!.Id);
        await details.Regenerate();
        Assert.That(details.Message.Value, Does.Contain("接続先").And.Contain("見つかりません"));
        Assert.That(server.SynthesisCount, Is.EqualTo(3), "Missing connections must never fall back to a different engine.");
    }

    [AvaloniaTest]
    public async Task Mixed_engine_script_preserves_assignments_and_can_retry_after_a_failed_utterance()
    {
        using var first = new EngineServer();
        using var second = new EngineServer(aivis: true) { FailSynthesis = true };
        var engines = new[]
        {
            await HttpVoiceEngine.DiscoverAsync(new() { Name = "First", Url = first.Url }),
            await HttpVoiceEngine.DiscoverAsync(new() { Name = "Second", Url = second.Url })
        };
        foreach (var engine in engines) VoiceEngineStore.Save(engine);
        using var editor = Editor();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        using var script = new ScriptImportViewModel(editor);
        script.RawText.Value = "A: 最初のエンジンです。\nB: 別のエンジンです。";
        script.ImportText();
        for (int i = 0; i < engines.Length; i++)
            script.Speakers[i].SelectedPreset.Value = new() { Name = "同じ名前", Settings = Settings(engines[i]) };
        await script.Generate();
        Assert.That(editor.Scene.Children, Is.Empty);
        Assert.That(Directory.GetFiles(Path.Combine(_directory, "resources", "tts")), Is.Empty);
        Assert.That(script.Speakers[1].SelectedPreset.Value!.Settings.EngineId, Is.EqualTo(engines[1].Id));
        second.FailSynthesis = false;
        await script.Generate();
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(2), script.Message.Value);
        Assert.That(editor.Scene.Children.Select(x => x.Objects.OfType<TtsController>().Single().Generation.Value!.Settings.EngineId),
            Is.EqualTo(engines.Select(x => x.Id)));
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Is.Empty);
        Assert.That(editor.History.Redo(), Is.True);
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(2));
    }

    [AvaloniaTest]
    [TestCase(320, false)]
    [TestCase(460, true)]
    public async Task Connection_editor_and_aivis_controls_work_without_native_core(int width, bool light)
    {
        using var server = new EngineServer(aivis: true, style: -12);
        using var editor = Editor();
        using var model = new TtsTabViewModel(new(), editor);
        var view = new TtsTabView { DataContext = model };
        var window = new Window { Width = width, Height = 600, Content = view,
            RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        try
        {
            window.Show();
            await Settle();
            var voiceView = view.FindControl<VoiceEditorView>("VoiceEditor")!;
            var menuButton = voiceView.FindControl<Button>("EngineMenuButton")!;
            var menu = (FAMenuFlyout)menuButton.Flyout!;
            async Task<Flyout> OpenConnectionEditor(string action)
            {
                menu.ShowAt(menuButton);
                await Settle();
                menu.Hide();
                menu.Items.OfType<FAMenuFlyoutItem>().Single(x => x.Text == action)
                    .RaiseEvent(new RoutedEventArgs(FAMenuFlyoutItem.ClickEvent));
                await Settle();
                return (Flyout)FlyoutBase.GetAttachedFlyout(menuButton)!;
            }
            static async Task Save(Flyout target)
            {
                ((StackPanel)target.Content!).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var save = ((StackPanel)target.Content!).Children.OfType<Button>().Single();
                for (int i = 0; !save.IsEnabled && i < 500; i++) await Task.Delay(10);
                await Settle();
            }
            var flyout = await OpenConnectionEditor("接続先を追加…");
            var content = (StackPanel)flyout.Content!;
            var inputs = content.Children.OfType<TextBox>().ToArray();
            inputs[0].Text = "Aivis テスト";
            inputs[1].Text = "invalid-url";
            await Save(flyout);
            Assert.That(flyout.IsOpen, Is.True);
            Assert.That(inputs[0].Text, Is.EqualTo("Aivis テスト"));
            Assert.That(model.ConnectionMessage.Value, Does.Contain("URL"));
            inputs[1].Text = server.Url;
            Capture(TopLevel.GetTopLevel(flyout.Popup.Child)!, $"connection-{width}");
            await Save(flyout);
            Assert.That(flyout.IsOpen, Is.False, model.ConnectionMessage.Value);
            Assert.That(model.ShowCoreInstallPrompt.Value, Is.False);
            Assert.That(model.IsEnabled.Value, Is.True);
            voiceView.FindControl<ComboBox>("VoiceSelector")!.SelectedItem = model.Voice.Value.Single();
            voiceView.FindControl<Expander>("TuningExpander")!.IsExpanded = true;
            model.TempoDynamicsScale.Value = 1.5;
            model.PresetName.Value = "感情を調整した声";
            model.AddPreset();
            Assert.That(model.SelectedPreset.Value!.Settings.TempoDynamicsScale, Is.EqualTo(1.5));
            Assert.That(voiceView.FindControl<Expander>("AccentExpander")!.IsVisible, Is.False);
            await Settle();
            view.FindControl<ScrollViewer>("EditorScroll")!.Offset = new Vector(0, double.MaxValue);
            await Settle();
            foreach (var slider in voiceView.GetVisualDescendants().OfType<Slider>().Where(x => x.IsEffectivelyVisible))
            {
                Assert.That(slider.Bounds.Width, Is.GreaterThan(60));
                Assert.That(slider.Bounds.Right, Is.LessThanOrEqualTo(((Control)slider.Parent!).Bounds.Width));
            }
            Capture(window, $"aivis-{width}-{(light ? "light" : "dark")}");
            model.SelectedEngine.Value = model.Engines.Value.Single(x => x.IsCore);
            Assert.That(voiceView.FindControl<Expander>("AccentExpander")!.IsVisible, Is.True);
            model.ApplyPreset();
            Assert.That(model.IsHttpEngine.Value, Is.True);
            Assert.That(model.TempoDynamicsScale.Value, Is.EqualTo(1.5));

            var connectionId = model.SelectedEngine.Value!.Id;
            flyout = await OpenConnectionEditor("接続先を編集…");
            VoiceEngineStore.Delete(connectionId);
            await Save(flyout);
            Assert.That(flyout.IsOpen, Is.True);
            Assert.That(model.ConnectionMessage.Value, Does.Contain("削除"));
            Assert.That(VoiceEngineStore.Load(), Is.Empty, "An editor opened before deletion must not recreate the connection.");
            Capture(TopLevel.GetTopLevel(flyout.Popup.Child)!, $"connection-deleted-{width}");
            flyout.Hide();

            flyout = await OpenConnectionEditor("接続先を編集…");
            ((StackPanel)flyout.Content!).Children.OfType<TextBox>().Last().Text = server.Url;
            await Save(flyout);
            Assert.That(flyout.IsOpen, Is.False, model.ConnectionMessage.Value);
            Assert.That(VoiceEngineStore.Load().Single().Id, Is.EqualTo(connectionId));
            Assert.That(model.SelectedStyle.Value, Is.Not.Null, "Editing a missing connection must restore its original preset identity.");
            Assert.That(model.TempoDynamicsScale.Value, Is.EqualTo(1.5));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    [TestCase("VOICE_HTTP_VOICEVOX_URL")]
    [TestCase("VOICE_HTTP_AIVIS_URL")]
    [TestCase("VOICE_HTTP_SHAREVOX_URL")]
    public async Task Native_http_engine_generates_regenerates_and_imports_a_script(string variable)
    {
        string? url = Environment.GetEnvironmentVariable(variable);
        if (url == null) Assert.Ignore($"Set {variable} to a running engine to enable this integration test.");
        using var editor = Editor();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        using var model = new TtsTabViewModel(new(), editor);
        Assert.That(await model.SaveConnection("", url!), Is.True, model.ConnectionMessage.Value);
        TestContext.Out.WriteLine($"Engine: {model.SelectedEngine.Value!.Name} {model.SelectedEngine.Value.Version}");
        model.SelectedVoice.Value = model.Voice.Value.First();
        model.Text.Value = "接続を確認します。";
        model.SpeedScale.Value = 1.1;
        model.TempoDynamicsScale.Value = 0.8;
        model.PresetName.Value = "接続テスト";
        model.AddPreset();
        model.ApplyPreset();
        await model.Generate();
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(1), model.Message.Value);
        var element = editor.Scene.Children.Single();
        var controller = element.Objects.OfType<TtsController>().Single();
        var sound = element.Objects.OfType<SourceSound>().Single();
        string original = sound.Source.Value!.Uri.LocalPath;
        Assert.That(new FileInfo(original).Length, Is.GreaterThan(1000));
        using var details = TtsControllerTests.CreateModel(editor, controller);
        details.SpeedScale.Value = 1.2;
        details.ConfirmEdit();
        await details.Regenerate();
        Assert.That(details.Message.Value, Is.EqualTo("音声を再生成しました。"));
        Assert.That(controller.Generation.Value!.Settings.SpeedScale, Is.EqualTo(1.2));
        Assert.That(File.Exists(original), Is.True);
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(sound.Source.Value!.Uri.LocalPath, Is.EqualTo(original));
        Assert.That(editor.History.Redo(), Is.True);
        using var script = new ScriptImportViewModel(editor);
        script.RawText.Value = "A: こんにちは。\nB: ありがとうございます。";
        script.ImportText();
        foreach (var speaker in script.Speakers) speaker.SelectedPreset.Value = model.SelectedPreset.Value;
        script.FirstLayer.Value = 5;
        await script.Generate();
        Assert.That(editor.Scene.Children, Has.Count.EqualTo(3), script.Message.Value);
        Assert.That(editor.Scene.Children.All(x => x.Length.Value > MediaTime.Zero), Is.True);
    }

    private static VoiceSettings Settings(VoiceEngineConnection engine) => new()
    {
        EngineId = engine.Id, EngineName = engine.Name, EngineUuid = engine.EngineUuid,
        SpeakerUuid = engine.Voices[0].SpeakerUuid, SpeakerName = engine.Voices[0].Name,
        StyleId = engine.Voices[0].Styles[0].Id, StyleName = engine.Voices[0].Styles[0].Name
    };

    private static async Task Settle()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
    }

    private static void Capture(TopLevel topLevel, string name)
    {
        if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is not { } directory) return;
        Directory.CreateDirectory(directory);
        using var frame = topLevel.CaptureRenderedFrame()!;
        frame.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions());
    }

    internal sealed class EngineServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public string Url { get; }
        public int DiscoveryCount { get; private set; }
        public int SynthesisCount { get; private set; }
        public bool FailSynthesis { get; set; }
        public bool FailDiscovery { get; set; }
        public bool AdjustPitch { get; set; } = true;
        public string EngineUuid { get; set; }
        public int ManifestStatus { get; set; }
        public int VersionStatus { get; set; } = 200;
        public bool OmitUuid { get; set; }
        public string? PauseAt { get; set; }
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? LastText { get; private set; }
        public string? LastStyle { get; private set; }
        public JsonNode? LastQuery { get; private set; }

        public EngineServer(bool aivis = false, long style = 7, bool manifest = true)
        {
            EngineUuid = aivis ? VoiceEngineCapabilities.AivisSpeechUuid : "compatible-engine";
            ManifestStatus = manifest ? 200 : 404;
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            int number = ((IPEndPoint)port.LocalEndpoint).Port;
            port.Stop();
            string prefix = $"http://127.0.0.1:{number}/";
            Url = prefix + "tts/";
            _listener.Prefixes.Add(prefix);
            _listener.Prefixes.Add($"http://localhost:{number}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        var context = await _listener.GetContextAsync();
                        string endpoint = context.Request.Url!.AbsolutePath;
                        if (endpoint == "/tts/speakers") DiscoveryCount++;
                        if (endpoint == "/tts/speakers" && FailDiscovery)
                        {
                            context.Response.StatusCode = 503;
                            context.Response.Close();
                            continue;
                        }
                        if (endpoint == PauseAt)
                        {
                            Paused.TrySetResult();
                            await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        }
                        object? result = null;
                        if (endpoint == "/tts/version")
                        {
                            context.Response.StatusCode = VersionStatus;
                            if (VersionStatus == 200) result = "test-1";
                        }
                        else if (endpoint == "/tts/speakers") result = new[] { new VoiceMetadata
                        {
                            Name = "テスト話者", SpeakerUuid = "same-speaker", Version = "model-1",
                            Styles = [new() { Id = style, Name = "通常", Type = aivis ? "talk" : "streaming_talk" }, new() { Id = 999, Name = "歌唱", Type = "sing" }]
                        } };
                        else if (endpoint == "/tts/engine_manifest" && ManifestStatus == 200) result = new
                        {
                            name = aivis ? "AivisSpeech Engine" : "Compatible engine",
                            uuid = EngineUuid,
                            supported_features = new { adjust_mora_pitch = !aivis, adjust_pitch_scale = AdjustPitch }
                        };
                        else if (endpoint == "/tts/engine_manifest") context.Response.StatusCode = ManifestStatus;
                        else if (endpoint == "/tts/audio_query")
                        {
                            LastText = context.Request.QueryString["text"];
                            var query = JsonSerializer.SerializeToNode(new AudioQueryModel
                            {
                                Kana = LastText, TempoDynamicsScale = aivis ? 1 : null,
                                AccentPhrases = [new() { Accent = 1, Moras = [new() { Text = "ア", Vowel = "a", VowelLength = 0.1, Pitch = 5 }] }]
                            })!;
                            query["future_parameter"] = "preserve";
                            query["accent_phrases"]![0]!["moras"]![0]!["future_mora_parameter"] = 123;
                            result = query;
                        }
                        else if (endpoint == "/tts/synthesis")
                        {
                            LastStyle = context.Request.QueryString["speaker"];
                            using var reader = new StreamReader(context.Request.InputStream);
                            LastQuery = JsonNode.Parse(await reader.ReadToEndAsync());
                            if (FailSynthesis) context.Response.StatusCode = 503;
                            else { SynthesisCount++; result = Wave(); }
                        }
                        else context.Response.StatusCode = 404;
                        if (endpoint == "/tts/engine_manifest" && result != null && OmitUuid)
                        {
                            var manifestJson = JsonSerializer.SerializeToNode(result)!.AsObject();
                            manifestJson.Remove("uuid");
                            result = manifestJson;
                        }
                        byte[] bytes = result as byte[] ?? JsonSerializer.SerializeToUtf8Bytes(result);
                        context.Response.ContentType = result is byte[] ? "audio/wav" : "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes);
                        context.Response.Close();
                    }
                }
                catch (HttpListenerException) when (!_listener.IsListening) { }
                catch (ObjectDisposedException) when (!_listener.IsListening) { }
            });
        }

        internal static byte[] Wave()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + 9600);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(24000); writer.Write(48000);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(9600); writer.Write(new byte[9600]);
            return stream.ToArray();
        }

        public void Dispose()
        {
            Resume.TrySetResult();
            _listener.Stop();
            _loop.GetAwaiter().GetResult();
            _listener.Close();
        }
    }
}
