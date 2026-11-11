using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Beutl.Extensions.Voice.Models;

internal sealed record DictionaryWordType(string Id, string Name, int ContextId,
    string PartOfSpeech, string Detail1, string Detail2 = "*", string Detail3 = "*")
{
    public static readonly DictionaryWordType[] All =
    [
        new("PROPER_NOUN", "固有名詞", 1348, "名詞", "固有名詞", "一般"),
        new("COMMON_NOUN", "普通名詞", 1345, "名詞", "一般"),
        new("VERB", "動詞", 642, "動詞", "自立"),
        new("ADJECTIVE", "形容詞", 20, "形容詞", "自立"),
        new("SUFFIX", "語尾", 1358, "名詞", "接尾", "一般"),
        new("LOCATION_NAME", "地名", 1353, "名詞", "固有名詞", "地域", "一般"),
        new("ORGANIZATION_NAME", "組織・施設名", 1352, "名詞", "固有名詞", "組織"),
        new("PERSON_NAME", "人名", 1349, "名詞", "固有名詞", "人名", "一般"),
        new("PERSON_FAMILY_NAME", "人名（姓）", 1350, "名詞", "固有名詞", "人名", "姓"),
        new("PERSON_GIVEN_NAME", "人名（名）", 1351, "名詞", "固有名詞", "人名", "名")
    ];

    public static DictionaryWordType? FromJson(JsonObject data) => All.FirstOrDefault(x =>
        (string?)data["part_of_speech"] == x.PartOfSpeech && (string?)data["part_of_speech_detail_1"] == x.Detail1
        && (string?)data["part_of_speech_detail_2"] == x.Detail2 && (string?)data["part_of_speech_detail_3"] == x.Detail3);
}

internal sealed record VoiceDictionaryPhrase(string Surface, string Pronunciation, int AccentType);

// Keep the original wire data: export must retain engine-specific fields and compound accents.
internal sealed record VoiceDictionaryWord(Guid Id, JsonObject Data)
{
    public string Surface => (string?)Data["surface"] ?? "";
    public string Pronunciation => string.Concat(Phrases.Select(x => x.Pronunciation));
    public int Priority => (int?)Data["priority"] ?? 5;
    public DictionaryWordType? WordType => DictionaryWordType.FromJson(Data);
    public VoiceDictionaryPhrase[] Phrases
    {
        get
        {
            if (Data["pronunciation"] is not JsonArray pronunciation)
                return [new(Surface, (string?)Data["pronunciation"] ?? "", (int?)Data["accent_type"] ?? 0)];
            var surfaces = Data["stem"] as JsonArray;
            var accents = Data["accent_type"] as JsonArray
                ?? throw new FormatException($"「{Surface}」のアクセントが配列ではありません。");
            if (pronunciation.Count == 0 || accents.Count != pronunciation.Count || surfaces?.Count != pronunciation.Count)
                throw new FormatException($"「{Surface}」の表記・読み・アクセント句の数が一致しません。");
            return pronunciation.Select((p, i) => new VoiceDictionaryPhrase(
                (string?)surfaces[i] ?? "", (string?)p ?? "", (int?)accents[i] ?? 0)).ToArray();
        }
    }

    public static VoiceDictionaryWord Create(Guid id, VoiceDictionaryPhrase[] phrases, DictionaryWordType type,
        int priority, bool compound, JsonObject? original = null)
    {
        Validate(phrases, priority);
        if (!compound && phrases.Length != 1) throw new ArgumentException("このエンジンは複数のアクセント句に対応していません。");
        var data = (JsonObject?)original?.DeepClone() ?? new JsonObject();
        data["surface"] = string.Concat(phrases.Select(x => x.Surface));
        data["priority"] = priority;
        data["context_id"] = type.ContextId;
        data["part_of_speech"] = type.PartOfSpeech;
        data["part_of_speech_detail_1"] = type.Detail1;
        data["part_of_speech_detail_2"] = type.Detail2;
        data["part_of_speech_detail_3"] = type.Detail3;
        foreach (string key in new[] { "inflectional_type", "inflectional_form", "accent_associative_rule" })
            if (data[key] == null) data[key] = "*";
        if (compound)
        {
            data["word_type"] = type.Id;
            data["stem"] = new JsonArray(phrases.Select(x => JsonValue.Create(x.Surface)).ToArray());
            data["yomi"] = new JsonArray(phrases.Select(x => JsonValue.Create(x.Pronunciation)).ToArray());
            data["pronunciation"] = data["yomi"]!.DeepClone();
            data["accent_type"] = new JsonArray(phrases.Select(x => JsonValue.Create(x.AccentType)).ToArray());
            data["mora_count"] = new JsonArray(phrases.Select(x => JsonValue.Create(DictionaryKana.Moras(x.Pronunciation).Length)).ToArray());
        }
        else
        {
            data.Remove("word_type");
            data["stem"] = "*";
            data["yomi"] = phrases[0].Pronunciation;
            data["pronunciation"] = phrases[0].Pronunciation;
            data["accent_type"] = phrases[0].AccentType;
            data["mora_count"] = DictionaryKana.Moras(phrases[0].Pronunciation).Length;
        }
        return new(id, data);
    }

    internal static void Validate(VoiceDictionaryPhrase[] phrases, int priority)
    {
        if (phrases.Length == 0 || priority is < 0 or > 10) throw new ArgumentException("優先度は0〜10で指定してください。");
        foreach (var phrase in phrases)
        {
            if (string.IsNullOrWhiteSpace(phrase.Surface) || phrase.Surface.IndexOfAny(['\0', '\r', '\n']) >= 0)
                throw new ArgumentException("表記を1行で入力してください。");
            var moras = DictionaryKana.Moras(phrase.Pronunciation);
            if (moras.Length == 0 || string.Concat(moras) != phrase.Pronunciation)
                throw new ArgumentException("読みを全角カタカナで入力してください。");
            if (phrase.AccentType < 0 || phrase.AccentType > moras.Length)
                throw new ArgumentException($"アクセント位置は0〜{moras.Length}で指定してください。");
        }
    }
}

internal static class DictionaryKana
{
    // VOICEVOX/OpenJTalk mora combinations; the same tokenization drives the editor and preview.
    private static readonly Regex s_moras = new(
        "イェ|ヴ[ャュョ]|[クグトド]ゥ|[テデ][ィャュョ]|デェ|[クグ]ヮ|[キシチニヒミリギジヂビピ][ェャュョ]|シィ|[クツフヴグ]ァ|[ウクスツフヴグズ]ィ|[ウクツフヴグ][ェォ]|フュ|[ァ-ヴー]",
        RegexOptions.CultureInvariant);

    public static string[] Moras(string pronunciation) => s_moras.Matches(pronunciation).Select(x => x.Value).ToArray();

    public static (string Kana, string Text) Preview(VoiceDictionaryPhrase[] phrases)
    {
        var kana = phrases.Select(phrase =>
        {
            var moras = Moras(phrase.Pronunciation).ToList();
            // AquesTalk kana requires an explicit vowel for each long-vowel mora.
            // Normalize only the preview; the dictionary keeps the original spelling.
            for (int i = 1; i < moras.Count; i++)
                if (moras[i] == "ー") moras[i] = VowelKana(moras[i - 1]);
            int accent = phrase.AccentType == 0 ? moras.Count : phrase.AccentType;
            moras.Insert(accent, "'");
            return string.Concat(moras);
        });
        return (string.Join('/', kana), string.Concat(phrases.Select(x => x.Pronunciation)));
    }

    private static string VowelKana(string mora) => mora[^1] switch
    {
        char c when "アァカガサザタダナハバパマヤャラワヮ".Contains(c) => "ア",
        char c when "イィキギシジチヂニヒビピミリヰ".Contains(c) => "イ",
        char c when "ウゥクグスズツヅヌフブプムユュルヴ".Contains(c) => "ウ",
        char c when "エェケゲセゼテデネヘベペメレヱ".Contains(c) => "エ",
        char c when "オォコゴソゾトドノホボポモヨョロヲ".Contains(c) => "オ",
        'ン' => "ン", 'ッ' => "ッ",
        _ => mora
    };
}
