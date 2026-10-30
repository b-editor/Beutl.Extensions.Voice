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
        {
            if (!File.Exists(GlobalPath)) return [];
            // An atomic replacement by another process must not invalidate an open reader.
            using var stream = new FileStream(GlobalPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<VoiceEngineConnection[]>(stream)
                ?? throw new JsonException("接続先を読み込めませんでした。");
        }
    }

    public static void Save(VoiceEngineConnection connection, bool allowCreate = true, string? expectedUrl = null) => Change(connections =>
    {
        if (connection.IsCore) throw new InvalidOperationException("内蔵エンジンの接続先は変更できません。");
        int index = FindConnection(connections, connection.Id, expectedUrl);
        if (index < 0)
        {
            if (!allowCreate) throw new InvalidOperationException("接続先が削除されています。");
            connections.Add(connection);
        }
        else connections[index] = connection;
    });

    internal static void UpdateMetadata(VoiceEngineConnection connection) => Change(connections =>
    {
        int index = FindConnection(connections, connection.Id, connection.Url);
        if (index < 0) throw new InvalidOperationException("接続先が削除されています。");
        var current = connections[index];
        connections[index] = current with
        {
            EngineUuid = connection.EngineUuid, Version = connection.Version,
            Capabilities = connection.Capabilities, Voices = connection.Voices
        };
    });

    public static void Delete(string id) => Change(connections => connections.RemoveAll(x => x.Id == id));

    internal static VoiceEngineConnection RegisterDiscovered(VoiceEngineConnection connection)
    {
        Change(connections =>
        {
            int index = connections.FindIndex(x => x.Url == connection.Url);
            if (index < 0) connections.Add(connection);
            else
            {
                connection = connection with { Id = connections[index].Id, Name = connections[index].Name };
                connections[index] = connection;
            }
        });
        return connection;
    }

    private static int FindConnection(List<VoiceEngineConnection> connections, string id, string? expectedUrl)
    {
        int index = connections.FindIndex(x => x.Id == id);
        if (index >= 0 && expectedUrl != null && connections[index].Url != expectedUrl)
            throw new InvalidOperationException("接続先が変更されています。もう一度操作してください。");
        return index;
    }

    private static void Change(Action<List<VoiceEngineConnection>> change)
    {
        lock (s_lock)
        {
            // Include other Beutl processes even when launched in separate login/shell sessions.
            using var mutex = new Mutex(false, "Beutl.Extensions.Voice.Engines",
                new NamedWaitHandleOptions { CurrentSessionOnly = false });
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                    throw new IOException("別のBeutlが接続先を保存しています。少し待ってからもう一度操作してください。");
            }
            catch (AbandonedMutexException)
            {
                // Ownership is acquired; an interrupted writer leaves the atomically replaced JSON intact.
            }
            try
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
            finally { mutex.ReleaseMutex(); }
        }
        Changed?.Invoke();
    }
}
