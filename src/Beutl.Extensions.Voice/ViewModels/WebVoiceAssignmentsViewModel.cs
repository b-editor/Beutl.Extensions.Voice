using Avalonia.Threading;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.Views;
using Beutl.ProjectSystem;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public sealed record WebVoiceAssignmentItem(WebVoiceAssignment Assignment, string Title, string Details);

// The characters and captions remembered for the voices of text-to-speech sites, shown in the speech tab's settings.
public sealed class WebVoiceAssignmentsViewModel : IDisposable
{
    private readonly Scene _scene;
    private readonly Func<string> _path;
    private readonly Func<WebVoiceAssignmentViewModel, string, CancellationToken, Task<WebVoiceAssignment?>> _choose;

    internal WebVoiceAssignmentsViewModel(Scene scene, Func<string>? path = null,
        Func<WebVoiceAssignmentViewModel, string, CancellationToken, Task<WebVoiceAssignment?>>? choose = null)
    {
        _scene = scene;
        _path = path ?? (() => WebVoiceAssignmentStore.GlobalPath);
        _choose = choose ?? WebVoiceAssignmentDialog.ShowAsync;
        WebVoiceAssignmentStore.Changed += OnStoreChanged;
        Refresh();
    }

    public ReactivePropertySlim<WebVoiceAssignmentItem[]> Items { get; } = new([]);
    public ReactivePropertySlim<bool> HasItems { get; } = new();
    public ReactivePropertySlim<string> Message { get; } = new("");

    public void Refresh()
    {
        try
        {
            Items.Value = WebVoiceAssignmentStore.Load(_path()).Select(ToItem).OrderBy(x => x.Title, StringComparer.CurrentCulture).ToArray();
            Message.Value = "";
        }
        catch (Exception ex)
        {
            Items.Value = [];
            Message.Value = $"サイトの声の割り当てを読み込めませんでした: {ex.Message}";
        }
        HasItems.Value = Items.Value.Length > 0;
    }

    public async Task Change(WebVoiceAssignmentItem item)
    {
        var current = item.Assignment;
        // A site this version no longer knows keeps its id as its name.
        var site = WebVoiceSites.Find(current.SiteId) ?? new WebVoiceSite(current.SiteId, current.SiteId, [], "");
        var model = new WebVoiceAssignmentViewModel(_scene, site, current.VoiceId, current.VoiceName, current);
        if (await _choose(model, "保存", CancellationToken.None) is not { } changed) return;
        try
        {
            WebVoiceAssignmentStore.Save(_path(), changed);
        }
        catch (Exception ex)
        {
            Message.Value = $"割り当てを保存できませんでした: {ex.Message}";
        }
    }

    public void Delete(WebVoiceAssignmentItem item)
    {
        try
        {
            WebVoiceAssignmentStore.Delete(_path(), item.Assignment.SiteId, item.Assignment.VoiceId);
        }
        catch (Exception ex)
        {
            Message.Value = $"割り当てを削除できませんでした: {ex.Message}";
        }
    }

    private void OnStoreChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private static WebVoiceAssignmentItem ToItem(WebVoiceAssignment assignment)
    {
        string site = WebVoiceSites.Find(assignment.SiteId)?.Name ?? assignment.SiteId;
        string character = assignment.LipSync is { } lipSync ? Path.GetFileNameWithoutExtension(lipSync.PsdPath) : "連携しない";
        return new(assignment, $"{site}: {WebVoiceSites.DescribeVoice(assignment.VoiceName, assignment.VoiceId)}",
            $"立ち絵: {character} / 字幕: {assignment.Subtitle?.Name ?? "字幕なし"}");
    }

    public void Dispose() => WebVoiceAssignmentStore.Changed -= OnStoreChanged;
}
