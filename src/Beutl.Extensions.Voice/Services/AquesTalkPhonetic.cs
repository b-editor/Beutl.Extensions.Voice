using System.Text;
using Beutl.Extensions.Voice.Models;

namespace Beutl.Extensions.Voice.Services;

// Writes VOICEVOX's analysis in AQUEST's phonetic notation (音声記号列仕様書 2.0), which AquesTalkPlayer reads on a line
// starting with "#>": a reading in hiragana with "'" after the accent nucleus, "/" between accent phrases, "、" at a
// pause, and "。" or "？" at the end.
internal static class AquesTalkPhonetic
{
    public const string Prefix = "#>";

    // Readings the notation has no symbol for, written as it says to: "ぢ" and "づ" as "じ" and "ず".
    private static readonly (string From, string To)[] s_replacements =
    [
        ("ヴァ", "バ"), ("ヴィ", "ビ"), ("ヴェ", "ベ"), ("ヴォ", "ボ"), ("ヴュ", "ビュ"), ("ヴ", "ブ"),
        ("ヂ", "ジ"), ("ヅ", "ズ"), ("ヮ", "ァ"), ("ヰ", "イ"), ("ヱ", "エ")
    ];

    public static string FromQuery(AudioQueryModel query)
    {
        var phrases = query.AccentPhrases.Where(phrase => phrase.Moras.Count > 0).ToArray();
        if (phrases.Length == 0) throw new InvalidOperationException("解析結果に読みがありません。");
        var builder = new StringBuilder(Prefix);
        for (int i = 0; i < phrases.Length; i++)
        {
            var phrase = phrases[i];
            string? previousVowel = null;
            for (int j = 0; j < phrase.Moras.Count; j++)
            {
                var mora = phrase.Moras[j];
                string vowel = mora.Vowel?.ToLowerInvariant() ?? "";
                // VOICEVOX spells a long vowel as the vowel itself; the notation has "ー", which cannot begin a phrase.
                bool lengthened = j > 0 && mora.Consonant == null && vowel is "a" or "i" or "u" or "e" or "o" && vowel == previousVowel;
                builder.Append(lengthened ? "ー" : Hiragana(mora.Text));
                previousVowel = vowel;
                // VOICEVOX writes a flat phrase as one accented on its last mora, which the notation leaves unmarked.
                if (j + 1 == phrase.Accent && phrase.Accent < phrase.Moras.Count) builder.Append('\'');
            }
            if (i == phrases.Length - 1) builder.Append(phrase.IsInterrogative ? '？' : '。');
            else if (phrase.PauseMora != null) builder.Append(phrase.IsInterrogative ? '？' : '、');
            else builder.Append('/');
        }
        return builder.ToString();
    }

    private static string Hiragana(string katakana)
    {
        foreach (var (from, to) in s_replacements) katakana = katakana.Replace(from, to);
        var builder = new StringBuilder(katakana.Length);
        // Hiragana leaves devoicing and the nasal g to the app's own rules, as it does for text.
        foreach (char c in katakana) builder.Append(c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c);
        return builder.ToString();
    }
}
