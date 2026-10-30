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
using System.Text.Json.Nodes;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using FluentAvalonia.UI.Controls;

namespace Beutl.Extensions.Voice.Tests;

public class LocalEngineSetupTests
{
    private string _directory = null!;
    private string? _previousHome;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "voice-local-setup-" + Guid.NewGuid());
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

    [TestCase("macos")]
    [TestCase("windows")]
    [TestCase("linux")]
    public void Finds_installed_applications_without_treating_missing_binaries_as_installed(string platform)
    {
        foreach (var engine in LocalVoiceEngines.Known)
        {
            string path = platform switch
            {
                "macos" => Path.Combine(_directory, engine.ApplicationName + ".app"),
                "windows" => Path.Combine(_directory, engine.ApplicationName, engine.ApplicationName + ".exe"),
                _ => Path.Combine(_directory, engine.ApplicationName + ".AppImage")
            };
            string marker = platform == "macos" ? Path.Combine(path, "Contents", "Info.plist") : path;
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            Assert.That(LocalVoiceEngines.FindApplication(engine, [_directory], platform), Is.Null);
            File.WriteAllText(marker, "");
            Assert.That(LocalVoiceEngines.FindApplication(engine, [_directory], platform), Is.EqualTo(path));
        }
    }

    [Test]
    public void Launch_uses_a_literal_application_path_and_standalone_engine_working_directory()
    {
        string bundle = Path.Combine(_directory, "An app 'with spaces'.app");
        var mac = LocalVoiceEngines.CreateStartInfo(bundle, macOS: true);
        Assert.That(mac.FileName, Is.EqualTo("/usr/bin/open"));
        Assert.That(mac.ArgumentList, Is.EqualTo(new[] { "-a", bundle }));
        string engine = Path.Combine(_directory, "engine folder", "run");
        var executable = LocalVoiceEngines.CreateStartInfo(engine, macOS: true);
        Assert.That(executable.FileName, Is.EqualTo(engine));
        Assert.That(executable.WorkingDirectory, Is.EqualTo(Path.GetDirectoryName(engine)));
        Assert.That(executable.UseShellExecute, Is.False);
    }

    [AvaloniaTest]
    public async Task Search_is_read_only_and_registration_reuses_existing_preset_identity()
    {
        using var server = new HttpVoiceEngineTests.EngineServer(aivis: true);
        int launches = 0;
        using var model = new LocalEngineSetupViewModel([LocalVoiceEngines.Known[1] with { Url = server.Url }],
            _ => "/an/installed/app", _ => launches++);
        await model.Refresh();
        Assert.That(VoiceEngineStore.Load(), Is.Empty);
        Assert.That(launches, Is.Zero);
        var existing = model.Engines[0].Connection! with { Id = "preset-engine-id", Name = "My engine" };
        VoiceEngineStore.Save(existing);
        server.ManifestStatus = 404;
        server.VersionStatus = 501;
        await model.Refresh();
        Assert.That(await model.Use(model.Engines[0]), Is.True, model.Message.Value);
        Assert.That(model.SelectedConnection!.Id, Is.EqualTo(existing.Id));
        Assert.That(model.SelectedConnection.Name, Is.EqualTo(existing.Name));
        Assert.That(model.SelectedConnection.EngineUuid, Is.EqualTo(existing.EngineUuid));
        Assert.That(model.SelectedConnection.Version, Is.EqualTo(existing.Version));
        Assert.That(model.SelectedConnection.Capabilities.TempoDynamics, Is.True);
        Assert.That(VoiceEngineStore.Load(), Has.Length.EqualTo(1));
        Assert.That(launches, Is.Zero, "An engine that is already responding must not be started again.");
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Explicit_start_waits_for_readiness_and_closing_cancels_registration(bool cancel)
    {
        using var server = new HttpVoiceEngineTests.EngineServer { FailDiscovery = true };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int launches = 0;
        using var model = new LocalEngineSetupViewModel([LocalVoiceEngines.Known[0] with { Url = server.Url }],
            _ => null, path =>
            {
                Assert.That(path, Is.EqualTo("/chosen/engine/run"));
                launches++;
                if (!cancel) server.FailDiscovery = false;
                started.TrySetResult();
            });
        var entry = model.Engines[0];
        await model.Refresh();
        Assert.That(entry.CanUse.Value, Is.False);
        model.SelectApplication(entry, "/chosen/engine/run");
        Assert.That(entry.CanUse.Value, Is.True);
        Assert.That(launches, Is.Zero);
        var usingEngine = model.Use(entry);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel)
        {
            model.Dispose();
            server.FailDiscovery = false;
        }
        Assert.That(await usingEngine.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(!cancel), model.Message.Value);
        Assert.That(launches, Is.EqualTo(1));
        Assert.That(VoiceEngineStore.Load().Length, Is.EqualTo(cancel ? 0 : 1));
        Assert.That(model.IsBusy.Value, Is.False);
    }

    [AvaloniaTest]
    public async Task Failed_application_launch_is_visible_and_can_be_retried()
    {
        using var server = new HttpVoiceEngineTests.EngineServer { FailDiscovery = true };
        using var model = new LocalEngineSetupViewModel([LocalVoiceEngines.Known[0] with { Url = server.Url }], _ => null,
            path =>
            {
                if (path == "/missing/run") throw new FileNotFoundException("アプリが見つかりません。");
                server.FailDiscovery = false;
            });
        var entry = model.Engines[0];
        model.SelectApplication(entry, "/missing/run");
        Assert.That(await model.Use(entry), Is.False);
        Assert.That(entry.Status.Value, Does.Contain("アプリが見つかりません"));
        Assert.That(model.IsBusy.Value, Is.False);
        Assert.That(VoiceEngineStore.Load(), Is.Empty);
        model.SelectApplication(entry, "/installed/run");
        Assert.That(await model.Use(entry), Is.True, model.Message.Value);
        Assert.That(VoiceEngineStore.Load(), Has.Length.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task Leaving_settings_cancels_search_without_registering_a_late_response()
    {
        using var server = new HttpVoiceEngineTests.EngineServer { PauseAt = "/tts/speakers" };
        using var model = new LocalEngineSetupViewModel([LocalVoiceEngines.Known[0] with { Url = server.Url }], _ => null);
        using var editor = new TestEditor(Path.Combine(_directory, "scene.bescene"));
        using var speech = new TtsTabViewModel(new(), editor);
        speech.SelectedPage.Value = 2;
        var view = new TtsTabView { DataContext = speech };
        var setup = view.FindControl<LocalEngineSetupView>("EngineSetup")!;
        (setup.DataContext as LocalEngineSetupViewModel)?.Dispose();
        setup.DataContext = model;
        var window = new Window { Width = 320, Height = 600, Content = view };
        window.Show();
        try
        {
            await server.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            view.FindControl<TabStrip>("PageTabs")!.SelectedIndex = 0;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            server.Resume.TrySetResult();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (model.IsBusy.Value && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.That(model.IsBusy.Value, Is.False);
            Assert.That(VoiceEngineStore.Load(), Is.Empty);
            Assert.That(model.Engines[0].Connection, Is.Null);
            view.FindControl<TabStrip>("PageTabs")!.SelectedIndex = 2;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Assert.That(setup.DataContext, Is.Not.SameAs(model), "Reopening settings starts a fresh search session.");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    [TestCase(320, false)]
    [TestCase(460, true)]
    public async Task Settings_tab_renders_scrolls_and_selects_the_registered_engine(int width, bool light)
    {
        using var ready = new HttpVoiceEngineTests.EngineServer(aivis: true);
        using var stopped = new HttpVoiceEngineTests.EngineServer { FailDiscovery = true };
        using var editor = new TestEditor(Path.Combine(_directory, "scene.bescene"));
        using var speech = new TtsTabViewModel(new(), editor);
        speech.SelectedPage.Value = 2;
        var owner = new TtsTabView { DataContext = speech };
        var window = new Window { Width = width, Height = 600, Content = owner,
            RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        using var model = new LocalEngineSetupViewModel([
            LocalVoiceEngines.Known[1] with { Url = ready.Url },
            LocalVoiceEngines.Known[0] with { Url = stopped.Url },
            LocalVoiceEngines.Known[2] with { Url = stopped.Url }
        ], engine => engine.ApplicationName == "VOICEVOX" ? "/Applications/VOICEVOX.app" : null);
        var content = owner.FindControl<LocalEngineSetupView>("EngineSetup")!;
        (content.DataContext as LocalEngineSetupViewModel)?.Dispose();
        content.DataContext = model;
        window.Show();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (model.IsBusy.Value && DateTime.UtcNow < deadline) await Task.Delay(20);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(150);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            Assert.That(model.IsBusy.Value, Is.False);
            Assert.That(window.GetVisualDescendants().OfType<FAContentDialog>(), Is.Empty);
            Assert.That(content.Bounds.Width, Is.LessThanOrEqualTo(width));
            var tabs = owner.FindControl<TabStrip>("PageTabs")!;
            Assert.That(tabs.Items.Cast<TabStripItem>().Select(x => x.Content), Is.EqualTo(new[] { "読み上げ", "台本", "設定" }));
            var scroll = owner.FindControl<ScrollViewer>("SettingsScroll")!;
            if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is { } directory)
            {
                Directory.CreateDirectory(directory);
                using var frame = window.CaptureRenderedFrame()!;
                frame.Save(Path.Combine(directory, $"voice-settings-tab-{width}.png"), new PngBitmapEncoderOptions());
            }
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            window.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            Assert.That(scroll.Offset.Y + scroll.Viewport.Height, Is.EqualTo(scroll.Extent.Height).Within(1));
            var last = content.GetVisualDescendants().OfType<Button>().Last(x => x.Name == "SelectApplicationButton");
            var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), scroll)!.Value.Y;
            if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is { } bottomDirectory)
            {
                using var frame = window.CaptureRenderedFrame()!;
                frame.Save(Path.Combine(bottomDirectory, $"voice-settings-tab-bottom-{width}.png"), new PngBitmapEncoderOptions());
            }
            Assert.That(bottom, Is.InRange(0, scroll.Bounds.Height),
                $"Last controls must be reachable (offset {scroll.Offset.Y}, extent {scroll.Extent.Height}, viewport {scroll.Viewport.Height}).");
            scroll.Offset = default;
            var button = content.GetVisualDescendants().OfType<Button>().First(x => x.Name == "UseEngineButton");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (speech.SelectedPage.Value != 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.That(speech.SelectedPage.Value, Is.Zero, model.Message.Value);
            Assert.That(model.SelectedConnection, Is.Not.Null);
            Assert.That(speech.SelectedEngine.Value!.Id, Is.EqualTo(model.SelectedConnection!.Id));
            Assert.That(speech.EngineCapabilities.Value.TempoDynamics, Is.True);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task Speech_and_script_drafts_survive_tab_changes_and_serialization()
    {
        using var editor = new TestEditor(Path.Combine(_directory, "scene.bescene"));
        using var model = new TtsTabViewModel(new(), editor);
        var view = new TtsTabView { DataContext = model };
        var window = new Window { Width = 320, Height = 600, Content = view };
        window.Show();
        try
        {
            model.Text.Value = "読み上げの下書き";
            model.Script.RawText.Value = "案内役: 台本の下書き";
            model.Script.ImportText();
            model.Script.GapSeconds.Value = 0.7;
            async Task SelectPage(int index, string name)
            {
                view.FindControl<TabStrip>("PageTabs")!.SelectedIndex = index;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = window.CaptureRenderedFrame()!;
                    frame.Save(Path.Combine(directory, $"voice-{name}-tab-320.png"), new PngBitmapEncoderOptions());
                }
            }
            await SelectPage(0, "speech");
            await SelectPage(1, "script");
            window.UpdateLayout();
            Assert.That(view.FindControl<ScriptImportView>("ScriptPage")!.DataContext, Is.SameAs(model.Script));
            Assert.That(view.FindControl<ScriptImportView>("ScriptPage")!.IsEffectivelyVisible, Is.True);
            Assert.That(model.Script.Lines, Has.Count.EqualTo(1));
            await SelectPage(0, "speech");
            Assert.That(view.FindControl<TextBox>("SpeechTextBox")!.Text, Is.EqualTo("読み上げの下書き"));
            await SelectPage(1, "script");
            var state = new JsonObject();
            model.WriteToJson(state);
            using var restored = new TtsTabViewModel(new(), editor);
            restored.ReadFromJson(state);
            Assert.That(restored.SelectedPage.Value, Is.EqualTo(1));
            Assert.That(restored.Text.Value, Is.EqualTo(model.Text.Value));
            Assert.That(restored.Script.RawText.Value, Is.EqualTo(model.Script.RawText.Value));
            Assert.That(restored.Script.GapSeconds.Value, Is.EqualTo(0.7));
            Assert.That(restored.Script.Lines.Single().Text, Is.EqualTo("台本の下書き"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Inline_close_button_cancels_search_and_returns_to_the_tab()
    {
        using var server = new HttpVoiceEngineTests.EngineServer { PauseAt = "/tts/speakers" };
        using var model = new LocalEngineSetupViewModel([LocalVoiceEngines.Known[0] with { Url = server.Url }], _ => null);
        var host = new ContentControl();
        var window = new Window { Width = 320, Height = 450, Content = new ScrollViewer { Content = host } };
        bool closed = false;
        window.Show();
        try
        {
            LocalEngineSetupView.ShowIn(host, closed: () => closed = true, model: model);
            await server.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ((LocalEngineSetupView)host.Content!).FindControl<Button>("CloseSetupButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            server.Resume.TrySetResult();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (model.IsBusy.Value && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.That(closed, Is.True);
            Assert.That(host.Content, Is.Null);
            Assert.That(host.IsVisible, Is.False);
            Assert.That(model.IsBusy.Value, Is.False);
            Assert.That(VoiceEngineStore.Load(), Is.Empty);
        }
        finally { window.Close(); }
    }
}
