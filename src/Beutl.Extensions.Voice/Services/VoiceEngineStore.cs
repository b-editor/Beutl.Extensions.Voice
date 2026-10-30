using System.Text.Json;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;

namespace Beutl.Extensions.Voice.Services;

public static class VoiceEngineStore
{
    private static readonly object s_lock = new();
    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true };
    public static string GlobalPath => Path.Combine(BeutlEnvironment.GetHomeDirectoryPath(), "voice", "engines.json");
    public static event Action? Changed;

    public static VoiceEngineConnection[] Load()
    {
        lock (s_lock)
            return File.Exists(GlobalPath)
                ? JsonSerializer.Deserialize<VoiceEngineConnection[]>(File.ReadAllText(GlobalPath))
                    ?? throw new JsonException("接続先を読み込めませんでした。")
                : [];
    }

    public static void Save(VoiceEngineConnection connection, bool allowCreate = true) => Change(connections =>
    {
        if (connection.IsCore) throw new InvalidOperationException("内蔵エンジンの接続先は変更できません。");
        int index = connections.FindIndex(x => x.Id == connection.Id);
        if (index < 0)
        {
            if (!allowCreate) throw new InvalidOperationException("接続先が削除されています。");
            connections.Add(connection);
        }
        else connections[index] = connection;
    });

    internal static void UpdateMetadata(VoiceEngineConnection connection) => Change(connections =>
    {
        int index = connections.FindIndex(x => x.Id == connection.Id);
        if (index < 0) throw new InvalidOperationException("接続先が削除されています。");
        var current = connections[index];
        if (current.Url != connection.Url)
            throw new InvalidOperationException("接続先が変更されています。もう一度再読み込みしてください。");
        connections[index] = current with
        {
            EngineUuid = connection.EngineUuid, Version = connection.Version,
            Capabilities = connection.Capabilities, Voices = connection.Voices
        };
    });

    public static void Delete(string id) => Change(connections => connections.RemoveAll(x => x.Id == id));

    private static void Change(Action<List<VoiceEngineConnection>> change)
    {
        lock (s_lock)
        {
            var connections = Load().ToList();
            change(connections);
            Directory.CreateDirectory(Path.GetDirectoryName(GlobalPath)!);
            string temporary = GlobalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(connections, s_options));
                File.Move(temporary, GlobalPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Changed?.Invoke();
    }
}
