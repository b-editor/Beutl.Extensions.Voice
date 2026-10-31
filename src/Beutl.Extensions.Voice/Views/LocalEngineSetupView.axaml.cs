using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Beutl.Extensions.Voice.ViewModels;

namespace Beutl.Extensions.Voice.Views;

public partial class LocalEngineSetupView : UserControl
{
    public event EventHandler? EngineConnected;
    private event Action? CloseRequested;
    private bool _needsNewSession;

    public LocalEngineSetupView() : this(new LocalEngineSetupViewModel()) { }

    public bool ShowCloseButton { get => CloseSetupButton.IsVisible; set => CloseSetupButton.IsVisible = value; }

    internal LocalEngineSetupView(LocalEngineSetupViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    internal static void ShowIn(ContentControl host, VoiceEditorViewModel? editor = null, Action? closed = null,
        LocalEngineSetupViewModel? model = null)
    {
        if (host.Content is LocalEngineSetupView) return;
        var view = new LocalEngineSetupView(model ?? new LocalEngineSetupViewModel());
        string? previousEngineId = editor?.SelectedEngine.Value?.Id;
        void Close()
        {
            (view.DataContext as LocalEngineSetupViewModel)?.Dispose();
            host.Content = null;
            host.IsVisible = false;
            closed?.Invoke();
        }
        view.CloseRequested += Close;
        view.EngineConnected += (_, _) =>
        {
            if (view.DataContext is LocalEngineSetupViewModel { SelectedConnection: { } selected } && editor != null
                && ReferenceEquals(host.DataContext, editor) && editor.SelectedEngine.Value?.Id == previousEngineId)
            {
                editor.RefreshEngines();
                editor.SelectedEngine.Value = editor.Engines.Value.FirstOrDefault(x => x.Id == selected.Id);
            }
            Close();
        };
        host.Content = view;
        host.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(host.Content, view)) view.BringIntoView(new Rect(0, 0, view.Bounds.Width, 1));
        }, DispatcherPriority.Loaded);
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        await RefreshSession();
    }

    internal async Task RefreshSession()
    {
        if (_needsNewSession) DataContext = new LocalEngineSetupViewModel();
        _needsNewSession = false;
        if (DataContext is LocalEngineSetupViewModel model) await model.Refresh();
    }

    internal void CloseSession()
    {
        (DataContext as LocalEngineSetupViewModel)?.Dispose();
        _needsNewSession = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CloseSession();
        base.OnDetachedFromVisualTree(e);
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private async void UseEngineClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LocalEngineSetupViewModel model && sender is Button { DataContext: LocalEngineCandidateViewModel entry }
            && await model.Use(entry)) EngineConnected?.Invoke(this, EventArgs.Empty);
    }

    private async void SelectApplicationClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LocalEngineSetupViewModel model
            || sender is not Button { DataContext: LocalEngineCandidateViewModel entry }
            || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = entry.Engine.ApplicationName + "のアプリ・エンジンを選択",
                AllowMultiple = false,
                FileTypeFilter = [new("アプリ・実行ファイル")
                {
                    Patterns = OperatingSystem.IsWindows() ? ["*.exe"] : ["*"],
                    AppleUniformTypeIdentifiers = ["com.apple.application-bundle", "public.unix-executable"]
                }]
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) model.SelectApplication(entry, path);
        }
        catch (Exception ex) { model.Message.Value = $"アプリを選択できませんでした: {ex.Message}"; }
    }
}
