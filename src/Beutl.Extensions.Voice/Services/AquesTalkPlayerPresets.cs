using System.Text;
using System.Text.Json;
using Beutl.Extensions.Voice.Models;

namespace Beutl.Extensions.Voice.Services;

// A named voice in AquesTalkPlayer, which its command line takes by name.
internal sealed record AquesTalkPlayerPreset(string Name, AquesTalkPlayerVoice Voice, string Memo = "")
{
    public VoiceMetadata ToVoice() => new()
    {
        Name = Name, SpeakerUuid = Name, Version = "", AquesTalkPlayer = Voice,
        Styles = [new VoiceStyle { Id = 0, Name = $"{Voice.Engine} {Voice.Voice}".Trim() }]
    };
}

// The Windows version keeps its presets in a Shift_JIS CSV next to the executable. The macOS version keeps them as JSON
// in its Application Support folder, and both use the presets they ship with until one is first saved.
internal static class AquesTalkPlayerPresets
{
    internal static readonly Encoding ShiftJis = CodePagesEncodingProvider.Instance.GetEncoding(932)!;

    public static IReadOnlyList<AquesTalkPlayerPreset> Defaults { get; } =
    [
        Shipped("デフォルト", "AquesTalk10", "F1E", memo: "AquesTalk10 女声"),
        Shipped("れいむ", "AquesTalk1", "f1", flat: true, memo: "ゆっくり霊夢"),
        Shipped("まりさ", "AquesTalk1", "f2", flat: true, memo: "ゆっくり魔理沙"),
        Shipped("こいし", "AquesTalk1", "f2", flat: true, speed: 75, frequencyScale: 181),
        Shipped("さとり", "AquesTalk1", "f1", flat: true, speed: 89, frequencyScale: 135),
        Shipped("AquesTalk10 F1", "AquesTalk10", "F1E", memo: "女声"),
        Shipped("AquesTalk10 F2", "AquesTalk10", "F2E", height: 77, accent: 150),
        Shipped("AquesTalk10 F3", "AquesTalk10", "F1E", quality: 61, frequencyScale: 148),
        Shipped("AquesTalk10 M1", "AquesTalk10", "M1E", height: 30, memo: "男声"),
        Shipped("AquesTalk10 M2", "AquesTalk10", "M1E", speed: 105, height: 45, accent: 130, quality: 120),
        Shipped("AquesTalk10 R1", "AquesTalk10", "M1E", height: 30, accent: 20, quality: 190, memo: "ロボット"),
        Shipped("AquesTalk10 R2", "AquesTalk10", "F2E", speed: 70, height: 50, accent: 50, quality: 50, frequencyScale: 180, memo: "ロボット"),
        Shipped("女性１", "AquesTalk1", "f1"),
        Shipped("女性２", "AquesTalk1", "f2"),
        Shipped("女性３", "AquesTalk1", "f3"),
        Shipped("男声１", "AquesTalk1", "m1"),
        Shipped("男声２", "AquesTalk1", "m2"),
        Shipped("ロボット", "AquesTalk1", "r1"),
        Shipped("中性", "AquesTalk1", "imd1", volume: 90),
        Shipped("機械", "AquesTalk1", "dvd", volume: 90),
        Shipped("特殊", "AquesTalk1", "jgr"),
        Shipped("女性A", "AquesTalk2", "aq_defo1"),
        Shipped("女性C", "AquesTalk2", "aq_f1c"),
        Shipped("女性D", "AquesTalk2", "aq_huskey"),
        Shipped("女性E", "AquesTalk2", "aq_momo1"),
        Shipped("女性F", "AquesTalk2", "aq_rb2"),
        Shipped("女性G", "AquesTalk2", "aq_rm"),
        Shipped("女性H", "AquesTalk2", "aq_yukkuri"),
        Shipped("男性B", "AquesTalk2", "aq_m4b"),
        Shipped("ロボット2", "AquesTalk2", "aq_robo"),
        Shipped("中性2", "AquesTalk2", "aq_teto1")
    ];

    private static AquesTalkPlayerPreset Shipped(string name, string engine, string voice, bool flat = false, int speed = 100,
        int volume = 100, int height = 100, int accent = 100, int quality = 100, int frequencyScale = 100, string memo = "") =>
        new(name, new AquesTalkPlayerVoice
        {
            Engine = engine, Voice = voice, FlatReading = flat, Speed = speed, Volume = volume, Height = height,
            Accent = accent, Quality = quality, FrequencyScale = frequencyScale
        }, memo);

    public static bool IsJson(string path) => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);

    public static AquesTalkPlayerPreset[] Load(string path)
    {
        if (!File.Exists(path)) return [.. Defaults];
        byte[] data = File.ReadAllBytes(path);
        var presets = IsJson(path) ? ParseJson(data) : ParseCsv(data);
        // The command line speaks the first preset of a name, and the temporary preset is Beutl's own.
        return presets.Where(preset => preset.Name != AquesTalkPlayerTemporaryPreset.Name)
            .DistinctBy(preset => preset.Name, StringComparer.Ordinal).ToArray();
    }

    internal static List<AquesTalkPlayerPreset> ParseJson(byte[] data)
    {
        using var document = JsonDocument.Parse(data);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("プリセットの一覧ではありません。");
        var presets = new List<AquesTalkPlayerPreset>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Text(item, "name") is not { } name || string.IsNullOrWhiteSpace(name)) continue;
            presets.Add(new(name, new AquesTalkPlayerVoice
            {
                Engine = Text(item, "engine") ?? "", Voice = Text(item, "voice") ?? "",
                FlatReading = item.TryGetProperty("boyomi", out var flat) && flat.ValueKind == JsonValueKind.True,
                Speed = Number(item, "spd"), Volume = Number(item, "vol"), Height = Number(item, "pit"),
                Accent = Number(item, "acc"), Quality = Number(item, "lmd"), FrequencyScale = Number(item, "fsc")
            }, Text(item, "memo") ?? ""));
        }
        return presets;

        static string? Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        static int Number(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.TryGetInt32(out int number) ? number : 100;
    }

    // Columns: name, flat reading, engine, voice type, speed, volume, height, accent, voice quality, pitch and memo,
    // after a row naming them.
    internal static List<AquesTalkPlayerPreset> ParseCsv(byte[] data)
    {
        var presets = new List<AquesTalkPlayerPreset>();
        foreach (var fields in ReadCsv(DecodeCsv(data).Text))
        {
            if (!IsPresetRow(fields)) continue;
            presets.Add(new(fields[0], new AquesTalkPlayerVoice
            {
                Engine = fields[2].Trim(), Voice = fields[3].Trim(), FlatReading = IsSet(fields[1]),
                Speed = Number(4), Volume = Number(5), Height = Number(6), Accent = Number(7), Quality = Number(8),
                FrequencyScale = Number(9)
            }, fields.Count > 10 ? fields[10].Trim() : ""));

            int Number(int index) => fields.Count > index && int.TryParse(fields[index].Trim(), out int value) ? value : 100;
        }
        return presets;
    }

    internal static bool IsPresetRow(List<string> fields) =>
        fields.Count >= 4 && !string.IsNullOrWhiteSpace(fields[0]) && (fields.Count == 4 || int.TryParse(fields[4].Trim(), out _));

    internal static bool IsSet(string flag) => flag.Trim() is "1" || flag.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    internal static (string Text, Encoding Encoding, bool Bom) DecodeCsv(byte[] data) => data is [0xEF, 0xBB, 0xBF, ..]
        ? (Encoding.UTF8.GetString(data, 3, data.Length - 3), Encoding.UTF8, true)
        : (ShiftJis.GetString(data), ShiftJis, false);

    internal static List<List<string>> ReadCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
