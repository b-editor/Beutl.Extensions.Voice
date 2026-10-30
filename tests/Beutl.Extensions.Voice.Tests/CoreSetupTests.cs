using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.Serialization;
using Beutl.Services;
using FluentAvalonia.UI.Controls;

namespace Beutl.Extensions.Voice.Tests;

public class CoreSetupTests
{
    private string _directory = null!;
    private string? _previousHome;
    private VoiceVoxLoader? _previousLoader;
    private JsonObject _previousSettings = null!;
    private bool _previousShouldPrompt;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "voice-setup-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _previousHome = Environment.GetEnvironmentVariable("BEUTL_HOME");
        Environment.SetEnvironmentVariable("BEUTL_HOME", _directory);
        _previousLoader = TtsLoader.VoiceVoxLoader.Value;
        TtsLoader.VoiceVoxLoader.Value = null;
        _previousSettings = CoreSerializer.SerializeToJsonObject(TtsLoader.Preferences);
        _previousShouldPrompt = TtsLoader.Preferences.ShouldPrompt;
        TtsLoader.Preferences.HideCoreInstallPrompt.Value = false;
        TtsLoader.Preferences.BeginSession();
    }

    [TearDown]
    public void TearDown()
    {
        CoreSerializer.PopulateFromJsonObject(TtsLoader.Preferences, _previousSettings);
        TtsLoader.Preferences.BeginSession();
        if (!_previousShouldPrompt) TtsLoader.Preferences.Skip(false);
        TtsLoader.VoiceVoxLoader.Value = _previousLoader;
        Environment.SetEnvironmentVariable("BEUTL_HOME", _previousHome);
        Directory.Delete(_directory, true);
    }

    [AvaloniaTest]
    public void Only_permanent_opt_out_is_saved_in_extension_settings()
    {
        Assert.That(new TtsLoader().Settings, Is.SameAs(TtsLoader.Preferences));
        var settings = new VoiceExtensionSettings();
        int saves = 0;
        settings.ConfigurationChanged += (_, _) => saves++;
        settings.Skip(false);
        Assert.That(settings.ShouldPrompt, Is.False);
        Assert.That(saves, Is.Zero, "Skipping this session does not change persisted settings.");
        var restored = new VoiceExtensionSettings();
        CoreSerializer.PopulateFromJsonObject(restored, CoreSerializer.SerializeToJsonObject(settings));
        Assert.That(restored.ShouldPrompt, Is.True);

        settings.Skip(true);
        Assert.That(saves, Is.EqualTo(1));
        CoreSerializer.PopulateFromJsonObject(restored, CoreSerializer.SerializeToJsonObject(settings));
        restored.BeginSession();
        Assert.That(restored.HideCoreInstallPrompt.Value, Is.True);
        Assert.That(restored.ShouldPrompt, Is.False);
        restored.HideCoreInstallPrompt.Value = false;
        Assert.That(restored.ShouldPrompt, Is.True, "The extension settings can enable prompts again.");
    }

    [AvaloniaTest]
    [TestCase("今はスキップ", false)]
    [TestCase("今後表示しない", true)]
    [TestCase(null, false)]
    public void Startup_notification_respects_skip_and_persistent_opt_out(string? action, bool permanent)
    {
        var notification = TtsLoader.CreateInstallNotification()!;
        Assert.That(notification.Type, Is.EqualTo(NotificationType.Information));
        if (action != null) notification.Actions!.Single(x => x.Text == action).Callback();
        notification.OnClose!();
        Assert.That(TtsLoader.CreateInstallNotification(), Is.Null);
        Assert.That(TtsLoader.Preferences.HideCoreInstallPrompt.Value, Is.EqualTo(permanent));
        TtsLoader.Preferences.BeginSession();
        Assert.That(TtsLoader.CreateInstallNotification() != null, Is.EqualTo(!permanent));
        VoiceEngineStore.Save(new VoiceEngineConnection { Name = "外部エンジン", Url = "http://localhost:50021/" });
        Assert.That(TtsLoader.CreateInstallNotification(), Is.Null);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public void Unreadable_connections_keep_optional_setup_available_and_respect_opt_out(bool malformed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VoiceEngineStore.GlobalPath)!);
        string contents = malformed ? "{" : "[]";
        File.WriteAllText(VoiceEngineStore.GlobalPath, contents);
        Notification? notification;
        using (var fileLock = malformed ? null : new FileStream(VoiceEngineStore.GlobalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            notification = TtsLoader.CreateInstallNotification();
        Assert.That(notification, Is.Not.Null);
        Assert.That(notification!.Message, Does.Contain("接続設定を読み込めませんでした"));
        Assert.That(notification.Message, Does.Contain("必要な場合"));
        Assert.That(File.ReadAllText(VoiceEngineStore.GlobalPath), Is.EqualTo(contents));
        notification.OnClose!();
        Assert.That(TtsLoader.CreateInstallNotification(), Is.Null);
        TtsLoader.Preferences.BeginSession();
        TtsLoader.Preferences.HideCoreInstallPrompt.Value = true;
        Assert.That(TtsLoader.CreateInstallNotification(), Is.Null);
    }

    [AvaloniaTest]
    [TestCase(320, false)]
    [TestCase(460, true)]
    public async Task Script_core_assignments_offer_installation_and_retry_after_failure(int width, bool permanent)
    {
        var connection = new VoiceEngineConnection { Name = "HTTP", Url = "http://localhost:50021/" };
        VoiceEngineStore.Save(connection);
        var core = new VoicePreset { Name = "Core", Settings = new() };
        var http = new VoicePreset { Name = "HTTP", Settings = new() { EngineId = connection.Id } };
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, core);
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, http);
        using var editor = new TestEditor(Path.Combine(_directory, "scene.bescene"));
        using var model = new ScriptImportViewModel(new(), editor);
        model.RawText.Value = "A: おはよう。\nB: こんにちは。";
        model.ImportText();
        model.Speakers[0].SelectedPreset.Value = http;
        model.Speakers[1].SelectedPreset.Value = http;
        Assert.That(model.ShowCoreInstallPrompt.Value, Is.False);
        model.Speakers[0].SelectedPreset.Value = core;
        Assert.That(model.ShowCoreInstallPrompt.Value, Is.True);
        var state = new JsonObject();
        model.WriteToJson(state);
        model.RawText.Value = "B: こんにちは。";
        model.ImportText();
        Assert.That(model.ShowCoreInstallPrompt.Value, Is.False, "Inactive assignments do not require Core.");
        model.ReadFromJson(state);
        Assert.That(model.ShowCoreInstallPrompt.Value, Is.True);
        model.Speakers[0].SelectedPreset.Value = http;
        Assert.That(model.ShowCoreInstallPrompt.Value, Is.False);
        model.Speakers[0].SelectedPreset.Value = core;

        var view = new ScriptImportView { DataContext = model };
        var window = new Window { Width = width, Height = 600, Content = view,
            RequestedThemeVariant = permanent ? ThemeVariant.Light : ThemeVariant.Dark };
        try
        {
            window.Show();
            await Settle();
            var prompt = view.FindControl<CoreInstallPromptView>("CoreInstallPrompt")!;
            Assert.That(prompt.IsEffectivelyVisible, Is.True);
            Capture(window, $"script-core-preset-{width}");
            prompt.FindControl<CheckBox>("NeverShowAgainCheckBox")!.IsChecked = permanent;
            prompt.FindControl<Button>("InstallCoreButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            var dialog = window.GetVisualDescendants().OfType<VoiceVoxInstallDialog>().Single();
            var installer = (VoiceVoxInstallDialogViewModel)dialog.DataContext!;
            Assert.That(installer.DoNotShowAgain.Value, Is.EqualTo(permanent));
            // The startup notification can already have dismissed itself after opening the installer.
            TtsLoader.Preferences.Skip(false);
            var primary = dialog.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "PrimaryButton");
            primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Assert.That(dialog.PrimaryButtonText, Is.EqualTo("インストール"));
            // Publish the installer's failure state without starting downloads.
            installer.Error.Value = "ダウンロード先に接続できませんでした。";
            installer.IsCompleted.Value = true;
            dialog.IsPrimaryButtonEnabled = false;
            dialog.CloseButtonText = "閉じる";
            await Settle();
            Capture(window, $"setup-failed-{width}");
            dialog.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "CloseButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Assert.That(model.ShowCoreInstallPrompt.Value, Is.EqualTo(!permanent));
            Assert.That(TtsLoader.Preferences.HideCoreInstallPrompt.Value, Is.EqualTo(permanent));
            if (!permanent)
            {
                prompt.FindControl<Button>("InstallCoreButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Settle();
                var retry = window.GetVisualDescendants().OfType<VoiceVoxInstallDialog>().Single();
                Assert.That(((VoiceVoxInstallDialogViewModel)retry.DataContext!).Error.Value, Is.Null.Or.Empty);
                retry.Hide();
                await Settle();
            }
        }
        finally
        {
            foreach (var dialog in window.GetVisualDescendants().OfType<VoiceVoxInstallDialog>().ToArray()) dialog.Hide();
            await Settle();
            window.Close();
        }
    }

    [AvaloniaTest]
    [TestCase(320, false)]
    [TestCase(460, true)]
    public async Task Tabs_share_skip_and_manual_setup_remains_available(int width, bool permanent)
    {
        using var editor = new TestEditor(Path.Combine(_directory, "scene.bescene"));
        using var speech = new TtsTabViewModel(new(), editor);
        using var script = new ScriptImportViewModel(new(), editor);
        var speechView = new TtsTabView { DataContext = speech };
        var scriptView = new ScriptImportView { DataContext = script };
        Control view = permanent ? scriptView : speechView;
        var window = new Window
        {
            Width = width, Height = 600, Content = view,
            RequestedThemeVariant = permanent ? ThemeVariant.Light : ThemeVariant.Dark
        };
        try
        {
            window.Show();
            await Settle();
            var prompt = view.FindControl<CoreInstallPromptView>("CoreInstallPrompt")!;
            Assert.That(prompt.IsEffectivelyVisible, Is.True);
            prompt.FindControl<CheckBox>("NeverShowAgainCheckBox")!.IsChecked = permanent;
            foreach (var button in prompt.GetVisualDescendants().OfType<Button>().Where(x => x.Name is "InstallCoreButton" or "SkipCoreButton"))
            {
                var point = button.TranslatePoint(default, window)!.Value;
                Assert.That(point.X, Is.GreaterThanOrEqualTo(0));
                Assert.That(point.X + button.Bounds.Width, Is.LessThanOrEqualTo(width));
                Assert.That(button.IsEffectivelyVisible, Is.True);
            }
            Capture(window, $"setup-prompt-{width}");
            prompt.FindControl<Button>("SkipCoreButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Assert.That(speech.ShowCoreInstallPrompt.Value, Is.False);
            Assert.That(script.ShowCoreInstallPrompt.Value, Is.False);
            Assert.That(prompt.IsVisible, Is.False);
            using (var reopened = new TtsTabViewModel(new(), editor))
                Assert.That(reopened.ShowCoreInstallPrompt.Value, Is.False);
            TtsLoader.Preferences.BeginSession();
            Assert.That(speech.ShowCoreInstallPrompt.Value, Is.EqualTo(!permanent));
            Assert.That(script.ShowCoreInstallPrompt.Value, Is.EqualTo(!permanent));
            TtsLoader.Preferences.Skip(false);

            window.Content = speechView;
            await Settle();
            var voiceView = speechView.FindControl<VoiceEditorView>("VoiceEditor")!;
            var menuButton = voiceView.FindControl<Button>("EngineMenuButton")!;
            var menu = (FAMenuFlyout)menuButton.Flyout!;
            menu.ShowAt(menuButton);
            await Settle();
            Assert.That(menu.Items.OfType<FAMenuFlyoutItem>().Single(x => x.Text == "接続先を追加…").IsEnabled, Is.True);
            menu.Hide();
            menu.Items.OfType<FAMenuFlyoutItem>().Single(x => x.Text == "VOICEVOX Coreをインストール…")
                .RaiseEvent(new RoutedEventArgs(FAMenuFlyoutItem.ClickEvent));
            await Settle();
            var dialog = window.GetVisualDescendants().OfType<VoiceVoxInstallDialog>().Single();
            var model = (VoiceVoxInstallDialogViewModel)dialog.DataContext!;
            Assert.That(model.DoNotShowAgain.Value, Is.EqualTo(permanent));
            Assert.That(dialog.CloseButtonText, Is.EqualTo("今はスキップ"));
            var content = dialog.FindControl<VoiceVoxInstallDialogContent>("Root")!;
            content.FindControl<CheckBox>("NeverShowAgainCheckBox")!.IsChecked = !permanent;
            Capture(window, $"setup-dialog-{width}");
            var close = dialog.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "CloseButton");
            if (permanent)
            {
                // Advancing to the download page must still permit skipping before any download starts.
                dialog.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "PrimaryButton")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Settle();
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else
            {
                close.Focus();
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            }
            await Settle();
            Assert.That(TopLevel.GetTopLevel(dialog), Is.Null, "Closing removes the dialog from the window overlay.");
            Assert.That(model.IsInstalling.Value, Is.False);
            Assert.That(Directory.Exists(Path.Combine(_directory, "voicevox")), Is.False);
            Assert.That(TtsLoader.Preferences.HideCoreInstallPrompt.Value, Is.EqualTo(!permanent));
            Assert.That(speech.ShowCoreInstallPrompt.Value, Is.False);
            TtsLoader.Preferences.BeginSession();
            Assert.That(speech.ShowCoreInstallPrompt.Value, Is.EqualTo(permanent));
        }
        finally
        {
            foreach (var dialog in window.GetVisualDescendants().OfType<VoiceVoxInstallDialog>().ToArray()) dialog.Hide();
            await Settle();
            window.Close();
        }
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
