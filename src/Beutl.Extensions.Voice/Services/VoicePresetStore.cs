using System.Text.Json;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;

namespace Beutl.Extensions.Voice.Services;

public static class VoicePresetStore
{
    private static readonly object s_lock = new();
    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true };

    public static string GlobalPath => Path.Combine(BeutlEnvironment.GetHomeDirectoryPath(), "voice", "presets.json");

    public static VoicePreset[] Load(string path)
    {
        lock (s_lock)
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<VoicePreset[]>(File.ReadAllText(path))
                ?? throw new JsonException("プリセットファイルを読み込めませんでした。");
        }
    }

    public static void Save(string path, VoicePreset preset) => Change(path, presets =>
    {
        int index = presets.FindIndex(x => x.Id == preset.Id);
        if (index < 0) presets.Add(preset);
        else presets[index] = preset;
    });

    public static void Update(string path, Guid id, Func<VoicePreset, VoicePreset> update) => Change(path, presets =>
    {
        int index = presets.FindIndex(x => x.Id == id);
        if (index < 0) throw new InvalidOperationException("プリセットは削除されています。一覧を再読み込みしてください。");
        presets[index] = update(presets[index]);
    });

    public static void Delete(string path, Guid id) => Change(path, presets => presets.RemoveAll(x => x.Id == id));

    private static void Change(string path, Action<List<VoicePreset>> change)
    {
        lock (s_lock)
        {
            // Re-read before each edit so another open scene's presets are retained.
            var presets = Load(path).ToList();
            change(presets);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(presets, s_options));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
