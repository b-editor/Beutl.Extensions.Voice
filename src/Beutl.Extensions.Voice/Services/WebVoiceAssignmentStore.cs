using System.Text.Json;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;

namespace Beutl.Extensions.Voice.Services;

internal static class WebVoiceAssignmentStore
{
    private static readonly object s_lock = new();
    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true };

    public static string GlobalPath => Path.Combine(BeutlEnvironment.GetHomeDirectoryPath(), "voice", "web-voices.json");

    public static event Action? Changed;

    public static WebVoiceAssignment[] Load(string path)
    {
        lock (s_lock)
        {
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<WebVoiceAssignment[]>(stream)
                ?? throw new JsonException("サイトの声の割り当てを読み込めませんでした。");
        }
    }

    public static WebVoiceAssignment? Find(string path, string siteId, string voiceId) =>
        Load(path).FirstOrDefault(x => x.IsFor(siteId, voiceId));

    public static void Save(string path, WebVoiceAssignment assignment) => Change(path, assignments =>
    {
        int index = assignments.FindIndex(x => x.IsFor(assignment.SiteId, assignment.VoiceId));
        if (index < 0) assignments.Add(assignment);
        else assignments[index] = assignment;
    });

    public static void Delete(string path, string siteId, string voiceId) =>
        Change(path, assignments => assignments.RemoveAll(x => x.IsFor(siteId, voiceId)));

    private static void Change(string path, Action<List<WebVoiceAssignment>> change)
    {
        lock (s_lock)
        {
            // Re-read before each edit so an assignment made in another editor is kept.
            var assignments = Load(path).ToList();
            change(assignments);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(assignments, s_options));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        Changed?.Invoke();
    }
}
