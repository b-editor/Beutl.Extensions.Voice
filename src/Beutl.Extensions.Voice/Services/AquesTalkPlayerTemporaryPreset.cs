using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Logging;
using Microsoft.Extensions.Logging;

namespace Beutl.Extensions.Voice.Services;

// AquesTalkPlayer's command line takes a voice only by preset name, so a voice tuned in Beutl is written to the app's
// preset file as a preset of Beutl's own for one run, and the file is put back as it was.
internal static class AquesTalkPlayerTemporaryPreset
{
    public const string Name = "Beutl_temp";

    private static readonly ILogger s_logger = Log.CreateLogger(typeof(AquesTalkPlayerTemporaryPreset));
    private static readonly JsonSerializerOptions s_json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<T> UseAsync<T>(string path, AquesTalkPlayerVoice voice, Func<string, Task<T>> run,
        CancellationToken cancellationToken)
    {
        if (!voice.HasValidVoiceType)
            throw new InvalidOperationException($"AquesTalkPlayerの声種ではないため、調声を反映できません（{voice.StyleName}）。話者を選び直してください。");
        // Another Beutl must not replace the preset between this one writing it and the app reading it.
        using var gate = await LockAsync(cancellationToken);
        byte[]? original = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
        // A preset left behind by a run that never finished is not put back.
        byte[]? cleaned = original == null ? null : Remove(original, path) ?? original;
        byte[] written = Add(cleaned, voice, path);
        try { Write(path, written); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"AquesTalkPlayerのプリセットファイルに書き込めないため、調声を反映できません（{path}）: {ex.Message}", ex);
        }
        try { return await run(Name); }
        finally { Restore(path, written, cleaned); }
    }

    // Unchanged since it was written, the file goes back to its bytes before; changed by the app meanwhile, it loses
    // only the temporary preset.
    private static void Restore(string path, byte[] written, byte[]? cleaned)
    {
        try
        {
            byte[]? current = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (current == null) return;
            if (current.AsSpan().SequenceEqual(written))
            {
                if (cleaned == null) File.Delete(path);
                else Write(path, cleaned);
            }
            else if (Remove(current, path) is { } removed) Write(path, removed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            s_logger.LogWarning(ex, "Could not remove the temporary preset from {Path}", path);
        }
    }

    private static void Write(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<FileStream> LockAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(BeutlEnvironment.GetHomeDirectoryPath(), "voice", "aquestalkplayer.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(50, cancellationToken); }
            catch (IOException ex) { throw new IOException("別のBeutlがAquesTalkPlayerで音声を生成しています。少し待ってからもう一度操作してください。", ex); }
        }
    }

    // The preset file without the temporary preset, or null when it has none.
    internal static byte[]? Remove(byte[] data, string path)
    {
        if (AquesTalkPlayerPresets.IsJson(path))
        {
            var presets = ParseJson(data);
            return presets.RemoveAll(IsTemporary) == 0 ? null : Encoding.UTF8.GetBytes(presets.ToJsonString(s_json));
        }
        var (text, encoding, bom) = AquesTalkPlayerPresets.DecodeCsv(data);
        var lines = SplitLines(text, out string newline);
        int removed = lines.RemoveAll(line => AquesTalkPlayerPresets.ReadCsv(line) is [[var name, ..]] && name == Name);
        return removed == 0 ? null : EncodeCsv(string.Join(newline, lines), encoding, bom);
    }

    internal static byte[] Add(byte[]? data, AquesTalkPlayerVoice voice, string path)
    {
        if (AquesTalkPlayerPresets.IsJson(path))
        {
            // Without a saved file the app speaks the presets it ships with, which the file then has to list.
            var presets = data == null
                ? new JsonArray([.. AquesTalkPlayerPresets.Defaults.Select(preset => AquesTalkPlayerPresets.ToJson(preset.Name, preset.Voice, preset.Memo))])
                : ParseJson(data);
            presets.Add(AquesTalkPlayerPresets.ToJson(Name, voice, ""));
            return Encoding.UTF8.GetBytes(presets.ToJsonString(s_json));
        }
        if (data == null) throw new FileNotFoundException("AquesTalkPlayerのプリセットファイルが見つかりません。", path);
        var (text, encoding, bom) = AquesTalkPlayerPresets.DecodeCsv(data);
        var lines = SplitLines(text, out string newline);
        var template = lines.Select(line => AquesTalkPlayerPresets.ReadCsv(line).FirstOrDefault() ?? [])
            .FirstOrDefault(AquesTalkPlayerPresets.IsPresetRow)
            ?? throw new FormatException("AquesTalkPlayerのプリセットファイルの形式を読み取れません。");
        string row = string.Join(",", AquesTalkPlayerPresets.ToCsvFields(template, Name, voice).Select(Quote));
        // Inserted before the line break that ends the file, so that the file still ends with one.
        int end = lines.Count > 0 && lines[^1].Length == 0 ? lines.Count - 1 : lines.Count;
        lines.Insert(end, row);
        if (end == lines.Count - 1) lines.Add("");
        return EncodeCsv(string.Join(newline, lines), encoding, bom);
    }

    private static JsonArray ParseJson(byte[] data) =>
        JsonNode.Parse(data) as JsonArray ?? throw new JsonException("AquesTalkPlayerのプリセットの一覧ではありません。");

    private static bool IsTemporary(JsonNode? item) =>
        item is JsonObject preset && preset["name"] is JsonValue name && name.TryGetValue(out string? value) && value == Name;

    private static List<string> SplitLines(string text, out string newline)
    {
        newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return [.. text.Split(newline)];
    }

    private static byte[] EncodeCsv(string text, Encoding encoding, bool bom) =>
        bom ? [0xEF, 0xBB, 0xBF, .. encoding.GetBytes(text)] : encoding.GetBytes(text);

    private static string Quote(string field) => field.IndexOfAny([',', '"', '\r', '\n']) < 0 ? field
        : "\"" + field.Replace("\"", "\"\"") + "\"";
}
