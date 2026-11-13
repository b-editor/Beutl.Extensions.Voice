using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using VoicevoxCoreSharp.Core;
using VoicevoxCoreSharp.Core.Enum;
using VoicevoxCoreSharp.Core.Struct;

namespace Beutl.Extensions.Voice.Services;

internal sealed record DictionaryImportEntry(VoiceDictionaryWord? Word, string Name, string? Error, bool Duplicate);

internal static class VoiceDictionaryService
{
    private static readonly object s_coreLock = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    public static string CorePath => Path.Combine(BeutlEnvironment.GetHomeDirectoryPath(), "voice", "core-user-dictionary.json");
    public static event Action<string>? Changed;

    public static bool IsAivis(VoiceEngineConnection engine) =>
        string.Equals(engine.EngineUuid, VoiceEngineCapabilities.AivisSpeechUuid, StringComparison.OrdinalIgnoreCase);

    private static void RequireConnection(VoiceEngineConnection engine)
    {
        if (engine.IsCore) return;
        if (engine.IsAquesTalkPlayer)
            throw new NotSupportedException("AquesTalkPlayerのユーザ辞書は、AquesTalkPlayerのユーザ辞書設定で編集してください。");
        var current = VoiceEngineStore.Load().FirstOrDefault(x => x.Id == engine.Id);
        if (current == null || current.Url != engine.Url)
            throw new InvalidOperationException("接続先が変更・削除されています。辞書のエンジンを選び直してください。");
    }

    public static VoiceDictionaryWord[] Parse(JsonObject data) => data.Select(item =>
        new VoiceDictionaryWord(Guid.Parse(item.Key), item.Value as JsonObject
            ?? throw new FormatException("単語のデータがオブジェクトではありません。"))).ToArray();

    public static JsonObject ToJson(IEnumerable<VoiceDictionaryWord> words) => new(words.Select(x =>
        new KeyValuePair<string, JsonNode?>(x.Id.ToString(), x.Data.DeepClone())));

    public static string Export(IEnumerable<VoiceDictionaryWord> words) => ToJson(words).ToJsonString(s_jsonOptions);

    internal static UserDict LoadCoreDictionary()
    {
        var dictionary = new UserDict();
        try
        {
            if (File.Exists(CorePath)) Check(dictionary.Load(CorePath));
            return dictionary;
        }
        catch { dictionary.Dispose(); throw; }
    }

    private static void Check(ResultCode result)
    {
        if (result != ResultCode.RESULT_OK) throw new InvalidOperationException(result.ToMessage());
    }

    private static async Task<VoiceVoxLoader> CoreLoader(CancellationToken cancellationToken)
    {
        var loader = TtsLoader.VoiceVoxLoader.Value
            ?? throw new InvalidOperationException("VOICEVOX Coreをインストールしてください。「読み上げ」のエンジンメニューからセットアップできます。");
        await loader.InitializationTcs.Task.WaitAsync(cancellationToken);
        if (!loader.IsLoaded) throw new InvalidOperationException("VOICEVOX Coreを利用できません。「読み上げ」のエンジンメニューからセットアップを確認してください。");
        return loader;
    }

    public static async Task<VoiceDictionaryWord[]> LoadAsync(VoiceEngineConnection engine, CancellationToken cancellationToken = default)
    {
        RequireConnection(engine);
        if (engine.IsCore)
        {
            var loader = await CoreLoader(cancellationToken);
            return await Task.Run(() =>
            {
                lock (s_coreLock)
                {
                    var dictionary = LoadCoreDictionary();
                    try
                    {
                        Check(dictionary.ToJson(out var json));
                        var words = Parse(JsonNode.Parse(json!)!.AsObject());
                        loader.ApplyUserDictionary(dictionary, () => { });
                        return words;
                    }
                    catch { dictionary.Dispose(); throw; }
                }
            }, cancellationToken);
        }
        try
        {
            using var timeout = Timeout(cancellationToken);
            using var response = await HttpVoiceEngine.SendAsync(engine, HttpMethod.Get,
                IsAivis(engine) ? "user_dict?enable_compound_accent=true" : "user_dict", null, timeout.Token);
            return Parse(JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token))!.AsObject());
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            throw new NotSupportedException("このエンジンは辞書APIに対応していません。", ex);
        }
    }

    public static async Task<Guid> SaveAsync(VoiceEngineConnection engine, Guid? id, VoiceDictionaryWord word,
        CancellationToken cancellationToken = default)
    {
        RequireConnection(engine);
        VoiceDictionaryWord.Validate(word.Phrases, word.Priority);
        if (word.WordType is not { } type) throw new ArgumentException("品詞を選択してください。");
        Guid savedId;
        if (engine.IsCore)
        {
            var loader = await CoreLoader(cancellationToken);
            savedId = await Task.Run(() => ChangeCore(loader, dictionary =>
            {
                if (word.Phrases.Length != 1 || !Enum.TryParse<UserDictWordType>(type.Id, out var nativeType))
                    throw new ArgumentException("この品詞・アクセント句はVOICEVOX Coreに対応していません。");
                var phrase = word.Phrases[0];
                var nativeWord = UserDictWord.Create(phrase.Surface, phrase.Pronunciation, checked((nuint)phrase.AccentType));
                nativeWord.WordType = nativeType;
                nativeWord.Priority = checked((uint)word.Priority);
                if (id is { } existing) { Check(dictionary.UpdateWord(existing.ToString(), nativeWord)); return existing; }
                Check(dictionary.AddWord(nativeWord, out string? uuid));
                return Guid.Parse(uuid!);
            }), cancellationToken);
        }
        else
        {
            using var timeout = Timeout(cancellationToken);
            string parameters = string.Join('&', word.Phrases.SelectMany(p => new[]
            {
                "surface=" + Uri.EscapeDataString(p.Surface), "pronunciation=" + Uri.EscapeDataString(p.Pronunciation),
                "accent_type=" + p.AccentType.ToString(CultureInfo.InvariantCulture)
            }).Concat(["word_type=" + type.Id, "priority=" + word.Priority.ToString(CultureInfo.InvariantCulture)]));
            using var response = await HttpVoiceEngine.SendAsync(engine, id == null ? HttpMethod.Post : HttpMethod.Put,
                (id == null ? "user_dict_word" : "user_dict_word/" + id) + "?" + parameters, null, timeout.Token);
            savedId = id ?? JsonSerializer.Deserialize<Guid>(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        NotifyChanged(engine.Id);
        return savedId;
    }

    public static async Task DeleteAsync(VoiceEngineConnection engine, Guid id, CancellationToken cancellationToken = default)
    {
        RequireConnection(engine);
        if (engine.IsCore)
        {
            var loader = await CoreLoader(cancellationToken);
            await Task.Run(() => ChangeCore(loader, dictionary => { Check(dictionary.RemoveWord(id.ToString())); return true; }), cancellationToken);
        }
        else
        {
            using var timeout = Timeout(cancellationToken);
            using var response = await HttpVoiceEngine.SendAsync(engine, HttpMethod.Delete, "user_dict_word/" + id, null, timeout.Token);
        }
        NotifyChanged(engine.Id);
    }

    // Re-read under the mutex so edits from other Beutl processes survive each update.
    private static T ChangeCore<T>(VoiceVoxLoader loader, Func<UserDict, T> change)
    {
        lock (s_coreLock)
        {
            using var mutex = new Mutex(false, "Beutl.Extensions.Voice.Dictionary", new NamedWaitHandleOptions { CurrentSessionOnly = false });
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException("別のBeutlが辞書を保存しています。もう一度操作してください。");
            }
            catch (AbandonedMutexException) { }
            try
            {
                var dictionary = LoadCoreDictionary();
                string temporary = CorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                bool transferred = false;
                try
                {
                    T result = change(dictionary);
                    Directory.CreateDirectory(Path.GetDirectoryName(CorePath)!);
                    Check(dictionary.Save(temporary));
                    loader.ApplyUserDictionary(dictionary, () => File.Move(temporary, CorePath, overwrite: true));
                    transferred = true;
                    return result;
                }
                finally
                {
                    if (!transferred) dictionary.Dispose();
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
    }

    public static DictionaryImportEntry[] PrepareImport(string json, VoiceEngineConnection engine, IEnumerable<Guid> existingIds)
    {
        var data = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("辞書のJSONオブジェクトを選択してください。");
        var existing = existingIds.ToHashSet();
        return data.Select(pair =>
        {
            string name = (pair.Value as JsonObject)?["surface"]?.ToString() ?? pair.Key;
            try
            {
                var word = new VoiceDictionaryWord(Guid.Parse(pair.Key), pair.Value as JsonObject
                    ?? throw new FormatException("単語のデータがオブジェクトではありません。"));
                var phrases = word.Phrases;
                VoiceDictionaryWord.Validate(phrases, word.Priority);
                var type = word.WordType ?? throw new FormatException("この品詞に対応していません。");
                bool aivis = IsAivis(engine);
                if (!aivis && (phrases.Length != 1 || Array.IndexOf(DictionaryWordType.All, type) >= 5))
                    throw new FormatException("このエンジンは複合アクセントまたはこの品詞に対応していません。");
                // Leave same-format data intact, including extension fields.
                if (aivis != (word.Data["pronunciation"] is JsonArray))
                    word = VoiceDictionaryWord.Create(word.Id, phrases, type, word.Priority, aivis, word.Data);
                return new DictionaryImportEntry(word, name, null, existing.Contains(word.Id));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or JsonException)
            { return new DictionaryImportEntry(null, name, ex.Message, false); }
        }).ToArray();
    }

    public static async Task ImportAsync(VoiceEngineConnection engine, IEnumerable<VoiceDictionaryWord> words,
        bool overwrite, CancellationToken cancellationToken = default)
    {
        RequireConnection(engine);
        var data = ToJson(words);
        if (data.Count == 0) return;
        if (engine.IsCore)
        {
            var loader = await CoreLoader(cancellationToken);
            await Task.Run(() => ChangeCore(loader, dictionary =>
            {
                Check(dictionary.ToJson(out string? json));
                var merged = JsonNode.Parse(json!)!.AsObject();
                foreach (var item in data)
                    if (overwrite || !merged.ContainsKey(item.Key)) merged[item.Key] = item.Value!.DeepClone();
                string temporary = Path.Combine(Path.GetTempPath(), "voice-dict-import-" + Guid.NewGuid().ToString("N") + ".json");
                try { File.WriteAllText(temporary, merged.ToJsonString()); Check(dictionary.Load(temporary)); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return true;
            }), cancellationToken);
        }
        else
        {
            using var timeout = Timeout(cancellationToken);
            using var response = await HttpVoiceEngine.SendAsync(engine, HttpMethod.Post,
                "import_user_dict?override=" + (overwrite ? "true" : "false"), data.ToJsonString(), timeout.Token);
        }
        NotifyChanged(engine.Id);
    }

    public static async Task<VoiceSynthesisResult> PreviewAsync(VoiceEngineConnection engine, VoiceDictionaryWord word,
        VoiceSettings settings, CancellationToken cancellationToken = default)
    {
        RequireConnection(engine);
        VoiceDictionaryWord.Validate(word.Phrases, word.Priority);
        var (kana, text) = DictionaryKana.Preview(word.Phrases);
        AudioQueryModel query;
        if (engine.IsCore)
        {
            var loader = await CoreLoader(cancellationToken);
            query = await Task.Run(() =>
            {
                if (!loader.EnsureVoiceModelLoaded(checked((uint)settings.StyleId))) throw new InvalidOperationException("音声モデルを読み込めませんでした。");
                Check(loader.Synthesizer!.CreateAudioQueryFromKana(kana, checked((uint)settings.StyleId), out string? json));
                return JsonSerializer.Deserialize<AudioQueryModel>(json!)!;
            }, cancellationToken);
        }
        else
        {
            query = await HttpVoiceEngine.CreateQueryAsync(engine, text, settings.StyleId, cancellationToken);
            using var timeout = Timeout(cancellationToken);
            using var response = await HttpVoiceEngine.SendAsync(engine, HttpMethod.Post,
                "accent_phrases?is_kana=true&speaker=" + settings.StyleId.ToString(CultureInfo.InvariantCulture)
                + "&text=" + Uri.EscapeDataString(kana), null, timeout.Token);
            query.AccentPhrases = JsonSerializer.Deserialize<List<AccentPhraseModel>>(await response.Content.ReadAsStringAsync(timeout.Token))!;
            // AivisSpeech uses kana as the original synthesis text, rather than AquesTalk notation.
            if (IsAivis(engine)) query.Kana = text;
            return await HttpVoiceEngine.SynthesizeAsync(engine, text, settings, query, cancellationToken);
        }
        return await VoiceSynthesisService.SynthesizeAsync(text, settings, query, cancellationToken);
    }

    internal static void NotifyChanged(string engineId) => Changed?.Invoke(engineId);

    private static CancellationTokenSource Timeout(CancellationToken token)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return timeout;
    }
}
