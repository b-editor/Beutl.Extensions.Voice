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
using Beutl.Extensions.Voice.Models;
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
            if (platform == "macos")
            {
                Assert.That(LocalVoiceEngines.FindApplication(engine, [_directory], platform), Is.Null);
                Directory.CreateDirectory(Path.Combine(path, "Contents", "MacOS"));
                File.WriteAllText(Path.Combine(path, "Contents", "MacOS", engine.ApplicationName), "");
            }
            Assert.That(LocalVoiceEngines.FindApplication(engine, [_directory], platform), Is.EqualTo(path));
        }
    }

    [Test]
    public void Incomplete_mac_bundle_does_not_hide_an_installed_application()
    {
        string[] roots = [Path.Combine(_directory, "system"), Path.Combine(_directory, "user")];
        foreach (var engine in LocalVoiceEngines.Known)
        {
            string[] bundles = roots.Select(root => Path.Combine(root, engine.ApplicationName + ".app")).ToArray();
            foreach (string bundle in bundles)
            {
                Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
                File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), "");
            }
            string executable = Path.Combine("Contents", "MacOS", engine.ApplicationName);
            File.WriteAllText(Path.Combine(bundles[1], executable), "");
            Assert.That(LocalVoiceEngines.FindApplication(engine, roots, "macos"), Is.EqualTo(bundles[1]));
            File.WriteAllText(Path.Combine(bundles[0], executable), "");
            Assert.That(LocalVoiceEngines.FindApplication(engine, roots, "macos"), Is.EqualTo(bundles[0]));
        }
    }

    [AvaloniaTest]
    public async Task Refresh_rediscovers_moved_or_removed_applications_but_keeps_user_selection()
    {
        using var server = new HttpVoiceEngineTests.EngineServer { FailDiscovery = true };
        string? installedPath = "/original/app";
        using var model = new LocalEngineSetupViewModel([LocalVoiceEngines.Known[0] with { Url = server.Url }], _ => installedPath);
        var entry = model.Engines[0];
        await model.Refresh();
        Assert.That(entry.ApplicationPath.Value, Is.EqualTo(installedPath));
        installedPath = "/moved/app";
        await model.Refresh();
        Assert.That(entry.ApplicationPath.Value, Is.EqualTo(installedPath));
        installedPath = null;
        await model.Refresh();
        Assert.That(entry.ApplicationPath.Value, Is.Null);
        Assert.That(entry.CanUse.Value, Is.False);
        model.SelectApplication(entry, "/chosen/app");
        installedPath = "/newly/installed/app";
        await model.Refresh();
        Assert.That(entry.ApplicationPath.Value, Is.EqualTo("/chosen/app"));
        Assert.That(entry.CanUse.Value, Is.True);
    }

    [AvaloniaTest]
    [TestCase("delete", false)]
    [TestCase("move", false)]
    [TestCase("replace", false)]
    [TestCase("create", false)]
    [TestCase("delete", true)]
    [TestCase("move", true)]
    [TestCase("replace", true)]
    [TestCase("create", true)]
    public async Task Registration_respects_connection_changes_during_discovery(string change, bool starting)
    {
        using var server = new HttpVoiceEngineTests.EngineServer
        {
            FailDiscovery = starting,
            PauseAt = starting ? null : "/tts/speakers"
        };
        var seed = new VoiceEngineConnection { Url = server.Url, Name = "Existing engine" };
        if (change != "create") VoiceEngineStore.Save(seed);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var model = new LocalEngineSetupViewModel([LocalVoiceEngines.Known[0] with { Url = server.Url }],
            _ => null, _ => started.TrySetResult());
        model.SelectApplication(model.Engines[0], "/installed/run");
        Task<bool> registration = model.Use(model.Engines[0]);
        await (starting ? started.Task : server.Paused.Task).WaitAsync(TimeSpan.FromSeconds(5));
        var current = change == "move" ? seed with { Url = "http://127.0.0.1:59999/", Name = "Moved engine" }
            : seed with { Id = "replacement-engine", Name = "Renamed engine" };
        if (change == "create") current = current with { Url = server.Url.Replace("127.0.0.1", "localhost") };
        if (change is "delete" or "replace") VoiceEngineStore.Delete(seed.Id);
        if (change != "delete") VoiceEngineStore.Save(current);
        server.FailDiscovery = false;
        server.Resume.TrySetResult();
        bool success = change is "replace" or "create";
        Assert.That(await registration, Is.EqualTo(success), model.Message.Value);
        var saved = VoiceEngineStore.Load();
        if (change == "delete")
        {
            Assert.That(saved, Is.Empty);
            Assert.That(model.Message.Value, Does.Contain("削除"));
            Assert.That(await model.Use(model.Engines[0]), Is.True, "Explicitly retrying can add a fresh connection.");
            Assert.That(model.SelectedConnection!.Id, Is.Not.EqualTo(seed.Id));
            Assert.That(VoiceEngineStore.Load(), Has.Length.EqualTo(1));
        }
        else
        {
            Assert.That(saved, Has.Length.EqualTo(1));
            Assert.That(saved[0].Id, Is.EqualTo(current.Id));
            Assert.That(saved[0].Url, Is.EqualTo(current.Url));
            Assert.That(saved[0].Name, Is.EqualTo(current.Name));
            if (success) Assert.That(model.SelectedConnection!.Id, Is.EqualTo(current.Id));
            else Assert.That(model.Message.Value, Does.Contain("変更"));
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
    [TestCase(false)]
    [TestCase(true)]
    public async Task Search_is_read_only_and_registration_reuses_existing_preset_identity(bool localhost)
    {
        using var server = new HttpVoiceEngineTests.EngineServer(aivis: true);
        int launches = 0;
        using var model = new LocalEngineSetupViewModel([LocalVoiceEngines.Known[1] with { Url = server.Url }],
            _ => "/an/installed/app", _ => launches++);
        await model.Refresh();
        Assert.That(VoiceEngineStore.Load(), Is.Empty);
        Assert.That(launches, Is.Zero);
        var existing = model.Engines[0].Connection! with { Id = "preset-engine-id", Name = "My engine" };
        if (localhost) existing = existing with { Url = server.Url.Replace("127.0.0.1", "localhost") };
        VoiceEngineStore.Save(existing);
        server.ManifestStatus = 404;
        server.VersionStatus = 501;
        await model.Refresh();
        Assert.That(await model.Use(model.Engines[0]), Is.True, model.Message.Value);
        Assert.That(model.SelectedConnection!.Id, Is.EqualTo(existing.Id));
        Assert.That(model.SelectedConnection.Name, Is.EqualTo(existing.Name));
        Assert.That(model.SelectedConnection.Url, Is.EqualTo(existing.Url));
        Assert.That(model.SelectedConnection.EngineUuid, Is.EqualTo(existing.EngineUuid));
        Assert.That(model.SelectedConnection.Version, Is.EqualTo(existing.Version));
        Assert.That(model.SelectedConnection.Capabilities.TempoDynamics, Is.True);
        Assert.That(VoiceEngineStore.Load(), Has.Length.EqualTo(1));
        Assert.That(launches, Is.Zero, "An engine that is already responding must not be started again.");
    }

    [AvaloniaTest]
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task Slow_engine_discovery_waits_for_a_response_and_can_be_cancelled(bool starting, bool cancel)
    {
        using var server = new HttpVoiceEngineTests.EngineServer { FailDiscovery = starting, PauseAt = "/tts/speakers" };
        int launches = 0;
        using var model = new LocalEngineSetupViewModel([LocalVoiceEngines.Known[0] with { Url = server.Url }],
            _ => null, _ => { launches++; server.FailDiscovery = false; });
        model.SelectApplication(model.Engines[0], "/installed/run");
        Task<bool> registration = model.Use(model.Engines[0]);
        await server.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (cancel) model.Dispose();
        server.Resume.TrySetResult();
        Assert.That(await registration.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(!cancel), model.Message.Value);
        Assert.That(launches, Is.EqualTo(starting ? 1 : 0), "Only a stopped engine needs to be launched.");
        if (!cancel)
            Assert.That(server.DiscoveryCount, Is.EqualTo(starting ? 2 : 1),
                "A slow discovery response must be allowed to complete without retrying it.");
        Assert.That(VoiceEngineStore.Load().Length, Is.EqualTo(cancel ? 0 : 1));
        Assert.That(model.IsBusy.Value, Is.False);
    }

    [TestCase("http://localhost:50021/tts/", true)]
    [TestCase("http://localhost:50022/tts/", false)]
    [TestCase("http://localhost:50021/other/", false)]
    [TestCase("https://localhost:50021/tts/", false)]
    [TestCase("http://example.com:50021/tts/", false)]
    [TestCase("http://[::1]:50021/tts/", false)]
    public void Registration_matches_loopback_aliases_without_merging_other_endpoints(string url, bool matches)
    {
        var existing = new VoiceEngineConnection { Url = url, Name = "Saved engine" };
        VoiceEngineStore.Save(existing);
        var discovered = new VoiceEngineConnection { Url = "http://127.0.0.1:50021/tts/", Name = "Detected engine" };
        var registered = VoiceEngineStore.RegisterDiscovered(discovered, allowCreate: true);
        Assert.That(registered.Id, Is.EqualTo(matches ? existing.Id : discovered.Id));
        Assert.That(registered.Url, Is.EqualTo(matches ? existing.Url : discovered.Url));
        Assert.That(VoiceEngineStore.Load(), Has.Length.EqualTo(matches ? 1 : 2));
        Assert.That(VoiceEngineStore.FindByUrl(VoiceEngineStore.Load(), existing.Url)!.Id, Is.EqualTo(existing.Id));
        Assert.That(VoiceEngineStore.FindByUrl(VoiceEngineStore.Load(), discovered.Url)!.Id, Is.EqualTo(registered.Id));
    }

    [Test]
    public void Registration_prefers_an_existing_exact_url_over_a_loopback_alias()
    {
        var alias = new VoiceEngineConnection { Url = "http://localhost:50021/" };
        var exact = new VoiceEngineConnection { Url = "http://127.0.0.1:50021/" };
        VoiceEngineStore.Save(alias);
        VoiceEngineStore.Save(exact);
        Assert.That(VoiceEngineStore.FindByUrl(VoiceEngineStore.Load(), exact.Url)!.Id, Is.EqualTo(exact.Id));
        var registered = VoiceEngineStore.RegisterDiscovered(new() { Url = exact.Url }, allowCreate: true);
        Assert.That(registered.Id, Is.EqualTo(exact.Id));
        Assert.That(VoiceEngineStore.Load(), Has.Length.EqualTo(2));
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
        using var ready = new HttpVoiceEngineTests.EngineServer(aivis: true) { PauseAt = "/tts/speakers" };
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
            await ready.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromSeconds(3));
            ready.Resume.TrySetResult();
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
            Assert.That(model.Engines[0].ApplicationPath.Value, Is.Null);
            Assert.That(button.IsEnabled, Is.True, "A slow running engine must remain connectable without an application path.");
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
