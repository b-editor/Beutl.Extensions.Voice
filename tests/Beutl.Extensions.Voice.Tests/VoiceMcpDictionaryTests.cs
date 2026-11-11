using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.NUnit;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using EngineServer = Beutl.Extensions.Voice.Tests.HttpVoiceEngineTests.EngineServer;

namespace Beutl.Extensions.Voice.Tests;

public partial class VoiceMcpToolTests
{
    private static VoiceDictionaryWord DictionaryEntry()
    {
        var word = VoiceDictionaryWord.Create(Guid.NewGuid(), [new("試験", "シケン", 2)], DictionaryWordType.All[0], 5, false);
        word.Data["engine_extension"] = new JsonObject { ["keep_original_name"] = 123 };
        return word;
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Dictionary_operations_remain_available_when_speaker_discovery_fails(bool aivis)
    {
        using var server = new EngineServer(aivis);
        var engine = await Register(server);
        server.FailDiscovery = true;
        Assert.That((await Invoke("voice_list_voices", new { engineId = engine.Id })).IsError, Is.True);
        int discoveryCount = server.DiscoveryCount;
        var empty = Data(await Invoke("voice_list_dictionary_words", new { engineId = engine.Id }));
        Assert.That(empty.GetProperty("supportsCompoundAccent").GetBoolean(), Is.EqualTo(aivis));
        Assert.That(empty.GetProperty("wordTypes").GetArrayLength(), Is.EqualTo(aivis ? 10 : 5));
        VoiceDictionaryPhrase[] phrases = aivis ? [new("新田", "アラタ", 1), new("真剣佑", "マッケンユー", 3)] : [new("試験", "シケン", 2)];
        string type = aivis ? "PERSON_GIVEN_NAME" : "PROPER_NOUN";
        var saved = Data(await Invoke("voice_save_dictionary_word", new { engineId = engine.Id, phrases, wordType = type }));
        Guid id = saved.GetProperty("wordId").GetGuid();
        Data(await Invoke("voice_save_dictionary_word", new { engineId = engine.Id, wordId = id, priority = 9 }));
        var exported = Data(await Invoke("voice_export_dictionary", new { engineId = engine.Id })).GetProperty("dictionary");
        Assert.That(exported.GetProperty(id.ToString()).GetProperty("priority").GetInt32(), Is.EqualTo(9));
        Data(await Invoke("voice_delete_dictionary_word", new { engineId = engine.Id, wordId = id }));
        Assert.That(server.Dictionary, Is.Empty);
        var imported = Data(await Invoke("voice_import_dictionary", new { engineId = engine.Id, dictionary = exported }));
        Assert.That(imported.GetProperty("submittedCount").GetInt32(), Is.EqualTo(1));
        var restored = VoiceDictionaryService.Parse(server.Dictionary).Single();
        Assert.That(restored.Id, Is.EqualTo(id));
        Assert.That(restored.Phrases, Is.EqualTo(phrases));
        Assert.That(restored.WordType!.Id, Is.EqualTo(type));
        Assert.That(server.DiscoveryCount, Is.EqualTo(discoveryCount), "Dictionary tools must use the registered connection without speaker discovery.");
        Assert.That(VoiceEngineStore.Load().Single().EngineUuid, Is.EqualTo(engine.EngineUuid));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Dictionary_crud_uses_the_selected_engine_and_preserves_omitted_word_fields(bool aivis)
    {
        using var server = new EngineServer(aivis);
        var engine = await Register(server);
        var empty = Data(await Invoke("voice_list_dictionary_words", new { engineId = engine.Id }));
        Assert.That(empty.GetProperty("words").GetArrayLength(), Is.Zero);
        Assert.That(empty.GetProperty("wordTypes").GetArrayLength(), Is.EqualTo(aivis ? 10 : 5));
        Assert.That(empty.GetProperty("supportsCompoundAccent").GetBoolean(), Is.EqualTo(aivis));
        VoiceDictionaryPhrase[] phrases = aivis ? [new("新田", "アラタ", 1), new("真剣佑", "マッケンユー", 3)] : [new("試験", "シケン", 2)];
        string type = aivis ? "PERSON_GIVEN_NAME" : "PROPER_NOUN";
        var saved = Data(await Invoke("voice_save_dictionary_word", new { engineId = engine.Id, phrases, wordType = type }));
        Guid id = saved.GetProperty("wordId").GetGuid();
        var word = Data(await Invoke("voice_list_dictionary_words", new { engineId = engine.Id })).GetProperty("words")[0];
        Assert.That(word.GetProperty("wordId").GetGuid(), Is.EqualTo(id));
        Assert.That(word.GetProperty("wordType").GetString(), Is.EqualTo(type));
        Assert.That(word.GetProperty("phrases").GetArrayLength(), Is.EqualTo(phrases.Length));
        Assert.That(server.CompoundDictionaryRequested, Is.EqualTo(aivis));
        Data(await Invoke("voice_save_dictionary_word", new { engineId = engine.Id, wordId = id, priority = 9 }));
        var loaded = VoiceDictionaryService.Parse(server.Dictionary).Single();
        Assert.That(loaded.Priority, Is.EqualTo(9));
        Assert.That(loaded.Phrases, Is.EqualTo(phrases));
        Assert.That(loaded.WordType!.Id, Is.EqualTo(type));
        int writes = server.DictionaryWriteCount;
        Assert.That((await Invoke("voice_save_dictionary_word", new { engineId = engine.Id, wordId = Guid.NewGuid(), priority = 1 })).IsError, Is.True);
        Assert.That(server.DictionaryWriteCount, Is.EqualTo(writes), "Updating a missing word must not create one.");
        Data(await Invoke("voice_delete_dictionary_word", new { engineId = engine.Id, wordId = id }));
        Assert.That(server.Dictionary, Is.Empty);
    }

    [AvaloniaTest]
    public async Task Dictionary_listing_filters_and_pages_editable_fields_without_exporting_raw_data()
    {
        using var server = new EngineServer();
        var engine = await Register(server);
        for (int i = 0; i < 60; i++)
        {
            var word = DictionaryEntry();
            word.Data["surface"] = $"単語{i:000}";
            server.Dictionary[word.Id.ToString()] = word.Data.DeepClone();
        }
        var first = Data(await Invoke("voice_list_dictionary_words", new { engineId = engine.Id }));
        Assert.That(first.GetProperty("totalCount").GetInt32(), Is.EqualTo(60));
        Assert.That(first.GetProperty("words").GetArrayLength(), Is.EqualTo(50));
        Assert.That(first.GetProperty("nextOffset").GetInt32(), Is.EqualTo(50));
        Assert.That(first.GetProperty("words")[0].TryGetProperty("data", out _), Is.False);
        var next = Data(await Invoke("voice_list_dictionary_words", new { engineId = engine.Id, offset = 50 }));
        Assert.That(next.GetProperty("words").GetArrayLength(), Is.EqualTo(10));
        Assert.That(next.GetProperty("words")[0].GetProperty("surface").GetString(), Is.EqualTo("単語050"));
        Assert.That(next.GetProperty("nextOffset").ValueKind, Is.EqualTo(JsonValueKind.Null));
        var filtered = Data(await Invoke("voice_list_dictionary_words", new { engineId = engine.Id, search = "単語05", offset = 2, limit = 3 }));
        Assert.That(filtered.GetProperty("totalCount").GetInt32(), Is.EqualTo(10));
        Assert.That(filtered.GetProperty("words").GetArrayLength(), Is.EqualTo(3));
        Assert.That(filtered.GetProperty("words")[0].GetProperty("surface").GetString(), Is.EqualTo("単語052"));
        Assert.That(filtered.GetProperty("nextOffset").GetInt32(), Is.EqualTo(5));
        var all = Data(await Invoke("voice_list_dictionary_words", new { engineId = engine.Id, search = " シケン ", limit = 100 }));
        Assert.That(all.GetProperty("words").GetArrayLength(), Is.EqualTo(60), "Callers can request a larger page.");
        var empty = Data(await Invoke("voice_list_dictionary_words", new { engineId = engine.Id, offset = int.MaxValue }));
        Assert.That(empty.GetProperty("words").GetArrayLength(), Is.Zero);
        Assert.That(empty.GetProperty("nextOffset").ValueKind, Is.EqualTo(JsonValueKind.Null));
        var latin = DictionaryEntry();
        latin.Data["surface"] = "Codex";
        server.Dictionary[latin.Id.ToString()] = latin.Data;
        var insensitive = Data(await Invoke("voice_list_dictionary_words", new { engineId = engine.Id, search = "cOdEx" }));
        Assert.That(insensitive.GetProperty("words").GetArrayLength(), Is.EqualTo(1));
        Assert.That(insensitive.GetProperty("words")[0].GetProperty("wordId").GetGuid(), Is.EqualTo(latin.Id));
        Assert.That(server.DictionaryWriteCount, Is.Zero);
    }

    [AvaloniaTest]
    public async Task Dictionary_export_and_import_preserve_ids_unknown_fields_and_report_invalid_entries_and_duplicates()
    {
        using var server = new EngineServer();
        var engine = await Register(server);
        var original = DictionaryEntry();
        server.Dictionary[original.Id.ToString()] = original.Data.DeepClone();
        var exported = Data(await Invoke("voice_export_dictionary", new { engineId = engine.Id }));
        var dictionary = JsonNode.Parse(exported.GetProperty("dictionary").GetRawText())!.AsObject();
        Assert.That(JsonNode.DeepEquals(dictionary, server.Dictionary), Is.True);
        var duplicate = Data(await Invoke("voice_import_dictionary", new { engineId = engine.Id, dictionary }));
        Assert.That(duplicate.GetProperty("submittedCount").GetInt32(), Is.Zero);
        Assert.That(duplicate.GetProperty("entries")[0].GetProperty("duplicate").GetBoolean(), Is.True);
        Assert.That(server.DictionaryWriteCount, Is.Zero);
        dictionary[original.Id.ToString()]!["priority"] = 1;
        var added = DictionaryEntry();
        dictionary[added.Id.ToString()] = added.Data.DeepClone();
        dictionary["not-a-uuid"] = added.Data.DeepClone();
        dictionary[Guid.NewGuid().ToString()] = 42;
        var preview = Data(await Invoke("voice_import_dictionary", new { engineId = engine.Id, dictionary, dryRun = true }));
        Assert.That(preview.GetProperty("applicableCount").GetInt32(), Is.EqualTo(1));
        Assert.That(preview.GetProperty("submittedCount").GetInt32(), Is.Zero);
        Assert.That(preview.GetProperty("entries")[2].GetProperty("error").GetString(), Is.Not.Empty);
        Assert.That(preview.GetProperty("entries")[3].GetProperty("error").GetString(), Is.Not.Empty);
        Assert.That(server.DictionaryWriteCount, Is.Zero);
        var imported = Data(await Invoke("voice_import_dictionary", new { engineId = engine.Id, dictionary }));
        Assert.That(imported.GetProperty("submittedCount").GetInt32(), Is.EqualTo(1));
        Assert.That((int?)server.Dictionary[original.Id.ToString()]!["priority"], Is.EqualTo(5));
        Assert.That(JsonNode.DeepEquals(server.Dictionary[added.Id.ToString()], added.Data), Is.True);
        var overwritten = Data(await Invoke("voice_import_dictionary", new { engineId = engine.Id, dictionary, overwrite = true }));
        Assert.That(overwritten.GetProperty("submittedCount").GetInt32(), Is.EqualTo(2));
        Assert.That((int?)server.Dictionary[original.Id.ToString()]!["priority"], Is.EqualTo(1));
        Assert.That((int?)server.Dictionary[original.Id.ToString()]!["engine_extension"]!["keep_original_name"], Is.EqualTo(123));
    }

    [AvaloniaTest]
    public async Task Dictionary_import_converts_supported_formats_and_keeps_engine_dictionaries_independent()
    {
        using var standardServer = new EngineServer();
        using var aivisServer = new EngineServer(aivis: true);
        var standard = await Register(standardServer);
        var aivis = await Register(aivisServer);
        var original = DictionaryEntry();
        standardServer.Dictionary[original.Id.ToString()] = original.Data.DeepClone();
        var exported = Data(await Invoke("voice_export_dictionary", new { engineId = standard.Id })).GetProperty("dictionary");
        Data(await Invoke("voice_import_dictionary", new { engineId = aivis.Id, dictionary = exported }));
        Assert.That(aivisServer.Dictionary[original.Id.ToString()]!["pronunciation"], Is.TypeOf<JsonArray>());
        Assert.That((int?)aivisServer.Dictionary[original.Id.ToString()]!["engine_extension"]!["keep_original_name"], Is.EqualTo(123));
        var compound = VoiceDictionaryWord.Create(Guid.NewGuid(), [new("新田", "アラタ", 1), new("真剣佑", "マッケンユー", 3)],
            DictionaryWordType.All[9], 7, true);
        aivisServer.Dictionary[compound.Id.ToString()] = compound.Data.DeepClone();
        var fromAivis = Data(await Invoke("voice_export_dictionary", new { engineId = aivis.Id })).GetProperty("dictionary");
        var imported = Data(await Invoke("voice_import_dictionary", new { engineId = standard.Id, dictionary = fromAivis, overwrite = true }));
        Assert.That(imported.GetProperty("submittedCount").GetInt32(), Is.EqualTo(1));
        Assert.That(imported.GetProperty("entries")[1].GetProperty("error").GetString(), Does.Contain("複合アクセント"));
        Assert.That(standardServer.Dictionary[original.Id.ToString()]!["pronunciation"]!.GetValue<string>(), Is.EqualTo("シケン"));
        Data(await Invoke("voice_delete_dictionary_word", new { engineId = standard.Id, wordId = original.Id }));
        Assert.That(standardServer.Dictionary, Is.Empty);
        Assert.That(aivisServer.Dictionary.ContainsKey(original.Id.ToString()), Is.True);
    }

    [AvaloniaTest]
    public async Task Dictionary_mcp_writes_notify_the_voice_editor_without_losing_manual_accents()
    {
        using var server = new EngineServer();
        var engine = await Register(server);
        using var editor = Editor();
        using var model = new TtsTabViewModel(new(), editor);
        model.SelectedEngine.Value = model.Engines.Value.Single(item => item.Id == engine.Id);
        model.SelectedVoice.Value = model.Voice.Value.Single();
        model.Text.Value = "試験です。";
        await model.CreateQuery();
        var query = model.AudioQuery.Value!;
        query.AccentPhrases[0].Moras[0].Pitch = 7;
        Data(await Invoke("voice_save_dictionary_word", new { engineId = engine.Id, phrases = DictionaryEntry().Phrases }));
        Assert.That(model.AudioQuery.Value, Is.SameAs(query));
        Assert.That(model.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch, Is.EqualTo(7));
        Assert.That(model.DictionaryNotice.Value, Is.Not.Empty);
        await model.CreateQuery();
        Assert.That(model.AudioQuery.Value, Is.Not.SameAs(query));
        Assert.That(model.DictionaryNotice.Value, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(404)]
    [TestCase(501)]
    [TestCase(503)]
    public async Task Dictionary_endpoint_failures_are_tool_errors_and_can_be_retried(int status)
    {
        using var server = new EngineServer { DictionaryStatus = status };
        var engine = await Register(server);
        Assert.That((await Invoke("voice_list_dictionary_words", new { engineId = engine.Id })).IsError, Is.True);
        server.DictionaryStatus = 200;
        Data(await Invoke("voice_list_dictionary_words", new { engineId = engine.Id }));
        server.DictionaryWriteStatus = 503;
        var request = new { engineId = engine.Id, phrases = DictionaryEntry().Phrases };
        Assert.That((await Invoke("voice_save_dictionary_word", request)).IsError, Is.True);
        Assert.That(server.Dictionary, Is.Empty);
        server.DictionaryWriteStatus = 204;
        Data(await Invoke("voice_save_dictionary_word", request));
        Assert.That(server.Dictionary, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    [TestCase("/tts/user_dict")]
    [TestCase("/tts/user_dict_word")]
    public async Task Dictionary_requests_propagate_cancellation_before_the_engine_performs_the_operation(string endpoint)
    {
        using var server = new EngineServer { PauseAt = endpoint, AbortPausedResponse = true };
        var engine = await Register(server);
        using var cancellation = new CancellationTokenSource();
        var pending = endpoint.EndsWith("word", StringComparison.Ordinal)
            ? Invoke("voice_save_dictionary_word", new { engineId = engine.Id, phrases = DictionaryEntry().Phrases }, cancellationToken: cancellation.Token)
            : Invoke("voice_list_dictionary_words", new { engineId = engine.Id }, cancellationToken: cancellation.Token);
        await server.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Fail("Expected cancellation."); }
        catch (OperationCanceledException) { }
        Assert.That(server.DictionaryWriteCount, Is.Zero);
        Assert.That(server.Dictionary, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Dictionary_updates_recheck_changed_or_deleted_connections_after_reading_the_word(bool deleted)
    {
        using var server = new EngineServer();
        var engine = await Register(server);
        var word = DictionaryEntry();
        server.Dictionary[word.Id.ToString()] = word.Data.DeepClone();
        server.PauseAt = "/tts/user_dict";
        var pending = Invoke("voice_save_dictionary_word", new { engineId = engine.Id, wordId = word.Id, priority = 1 });
        await server.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (deleted) VoiceEngineStore.Delete(engine.Id);
        else VoiceEngineStore.Save(engine with { Url = "http://127.0.0.1:1/" });
        server.Resume.TrySetResult();
        Assert.That((await pending).IsError, Is.True);
        Assert.That(server.DictionaryWriteCount, Is.Zero);
        Assert.That(JsonNode.DeepEquals(server.Dictionary[word.Id.ToString()], word.Data), Is.True);
    }

    [AvaloniaTest]
    public async Task Native_core_dictionary_is_accessible_from_mcp_without_an_open_scene()
    {
        string? coreHome = Environment.GetEnvironmentVariable("VOICEVOX_TEST_HOME");
        if (coreHome == null) Assert.Ignore("Set VOICEVOX_TEST_HOME to an installed VOICEVOX Core directory.");
        var previous = TtsLoader.VoiceVoxLoader.Value;
        var loader = new VoiceVoxLoader(coreHome!);
        try
        {
            await Task.Run(loader.Load);
            TtsLoader.VoiceVoxLoader.Value = loader;
            Data(await Invoke("voice_list_dictionary_words", new { }));
            var result = Data(await Invoke("voice_save_dictionary_word", new { phrases = DictionaryEntry().Phrases }));
            Guid id = result.GetProperty("wordId").GetGuid();
            var exported = Data(await Invoke("voice_export_dictionary", new { })).GetProperty("dictionary");
            Assert.That(exported.TryGetProperty(id.ToString(), out _), Is.True);
            Data(await Invoke("voice_delete_dictionary_word", new { wordId = id }));
            Assert.That(Data(await Invoke("voice_list_dictionary_words", new { })).GetProperty("words").GetArrayLength(), Is.Zero);
            Data(await Invoke("voice_import_dictionary", new { dictionary = exported }));
            Assert.That(Data(await Invoke("voice_list_dictionary_words", new { })).GetProperty("words")[0].GetProperty("wordId").GetGuid(), Is.EqualTo(id));
        }
        finally
        {
            TtsLoader.VoiceVoxLoader.Value = previous;
            loader.Unload();
        }
    }
}
