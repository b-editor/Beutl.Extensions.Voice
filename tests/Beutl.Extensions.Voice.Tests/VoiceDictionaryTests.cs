using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using FluentAvalonia.UI.Controls;
using VoicevoxCoreSharp.Core.Enum;

namespace Beutl.Extensions.Voice.Tests;

public class VoiceDictionaryTests
{
    private string _directory = null!;
    private string? _previousHome;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "voice-dictionary-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _previousHome = Environment.GetEnvironmentVariable("BEUTL_HOME");
        Environment.SetEnvironmentVariable("BEUTL_HOME", _directory);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("BEUTL_HOME", _previousHome);
        Directory.Delete(_directory, true);
    }

    private static VoiceDictionaryWord Word(Guid? id = null) => new(id ?? Guid.NewGuid(), JsonNode.Parse("""
        {"surface":"試験","priority":5,"context_id":1348,"part_of_speech":"名詞",
         "part_of_speech_detail_1":"固有名詞","part_of_speech_detail_2":"一般","part_of_speech_detail_3":"*",
         "inflectional_type":"*","inflectional_form":"*","stem":"*","yomi":"シケン","pronunciation":"シケン",
         "accent_type":2,"mora_count":3,"accent_associative_rule":"*","future_field":{"keep":true}}
        """)!.AsObject());

    private static VoiceDictionaryWord Compound() => new(Guid.NewGuid(), JsonNode.Parse("""
        {"surface":"新田真剣佑","priority":7,"context_id":1351,"part_of_speech":"名詞",
         "part_of_speech_detail_1":"固有名詞","part_of_speech_detail_2":"人名","part_of_speech_detail_3":"名",
         "word_type":"PERSON_GIVEN_NAME","inflectional_type":"*","inflectional_form":"*",
         "stem":["新田","真剣佑"],"yomi":["アラタ","マッケンユー"],"pronunciation":["アラタ","マッケンユー"],
         "accent_type":[1,3],"mora_count":[3,6],"accent_associative_rule":"*","future_field":123}
        """)!.AsObject());

    private static async Task<VoiceEngineConnection> Connect(HttpVoiceEngineTests.EngineServer server)
    {
        var engine = await HttpVoiceEngine.DiscoverAsync(new() { Url = server.Url });
        VoiceEngineStore.Save(engine);
        return engine;
    }

    [Test]
    public void Json_roundtrip_and_import_conversion_preserve_identity_and_engine_information()
    {
        var word = Word();
        var compound = Compound();
        var aivis = new VoiceEngineConnection { EngineUuid = VoiceEngineCapabilities.AivisSpeechUuid };
        string json = VoiceDictionaryService.Export([word, compound]);
        var parsed = VoiceDictionaryService.Parse(JsonNode.Parse(json)!.AsObject());
        Assert.That(JsonNode.DeepEquals(parsed[0].Data, word.Data), Is.True);
        Assert.That(JsonNode.DeepEquals(parsed[1].Data, compound.Data), Is.True);

        var entries = VoiceDictionaryService.PrepareImport(json, aivis, [word.Id]);
        Assert.That(entries[0].Duplicate, Is.True);
        Assert.That(entries[0].Word!.Id, Is.EqualTo(word.Id));
        Assert.That(entries[0].Word!.Data["pronunciation"], Is.TypeOf<JsonArray>());
        Assert.That((bool?)entries[0].Word!.Data["future_field"]!["keep"], Is.True);
        Assert.That(JsonNode.DeepEquals(entries[1].Word!.Data, compound.Data), Is.True);
        Assert.That(entries[1].Word!.Phrases.Select(x => x.AccentType), Is.EqualTo(new[] { 1, 3 }));

        var converted = VoiceDictionaryService.PrepareImport(VoiceDictionaryService.Export([entries[0].Word!]), VoiceEngineConnection.Core, []);
        Assert.That(converted.Single().Word!.Pronunciation, Is.EqualTo("シケン"));
        Assert.That(converted.Single().Word!.Id, Is.EqualTo(word.Id));
        Assert.That((bool?)converted.Single().Word!.Data["future_field"]!["keep"], Is.True);
        Assert.That(VoiceDictionaryService.PrepareImport(json, VoiceEngineConnection.Core, [])[1].Error,
            Does.Contain("複合アクセント"));
    }

    [Test]
    public void Import_review_reports_invalid_entries_without_losing_valid_ones()
    {
        var word = Word();
        var data = VoiceDictionaryService.ToJson([word, Compound()]);
        data["invalid-id"] = word.Data.DeepClone();
        var items = VoiceDictionaryService.PrepareImport(data.ToJsonString(), VoiceEngineConnection.Core, []);
        Assert.That(items.Count(x => x.Error == null), Is.EqualTo(1));
        Assert.That(items.Count(x => x.Error != null), Is.EqualTo(2));
        Assert.That(items.Single(x => x.Error == null).Word!.Id, Is.EqualTo(word.Id));
        Assert.Throws<FormatException>(() => VoiceDictionaryService.PrepareImport("[]", VoiceEngineConnection.Core, []));
    }

    [TestCase(0, "テスト'")]
    [TestCase(1, "テ'スト")]
    [TestCase(3, "テスト'")]
    public void Preview_reads_only_the_word_with_the_requested_accent(int accent, string expected)
    {
        var preview = DictionaryKana.Preview([new("テスト", "テスト", accent)]);
        Assert.That(preview.Kana, Is.EqualTo(expected));
        Assert.That(preview.Text, Is.EqualTo("テスト"));
        Assert.That(DictionaryKana.Moras("キャットクヮルテット"), Is.EqualTo(new[] { "キャ", "ッ", "ト", "クヮ", "ル", "テ", "ッ", "ト" }));
    }

    [TestCase("コーヒー", 0, "コオヒイ'")]
    [TestCase("コーヒー", 1, "コ'オヒイ")]
    [TestCase("コーヒー", 2, "コオ'ヒイ")]
    [TestCase("コーヒー", 4, "コオヒイ'")]
    [TestCase("キャーー", 0, "キャアア'")]
    [TestCase("シュート", 1, "シュ'ウト")]
    [TestCase("ヴェール", 0, "ヴェエル'")]
    [TestCase("グヮー", 0, "グヮア'")]
    [TestCase("ンー", 0, "ンン'")]
    [TestCase("ッー", 0, "ッッ'")]
    public void Preview_expands_long_vowels_without_changing_the_dictionary(string reading, int accent, string expected)
    {
        var word = VoiceDictionaryWord.Create(Guid.NewGuid(), [new("試聴", reading, accent)], DictionaryWordType.All[0], 5, false);
        string original = word.Data.ToJsonString();
        var preview = DictionaryKana.Preview(word.Phrases);
        Assert.That(preview.Kana, Is.EqualTo(expected));
        Assert.That(preview.Text, Is.EqualTo(reading));
        Assert.That(DictionaryKana.Moras(preview.Kana.Replace("'", "")).Length, Is.EqualTo(DictionaryKana.Moras(reading).Length));
        Assert.That(word.Data.ToJsonString(), Is.EqualTo(original));
    }

    [Test]
    public void Preview_expands_long_vowels_in_each_compound_phrase_and_preserves_accent_positions()
    {
        var preview = DictionaryKana.Preview([new("缶", "カーン", 1), new("珈琲", "コーヒー", 0)]);
        Assert.That(preview.Kana, Is.EqualTo("カ'アン/コオヒイ'"));
        Assert.That(preview.Text, Is.EqualTo("カーンコーヒー"));
    }

    [Test]
    public void Preview_preserves_a_real_final_ga()
    {
        var preview = DictionaryKana.Preview([new("大河", "タイガ", 0)]);
        Assert.That(preview.Kana, Is.EqualTo("タイガ'"));
        Assert.That(preview.Text, Is.EqualTo("タイガ"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Http_crud_import_and_preview_use_the_engine_dictionary(bool aivis)
    {
        using var server = new HttpVoiceEngineTests.EngineServer(aivis);
        var engine = await Connect(server);
        var word = aivis ? Compound() : Word();
        var id = await VoiceDictionaryService.SaveAsync(engine, null, word);
        Assert.That(server.LastDictionarySurfaces, Is.EqualTo(word.Phrases.Select(x => x.Surface)));
        Assert.That(server.LastDictionaryPronunciations, Is.EqualTo(word.Phrases.Select(x => x.Pronunciation)));
        Assert.That(server.LastDictionaryAccents, Is.EqualTo(word.Phrases.Select(x => x.AccentType.ToString())));
        var loaded = (await VoiceDictionaryService.LoadAsync(engine)).Single();
        Assert.That(server.CompoundDictionaryRequested, Is.EqualTo(aivis));
        Assert.That(loaded.Id, Is.EqualTo(id));
        Assert.That(loaded.Phrases, Is.EqualTo(word.Phrases));

        var updated = VoiceDictionaryWord.Create(id, loaded.Phrases, loaded.WordType!, 9, aivis, loaded.Data);
        Assert.That(await VoiceDictionaryService.SaveAsync(engine, id, updated), Is.EqualTo(id));
        Assert.That((await VoiceDictionaryService.LoadAsync(engine)).Single().Priority, Is.EqualTo(9));
        updated.Data["priority"] = 1;
        updated.Data["future_field"] = "retained";
        await VoiceDictionaryService.ImportAsync(engine, [updated], overwrite: false);
        Assert.That((await VoiceDictionaryService.LoadAsync(engine)).Single().Priority, Is.EqualTo(9));
        await VoiceDictionaryService.ImportAsync(engine, [updated], overwrite: true);
        loaded = (await VoiceDictionaryService.LoadAsync(engine)).Single();
        Assert.That(loaded.Priority, Is.EqualTo(1));
        Assert.That((string?)loaded.Data["future_field"], Is.EqualTo("retained"));

        var voice = engine.Voices.Single();
        int writes = server.DictionaryWriteCount;
        var result = await VoiceDictionaryService.PreviewAsync(engine, updated, new()
        {
            EngineId = engine.Id, SpeakerUuid = voice.SpeakerUuid, StyleId = voice.Styles[0].Id
        });
        Assert.That(result.Wave, Is.Not.Empty);
        Assert.That(server.DictionaryWriteCount, Is.EqualTo(writes), "Preview must not mutate the engine dictionary.");
        Assert.That(server.LastPreviewKana, Is.EqualTo(DictionaryKana.Preview(word.Phrases).Kana));
        Assert.That((string?)server.LastQuery!["kana"], Is.EqualTo(DictionaryKana.Preview(word.Phrases).Text));
        var longVowelWord = VoiceDictionaryWord.Create(Guid.NewGuid(), [new("珈琲", "コーヒー", 0)], DictionaryWordType.All[0], 5, aivis);
        await VoiceDictionaryService.PreviewAsync(engine, longVowelWord, new()
        {
            EngineId = engine.Id, SpeakerUuid = voice.SpeakerUuid, StyleId = voice.Styles[0].Id
        });
        Assert.That(server.LastPreviewKana, Is.EqualTo("コオヒイ'"));
        Assert.That(server.DictionaryWriteCount, Is.EqualTo(writes));
        if (aivis) Assert.That((string?)server.LastQuery!["kana"], Is.EqualTo("コーヒー"));
        await VoiceDictionaryService.DeleteAsync(engine, id);
        Assert.That(await VoiceDictionaryService.LoadAsync(engine), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Changed_connections_do_not_send_dictionary_edits_to_a_stale_endpoint(bool deleted)
    {
        using var old = new HttpVoiceEngineTests.EngineServer();
        using var next = new HttpVoiceEngineTests.EngineServer();
        var engine = await Connect(old);
        if (deleted) VoiceEngineStore.Delete(engine.Id);
        else VoiceEngineStore.Save(engine with { Url = next.Url });
        await ExpectFailure<InvalidOperationException>(() => VoiceDictionaryService.SaveAsync(engine, null, Word()));
        Assert.That(old.DictionaryWriteCount, Is.Zero);
        Assert.That(next.DictionaryWriteCount, Is.Zero);
        if (!deleted)
        {
            var current = VoiceEngineStore.Load().Single();
            await VoiceDictionaryService.SaveAsync(current, null, Word());
            Assert.That(next.DictionaryWriteCount, Is.EqualTo(1));
        }
    }

    [AvaloniaTest]
    public async Task Dirty_drafts_require_save_or_discard_and_dictionary_engine_selection_is_independent()
    {
        using var first = new HttpVoiceEngineTests.EngineServer();
        using var second = new HttpVoiceEngineTests.EngineServer(aivis: true);
        var firstEngine = await Connect(first);
        var secondEngine = await Connect(second);
        using var editor = new TestEditor(Path.Combine(_directory, "scene.bescene"));
        using var speech = new TtsTabViewModel(new(), editor);
        speech.SelectedEngine.Value = firstEngine;
        var model = speech.Dictionary;
        await model.ChooseEngine(firstEngine);
        model.Phrases[0].Surface.Value = "保存する単語";
        model.Phrases[0].Pronunciation.Value = "テスト";
        Assert.That(model.IsDirty.Value, Is.True);
        await model.ChooseEngine(secondEngine);
        Assert.That(model.ConfirmChange.Value, Is.True);
        Assert.That(model.SelectedEngine.Value.Id, Is.EqualTo(firstEngine.Id));
        await model.SaveAndContinue();
        Assert.That(model.SelectedEngine.Value.Id, Is.EqualTo(secondEngine.Id), model.Message.Value);
        Assert.That(speech.SelectedEngine.Value.Id, Is.EqualTo(firstEngine.Id));
        Assert.That(first.Dictionary, Has.Count.EqualTo(1));
        Assert.That(second.Dictionary, Is.Empty);
        Assert.That(model.IsDirty.Value, Is.False);
        model.Phrases[0].Surface.Value = "破棄する単語";
        await model.NewWord();
        Assert.That(model.ConfirmChange.Value, Is.True);
        model.CancelChange();
        Assert.That(model.Phrases[0].Surface.Value, Is.EqualTo("破棄する単語"));
        await model.Refresh();
        await model.DiscardAndContinue();
        Assert.That(model.Phrases[0].Surface.Value, Is.Empty);
        Assert.That(model.IsDirty.Value, Is.False);
    }

    [AvaloniaTest]
    public async Task Switching_engines_cancels_a_pending_read_and_ignores_the_old_response()
    {
        using var first = new HttpVoiceEngineTests.EngineServer { PauseAt = "/tts/user_dict" };
        using var second = new HttpVoiceEngineTests.EngineServer();
        var firstEngine = await Connect(first);
        var secondEngine = await Connect(second);
        var old = Word();
        first.Dictionary[old.Id.ToString()] = old.Data;
        using var model = new VoiceDictionaryViewModel();
        var pending = model.ChooseEngine(firstEngine);
        try
        {
            await first.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.ChooseEngine(secondEngine);
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(first.Resume.Task.IsCompleted, Is.False);
            Assert.That(model.SelectedEngine.Value.Id, Is.EqualTo(secondEngine.Id));
            Assert.That(model.VisibleWords.Value, Is.Empty);
            Assert.That(model.IsBusy.Value, Is.False);
        }
        finally { first.Resume.TrySetResult(); }
    }

    [AvaloniaTest]
    [TestCase(403)]
    [TestCase(422)]
    [TestCase(503)]
    public async Task Failed_writes_keep_the_draft_and_can_be_retried(int status)
    {
        using var server = new HttpVoiceEngineTests.EngineServer { DictionaryWriteStatus = status };
        var engine = await Connect(server);
        using var model = new VoiceDictionaryViewModel();
        await model.ChooseEngine(engine);
        model.Phrases[0].Surface.Value = "失わない単語";
        model.Phrases[0].Pronunciation.Value = "テスト";
        Assert.That(await model.Save(), Is.False);
        Assert.That(model.IsDirty.Value, Is.True);
        Assert.That(model.Phrases[0].Surface.Value, Is.EqualTo("失わない単語"));
        Assert.That(server.Dictionary, Is.Empty);
        Assert.That(model.IsReadOnly.Value, Is.EqualTo(status == 403));
        server.DictionaryWriteStatus = 204;
        if (status == 403)
        {
            await model.Refresh();
            await model.DiscardAndContinue();
            Assert.That(model.CanEdit.Value, Is.True);
            model.Phrases[0].Surface.Value = "もう一度保存";
            model.Phrases[0].Pronunciation.Value = "テスト";
        }
        Assert.That(await model.Save(), Is.True, model.Message.Value);
        Assert.That(server.Dictionary, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    [TestCase(404)]
    [TestCase(501)]
    [TestCase(503)]
    public async Task Unsupported_and_unavailable_dictionaries_recover_on_reload(int status)
    {
        using var server = new HttpVoiceEngineTests.EngineServer { DictionaryStatus = status };
        var engine = await Connect(server);
        using var model = new VoiceDictionaryViewModel();
        await model.ChooseEngine(engine);
        Assert.That(model.HasDictionary.Value, Is.False);
        Assert.That(model.Message.Value, Does.Contain(status is 404 or 501 ? "対応していません" : "HTTP 503"));
        server.DictionaryStatus = 200;
        await model.Refresh();
        Assert.That(model.HasDictionary.Value, Is.True, model.Message.Value);
        Assert.That(model.CanEdit.Value, Is.True);
    }

    [AvaloniaTest]
    public async Task Dictionary_updates_preserve_manual_accents_until_explicit_reanalysis()
    {
        using var server = new HttpVoiceEngineTests.EngineServer();
        using var editor = new TestEditor(Path.Combine(_directory, "scene.bescene"));
        using var speech = new TtsTabViewModel(new(), editor);
        Assert.That(await speech.SaveConnection("Test", server.Url), Is.True);
        speech.SelectedVoice.Value = speech.Voice.Value.Single();
        speech.Text.Value = "試験です。";
        await speech.CreateQuery();
        var query = speech.AudioQuery.Value!;
        query.AccentPhrases[0].Moras[0].Pitch = 7;
        VoiceDictionaryService.NotifyChanged("other-engine");
        Assert.That(speech.DictionaryNotice.Value, Is.Empty);
        VoiceDictionaryService.NotifyChanged(speech.SelectedEngine.Value!.Id);
        Assert.That(speech.AudioQuery.Value, Is.SameAs(query));
        Assert.That(speech.AudioQuery.Value!.AccentPhrases[0].Moras[0].Pitch, Is.EqualTo(7));
        Assert.That(speech.DictionaryNotice.Value, Does.Contain("テキストを解析"));
        await speech.CreateQuery();
        Assert.That(speech.AudioQuery.Value, Is.Not.SameAs(query));
        Assert.That(speech.DictionaryNotice.Value, Is.Empty);
    }

    [AvaloniaTest]
    public async Task Reloading_connections_reconciles_the_selected_engine_without_resetting_the_draft()
    {
        using var server = new HttpVoiceEngineTests.EngineServer();
        var engine = await Connect(server);
        using var model = new VoiceDictionaryViewModel();
        await model.ChooseEngine(engine);
        await model.NewWord();
        var draft = model.Phrases.Single();
        draft.Surface.Value = "編集中の単語";
        draft.Pronunciation.Value = "ヘンシュウチュウノタンゴ";
        var view = new VoiceDictionaryView { DataContext = model };
        var window = new Window { Width = 320, Height = 640, Content = view };
        try
        {
            window.Show();
            await Settle();
            var selector = view.FindControl<ComboBox>("DictionaryEngineSelector")!;
            void AssertDraftAndSelection(string name)
            {
                Assert.That(selector.SelectedItem, Is.SameAs(model.SelectedEngine.Value));
                Assert.That(model.Engines.Value.Any(x => ReferenceEquals(x, model.SelectedEngine.Value)), Is.True);
                Assert.That(model.SelectedEngine.Value.Name, Is.EqualTo(name));
                Assert.That(model.Phrases.Single(), Is.SameAs(draft));
                Assert.That(draft.Surface.Value, Is.EqualTo("編集中の単語"));
                Assert.That(draft.Pronunciation.Value, Is.EqualTo("ヘンシュウチュウノタンゴ"));
                Assert.That(model.IsEditing.Value, Is.True);
                Assert.That(model.IsDirty.Value, Is.True);
                Assert.That(model.ConfirmChange.Value, Is.False);
            }

            await model.Activate();
            await Settle();
            Capture(window, "dictionary-engine-reloaded");
            AssertDraftAndSelection(engine.Name);
            var refreshed = model.Engines.Value.Single(x => x.Id == engine.Id);
            selector.SelectedItem = null;
            selector.SelectedItem = refreshed;
            await Settle();
            AssertDraftAndSelection(engine.Name);

            VoiceEngineStore.Save(engine with { Name = "名前を変更したエンジン" });
            await Settle();
            AssertDraftAndSelection("名前を変更したエンジン");
            Capture(window, "dictionary-engine-renamed");

            var retained = model.SelectedEngine.Value;
            VoiceEngineStore.Delete(engine.Id);
            await Settle();
            Assert.That(model.SelectedEngine.Value, Is.SameAs(retained));
            AssertDraftAndSelection(retained.Name);

            var moved = engine with { Url = engine.Url + "moved/" };
            VoiceEngineStore.Save(moved);
            await Settle();
            AssertDraftAndSelection(retained.Name);
            Assert.That(model.Engines.Value.Any(x => x.Id == moved.Id && x.Url == moved.Url), Is.True);
            selector.SelectedItem = model.Engines.Value.Single(x => x.Id == moved.Id && x.Url == moved.Url);
            await Settle();
            Assert.That(model.ConfirmChange.Value, Is.True);
            model.CancelChange();
            AssertDraftAndSelection(retained.Name);
            Assert.That(server.DictionaryWriteCount, Is.Zero);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Voice_selectors_keep_the_selected_speaker_and_style_after_switching_and_refreshing()
    {
        using var server = new HttpVoiceEngineTests.EngineServer
        {
            Speakers =
            [
                new() { Name = "話者", SpeakerUuid = "first", Version = "1", Styles =
                    [new() { Id = 11, Name = "通常" }, new() { Id = 12, Name = "喜び" }] },
                new() { Name = "話者", SpeakerUuid = "second", Version = "1", Styles =
                    [new() { Id = 21, Name = "通常" }, new() { Id = 22, Name = "ささやき" }] }
            ]
        };
        var engine = await Connect(server);
        using var model = new VoiceDictionaryViewModel();
        await model.ChooseEngine(engine);
        await model.NewWord();
        var view = new VoiceDictionaryView { DataContext = model };
        var window = new Window { Width = 320, Height = 640, Content = view };
        try
        {
            window.Show();
            view.FindControl<Expander>("DictionaryPreviewSettings")!.IsExpanded = true;
            await Settle();
            var voice = view.FindControl<ComboBox>("DictionaryVoiceSelector")!;
            var style = view.FindControl<ComboBox>("DictionaryStyleSelector")!;
            style.SelectedIndex = 1;
            Assert.That(model.SelectedStyle.Value?.Id, Is.EqualTo(12));
            voice.SelectedIndex = 1;
            Assert.That(model.SelectedVoice.Value?.SpeakerUuid, Is.EqualTo("second"));
            Assert.That(style.Items.Cast<VoiceStyle>().Select(x => x.Id), Is.EqualTo(new long[] { 21, 22 }));
            style.SelectedIndex = 1;
            Assert.That(model.SelectedStyle.Value?.Id, Is.EqualTo(22));
            await model.Refresh();
            await Settle();
            Assert.That(model.SelectedVoice.Value?.SpeakerUuid, Is.EqualTo("second"));
            Assert.That(model.SelectedStyle.Value?.Id, Is.EqualTo(22));
            Assert.That(voice.SelectedItem, Is.SameAs(model.SelectedVoice.Value));
            Assert.That(style.SelectedItem, Is.SameAs(model.SelectedStyle.Value));
            var previewSettings = view.FindControl<Expander>("DictionaryPreviewSettings")!;
            previewSettings.IsExpanded = false;
            await Settle();
            var previewVoice = view.FindControl<TextBlock>("DictionaryPreviewVoice")!;
            Assert.That(previewVoice.IsEffectivelyVisible, Is.True);
            Assert.That(previewVoice.Text, Is.EqualTo("話者 · ささやき"));
            previewSettings.IsExpanded = true;
            voice.SelectedIndex = 0;
            Assert.That(model.SelectedStyle.Value?.Id, Is.EqualTo(11));
            server.Speakers[0] = new()
            {
                Name = "話者", SpeakerUuid = "first", Version = "2",
                Styles = [new() { Id = 12, Name = "喜び" }, new() { Id = 11, Name = "通常" }]
            };
            await model.Refresh();
            await Settle();
            Assert.That(model.SelectedVoice.Value?.SpeakerUuid, Is.EqualTo("first"));
            Assert.That(model.SelectedStyle.Value?.Id, Is.EqualTo(11));
            Assert.That(style.SelectedIndex, Is.EqualTo(1));
            Assert.That(previewVoice.Text, Is.EqualTo("話者 · 通常"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Dictionary_accent_bars_follow_reading_and_numeric_edits_and_save_each_phrase()
    {
        using var server = new HttpVoiceEngineTests.EngineServer(aivis: true);
        var word = Compound();
        server.Dictionary[word.Id.ToString()] = word.Data;
        var engine = await Connect(server);
        using var model = new VoiceDictionaryViewModel();
        await model.ChooseEngine(engine);
        await model.ChooseWord(model.VisibleWords.Value.Single());
        var view = new VoiceDictionaryView { DataContext = model };
        var window = new Window { Width = 320, Height = 700, Content = view };
        try
        {
            window.Show();
            await Settle();
            AccentPhrasePanel PanelFor(int index) => view.GetVisualDescendants().OfType<AccentPhrasePanel>()
                .Single(x => ReferenceEquals(x.DataContext, model.Phrases[index]));
            void ClickMora(AccentPhrasePanel panel, int index)
            {
                var point = panel.TranslatePoint(new Point(index * 36 + 18, panel.Bounds.Height - 40), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
            }

            var first = PanelFor(0);
            Assert.That(first.ShowPitch, Is.False);
            Assert.That(model.IsDirty.Value, Is.False, "Displaying an accent must not alter the dictionary draft.");
            var reading = view.GetVisualDescendants().OfType<TextBox>().Single(x =>
                x.Name == "DictionaryReading" && ReferenceEquals(x.DataContext, model.Phrases[0]));
            reading.Text = "キョーリュー";
            await Settle();
            Assert.That(model.Phrases[0].MoraCount.Value, Is.EqualTo(4));
            Assert.That(first.AccentPhrases!.Single().Moras.Select(x => x.Text), Is.EqualTo(new[] { "キョ", "ー", "リュ", "ー" }));
            first.BringIntoView();
            await Settle();
            ClickMora(first, 2);
            await Settle();
            var numeric = view.GetVisualDescendants().OfType<NumericUpDown>()
                .Single(x => ReferenceEquals(x.DataContext, model.Phrases[0]));
            Assert.That(model.Phrases[0].Accent.Value, Is.EqualTo(3));
            Assert.That(numeric.Value, Is.EqualTo(3));
            numeric.Value = 0;
            await Settle();
            Assert.That(model.Phrases[0].Accent.Value, Is.Zero);
            Assert.That(first.AccentPhrases!.Single().Accent, Is.Zero);

            reading.Text = string.Concat(Enumerable.Repeat("キャ", 12));
            await Settle();
            var scroll = first.FindAncestorOfType<ScrollViewer>()!;
            Assert.That(scroll.Extent.Width, Is.GreaterThan(scroll.Viewport.Width), "Long readings remain accessible by scrolling.");
            scroll.Offset = new Vector(128, 0);
            await Settle();
            Assert.That(scroll.Offset.X, Is.GreaterThan(0));
            Capture(window, "dictionary-long-accent");
            reading.Text = "";
            await Settle();
            Assert.That(first.AccentPhrases, Is.Empty);
            reading.Text = "キョーリュー";
            await Settle();
            Assert.That(first.AccentPhrases!.Single().Accent, Is.Zero);

            var second = PanelFor(1);
            second.BringIntoView();
            await Settle();
            ClickMora(second, 3);
            await Settle();
            Assert.That(model.Phrases.Select(x => x.Accent.Value), Is.EqualTo(new[] { 0, 4 }));
            Assert.That(second.AccentPhrases!.Single().Accent, Is.EqualTo(4));
            Assert.That(server.DictionaryWriteCount, Is.Zero);
            Assert.That(server.MoraPitchCount, Is.Zero, "Dictionary accents do not require per-utterance pitch editing.");
            Capture(window, "dictionary-accent-edited");
            Assert.That(await model.Save(), Is.True, model.Message.Value);
            Assert.That(server.LastDictionaryAccents, Is.EqualTo(new[] { "0", "4" }));
            Assert.That(model.VisibleWords.Value.Single().Phrases.Select(x => x.AccentType), Is.EqualTo(new[] { 0, 4 }));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task List_navigation_retains_search_and_scroll_and_confirms_unsaved_changes()
    {
        using var server = new HttpVoiceEngineTests.EngineServer();
        for (int i = 0; i < 50; i++)
        {
            var word = Word();
            word.Data["surface"] = $"単語 {i:00}";
            server.Dictionary[word.Id.ToString()] = word.Data;
        }
        var engine = await Connect(server);
        using var model = new VoiceDictionaryViewModel();
        await model.ChooseEngine(engine);
        model.Search.Value = "単語";
        var view = new VoiceDictionaryView { DataContext = model };
        var window = new Window { Width = 320, Height = 640, Content = view };
        try
        {
            window.Show();
            await Settle();
            var list = view.FindControl<ListBox>("DictionaryWords")!;
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().Single();
            scroll.Offset = new Vector(0, 360);
            await Settle();
            double offset = scroll.Offset.Y;
            Assert.That(offset, Is.GreaterThan(0));
            var listTop = list.TranslatePoint(default, window)!.Value.Y;
            var item = list.GetVisualDescendants().OfType<ListBoxItem>().First(x =>
            {
                double top = x.TranslatePoint(default, window)!.Value.Y;
                return top >= listTop && top + x.Bounds.Height <= window.Bounds.Height;
            });
            var selected = (VoiceDictionaryWord)item.DataContext!;
            Capture(window, "dictionary-list-scrolled");
            Click(window, item);
            await Settle();
            Assert.That(model.IsEditing.Value, Is.True);
            Assert.That(model.SelectedWord.Value!.Id, Is.EqualTo(selected.Id));
            model.Phrases[0].Surface.Value += "編集中";
            await model.Activate();
            Assert.That(model.IsDirty.Value, Is.True);
            Assert.That(model.ConfirmChange.Value, Is.False, "Returning to the tab retains the open editor.");
            Click(window, view.FindControl<Button>("BackToDictionaryButton")!);
            await Settle();
            Assert.That(model.ConfirmChange.Value, Is.True);
            model.CancelChange();
            Assert.That(model.IsEditing.Value, Is.True);
            Assert.That(model.Phrases[0].Surface.Value, Does.EndWith("編集中"));
            await model.BackToList();
            await model.DiscardAndContinue();
            await Settle();
            Assert.That(model.IsListing.Value, Is.True);
            Assert.That(model.Search.Value, Is.EqualTo("単語"));
            Assert.That(scroll.Offset.Y, Is.EqualTo(offset).Within(1));
            Assert.That(server.DictionaryWriteCount, Is.Zero);
            item = list.GetVisualDescendants().OfType<ListBoxItem>().Single(x =>
                x.DataContext is VoiceDictionaryWord word && word.Id == selected.Id);
            Click(window, item);
            await Settle();
            Assert.That(model.IsEditing.Value, Is.True, "An already-selected row must open again.");
            model.Phrases[0].Surface.Value += "更新";
            await model.BackToList();
            await model.SaveAndContinue();
            await Settle();
            Assert.That(model.IsListing.Value, Is.True);
            Assert.That(model.IsDirty.Value, Is.False);
            Assert.That(model.Search.Value, Is.EqualTo("単語"));
            Assert.That(scroll.Offset.Y, Is.EqualTo(offset).Within(1));
            Assert.That(server.DictionaryWriteCount, Is.EqualTo(1));
            Assert.That((string?)server.Dictionary[selected.Id.ToString()]!["surface"], Does.EndWith("更新"));
            Click(window, view.FindControl<Button>("NewDictionaryWordButton")!);
            await Settle();
            Assert.That(model.IsEditing.Value, Is.True);
            Assert.That(model.SelectedWord.Value, Is.Null);
            Assert.That(model.Phrases[0].Surface.Value, Is.Empty);
            await model.BackToList();
            await Settle();
            Assert.That(model.IsListing.Value, Is.True);
            Assert.That(model.Search.Value, Is.EqualTo("単語"));
            Assert.That(scroll.Offset.Y, Is.EqualTo(offset).Within(1));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void Legacy_settings_page_and_new_page_ids_restore_correctly()
    {
        using var editor = new TestEditor(Path.Combine(_directory, "scene.bescene"));
        using var speech = new TtsTabViewModel(new(), editor);
        speech.ReadFromJson(new() { ["SelectedPage"] = 2, ["VoiceSettings"] = JsonSerializer.SerializeToNode(new VoiceSettings()) });
        Assert.That(speech.SelectedPage.Value, Is.EqualTo(3));
        foreach (int page in Enumerable.Range(0, 4))
        {
            speech.SelectedPage.Value = page;
            var state = new JsonObject();
            speech.WriteToJson(state);
            speech.SelectedPage.Value = 0;
            speech.ReadFromJson(state);
            Assert.That(speech.SelectedPage.Value, Is.EqualTo(page));
        }
    }

    [AvaloniaTest]
    [TestCase(320, false)]
    [TestCase(320, true)]
    [TestCase(460, false)]
    [TestCase(460, true)]
    public async Task Dictionary_ui_renders_edits_searches_and_reviews_imports(int width, bool light)
    {
        using var server = new HttpVoiceEngineTests.EngineServer(aivis: true);
        var engine = await Connect(server);
        var word = Compound();
        server.Dictionary[word.Id.ToString()] = word.Data;
        using var editor = new TestEditor(Path.Combine(_directory, "scene.bescene"));
        using var speech = new TtsTabViewModel(new(), editor);
        var model = speech.Dictionary;
        await model.ChooseEngine(engine);
        speech.SelectedPage.Value = 2;
        var view = new TtsTabView { DataContext = speech };
        var window = new Window { Width = width, Height = 700, Content = view,
            RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        try
        {
            window.Show();
            await Settle();
            for (int i = 0; i < 100 && model.IsBusy.Value; i++) await Settle();
            Assert.That(view.FindControl<VoiceDictionaryView>("DictionaryPage")!.DataContext, Is.SameAs(model));
            var page = view.FindControl<VoiceDictionaryView>("DictionaryPage")!;
            async Task OpenWord()
            {
                await Settle();
                var item = page.FindControl<ListBox>("DictionaryWords")!.GetVisualDescendants().OfType<ListBoxItem>()
                    .Single(x => x.DataContext is VoiceDictionaryWord entry && entry.Id == word.Id);
                Click(window, item);
                await Settle();
                Assert.That(model.IsEditing.Value, Is.True);
                Assert.That(page.FindControl<ListBox>("DictionaryWords")!.IsEffectivelyVisible, Is.False);
                Assert.That(page.FindControl<Grid>("DictionaryEditorPage")!.GetVisualDescendants().OfType<Button>()
                    .Any(x => x.Name == "DeleteDictionaryWordButton"), Is.False);
                var fields = page.FindControl<Grid>("DictionaryEditorPage")!.GetVisualDescendants().OfType<TextBox>().ToArray();
                var surface = fields.First(x => x.Name == "DictionarySurface");
                var reading = fields.First(x => x.Name == "DictionaryReading");
                var surfacePosition = surface.TranslatePoint(default, window)!.Value;
                var readingPosition = reading.TranslatePoint(default, window)!.Value;
                if (width < 380)
                {
                    Assert.That(surface.Bounds.Width, Is.GreaterThan(width * .8));
                    Assert.That(readingPosition.X, Is.EqualTo(surfacePosition.X).Within(1));
                    Assert.That(readingPosition.Y, Is.GreaterThan(surfacePosition.Y + surface.Bounds.Height));
                }
                else
                {
                    Assert.That(readingPosition.X, Is.GreaterThan(surfacePosition.X + surface.Bounds.Width));
                    Assert.That(readingPosition.Y, Is.EqualTo(surfacePosition.Y).Within(1));
                }
                Assert.That(page.FindControl<TextBlock>("DictionaryPreviewVoice")!.IsEffectivelyVisible, Is.True);
            }
            var details = page.FindControl<Expander>("DictionaryDetails")!;
            var previewSettings = page.FindControl<Expander>("DictionaryPreviewSettings")!;
            Assert.That(details.IsExpanded, Is.False);
            Assert.That(previewSettings.IsExpanded, Is.False);
            Assert.That(model.IsListing.Value, Is.True);
            Assert.That(page.FindControl<ListBox>("DictionaryWords")!.Bounds.Height, Is.GreaterThan(window.Bounds.Height / 2));
            Assert.That(page.FindControl<Button>("SaveDictionaryWordButton")!.IsEffectivelyVisible, Is.False);
            Assert.That(model.Message.Value, Is.Empty);
            Assert.That(page.FindControl<Button>("SaveDictionaryWordButton")!.IsEnabled, Is.False);
            Assert.That(model.SelectedStyle.Value?.Id, Is.EqualTo(7));
            Assert.That(page.FindControl<ComboBox>("DictionaryStyleSelector")!.SelectedItem, Is.SameAs(model.SelectedStyle.Value));
            page.FindControl<TextBox>("DictionarySearch")!.Text = "マッケン";
            Assert.That(model.VisibleWords.Value, Has.Length.EqualTo(1));
            page.FindControl<TextBox>("DictionarySearch")!.Text = "一致しない単語";
            Assert.That(model.VisibleWords.Value, Is.Empty);
            await Settle();
            Assert.That(page.FindControl<TextBlock>("DictionaryEmptyState")!.IsEffectivelyVisible, Is.True);
            Capture(window, $"dictionary-search-empty-{width}-{(light ? "light" : "dark")}");
            Click(window, page.FindControl<Button>("ClearDictionarySearchButton")!);
            await Settle();
            Assert.That(model.Search.Value, Is.Empty);
            Assert.That(model.VisibleWords.Value, Has.Length.EqualTo(1));
            Assert.That(page.FindControl<TextBlock>("DictionaryEmptyState")!.IsEffectivelyVisible, Is.False);
            Capture(window, $"dictionary-{width}-{(light ? "light" : "dark")}");
            var menuButton = page.FindControl<Button>("DictionaryMenuButton")!;
            var menuPosition = menuButton.TranslatePoint(new Point(menuButton.Bounds.Width / 2, menuButton.Bounds.Height / 2), window)!.Value;
            window.MouseDown(menuPosition, MouseButton.Left);
            window.MouseUp(menuPosition, MouseButton.Left);
            await Settle();
            var menu = (FAMenuFlyout)menuButton.Flyout!;
            Assert.That(menu.IsOpen, Is.True);
            Assert.That(menu.Items.OfType<FAMenuFlyoutItem>().Select(x => x.Text),
                Is.EqualTo(new[] { "再読み込み", "インポート…", "エクスポート…" }));
            Capture(TopLevel.GetTopLevel(menu.Popup!.Child)!, $"dictionary-menu-{width}-{(light ? "light" : "dark")}");
            menu.Hide();
            await OpenWord();
            Assert.That(model.Phrases, Has.Count.EqualTo(2));
            Capture(window, $"dictionary-editor-default-{width}-{(light ? "light" : "dark")}");
            details.IsExpanded = true;
            previewSettings.IsExpanded = true;
            await Task.Delay(250);
            await Settle();
            page.FindControl<Button>("SaveDictionaryWordButton")!.BringIntoView();
            await Settle();
            Capture(window, $"dictionary-edit-{width}-{(light ? "light" : "dark")}");
            details.BringIntoView();
            await Settle();
            Capture(window, $"dictionary-settings-{width}-{(light ? "light" : "dark")}");
            Assert.That(page.FindControl<ComboBox>("DictionaryWordTypeSelector")!.TranslatePoint(default, window)!.Value.Y,
                Is.LessThan(page.FindControl<Border>("DictionaryEditorFooter")!.TranslatePoint(default, window)!.Value.Y));
            Assert.That(model.IsDirty.Value, Is.False, "Rendering NumericUpDown must not alter stored accent positions.");
            model.Phrases[1].Accent.Value = 2;
            Assert.That(model.IsDirty.Value, Is.True);
            Click(window, page.FindControl<Button>("BackToDictionaryButton")!);
            await Settle();
            Assert.That(model.ConfirmChange.Value, Is.True);
            Assert.That(model.IsEditing.Value, Is.True);
            Capture(window, $"dictionary-unsaved-{width}-{(light ? "light" : "dark")}");
            model.CancelChange();
            Assert.That(model.Phrases[1].Accent.Value, Is.EqualTo(2));
            var save = page.FindControl<Button>("SaveDictionaryWordButton")!;
            var click = save.TranslatePoint(new Point(save.Bounds.Width / 2, save.Bounds.Height / 2), window)!.Value;
            window.MouseDown(click, MouseButton.Left);
            window.MouseUp(click, MouseButton.Left);
            for (int i = 0; i < 100 && model.IsBusy.Value; i++) await Settle();
            Assert.That(model.IsDirty.Value, Is.False, model.Message.Value);
            Assert.That(model.Message.Value, Is.Empty);
            Assert.That(save.IsEnabled, Is.False);
            Assert.That(model.IsListing.Value, Is.True);
            Assert.That(save.IsEffectivelyVisible, Is.False);
            Assert.That(model.SelectedStyle.Value?.Id, Is.EqualTo(7), "Reloading the dictionary must retain a playable style.");
            Assert.That(server.LastDictionaryAccents, Is.EqualTo(new[] { "1", "2" }));
            var imported = Word();
            await model.PrepareImport(VoiceDictionaryService.Export([imported, word]));
            await Settle();
            page.FindControl<Border>("DictionaryImportReview")!.BringIntoView();
            await Settle();
            Capture(window, $"dictionary-import-{width}-{(light ? "light" : "dark")}");
            Assert.That(model.ImportSummary.Value, Does.Contain("重複 1件"));
            Assert.That(model.IsEditing.Value, Is.False);
            Assert.That(save.IsEffectivelyVisible, Is.False);
            foreach (var control in page.GetVisualDescendants().OfType<Control>()
                         .Where(x => x.IsEffectivelyVisible && x is TextBox or ComboBox or NumericUpDown))
            {
                var origin = control.TranslatePoint(default, window)!.Value;
                Assert.That(origin.X, Is.GreaterThanOrEqualTo(0), control.ToString());
                Assert.That(origin.X + control.Bounds.Width, Is.LessThanOrEqualTo(width + 1), control.ToString());
            }
            await model.Import();
            Assert.That(server.Dictionary, Has.Count.EqualTo(2));
            await Settle();
            var deleteItem = page.FindControl<ListBox>("DictionaryWords")!.GetVisualDescendants().OfType<ListBoxItem>()
                .Single(x => x.DataContext is VoiceDictionaryWord entry && entry.Id == imported.Id);
            var rowDelete = deleteItem.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "DeleteDictionaryWordButton");
            Assert.That(rowDelete.IsEnabled, Is.True);
            Assert.That(model.SelectedWord.Value!.Id, Is.EqualTo(word.Id));
            int writes = server.DictionaryWriteCount;
            double rowY = deleteItem.TranslatePoint(default, window)!.Value.Y;
            Click(window, rowDelete);
            await Settle();
            Assert.That(model.WordToDelete.Value?.Id, Is.EqualTo(imported.Id));
            Assert.That(model.SelectedWord.Value!.Id, Is.EqualTo(word.Id));
            Assert.That(model.IsListing.Value, Is.True, "The delete button must not open the word editor.");
            Assert.That(deleteItem.TranslatePoint(default, window)!.Value.Y, Is.EqualTo(rowY).Within(1),
                "Confirming deletion must not shift the list rows.");
            Assert.That(server.DictionaryWriteCount, Is.EqualTo(writes));
            var cancelDelete = page.FindControl<Button>("CancelDeleteDictionaryWordButton")!;
            Click(window, cancelDelete);
            await Settle();
            Assert.That(model.WordToDelete.Value, Is.Null);
            Assert.That(server.Dictionary, Has.Count.EqualTo(2));
            foreach (var key in new[] { PhysicalKey.Enter, PhysicalKey.Space })
            {
                Assert.That(rowDelete.Focus(), Is.True);
                window.KeyPressQwerty(key, RawInputModifiers.None);
                window.KeyReleaseQwerty(key, RawInputModifiers.None);
                await Settle();
                Assert.That(model.WordToDelete.Value?.Id, Is.EqualTo(imported.Id));
                Assert.That(model.IsListing.Value, Is.True);
                Click(window, cancelDelete);
                await Settle();
            }
            Click(window, rowDelete);
            await Settle();
            var delete = page.FindControl<Button>("ConfirmDeleteDictionaryWordButton")!;
            var deletePosition = delete.TranslatePoint(new Point(delete.Bounds.Width / 2, delete.Bounds.Height / 2), window)!.Value;
            Assert.That(deletePosition.Y, Is.InRange(0, window.Bounds.Height));
            Capture(window, $"dictionary-delete-{width}-{(light ? "light" : "dark")}");
            window.MouseDown(deletePosition, MouseButton.Left);
            window.MouseUp(deletePosition, MouseButton.Left);
            for (int i = 0; i < 100 && model.IsBusy.Value; i++) await Settle();
            Assert.That(server.Dictionary, Has.Count.EqualTo(1), model.Message.Value);
            Assert.That(server.Dictionary.ContainsKey(imported.Id.ToString()), Is.False);
            Assert.That(server.Dictionary.ContainsKey(word.Id.ToString()), Is.True);
            Assert.That(model.WordToDelete.Value, Is.Null);
            Assert.That(model.IsListing.Value, Is.True);
            await OpenWord();
            await model.BackToList();
            await model.NewWord();
            details.IsExpanded = false;
            previewSettings.IsExpanded = false;
            model.Phrases[0].Surface.Value = "珈琲";
            model.Phrases[0].Pronunciation.Value = "コーヒー";
            await Task.Delay(200);
            await Settle();
            Capture(window, $"dictionary-new-{width}-{(light ? "light" : "dark")}");
            await model.BackToList();
            await model.DiscardAndContinue();
            await model.NewWord();
            server.DictionaryStatus = 503;
            page.FindControl<ScrollViewer>("DictionaryScroll")!.Offset = new Vector(0, double.MaxValue);
            await model.Refresh();
            await Settle();
            Assert.That(model.Message.Value, Does.Contain("HTTP 503"));
            Assert.That(page.FindControl<TextBlock>("DictionaryStatus")!.TranslatePoint(default, window)!.Value.Y,
                Is.LessThan(page.FindControl<ScrollViewer>("DictionaryScroll")!.TranslatePoint(default, window)!.Value.Y));
            Capture(window, $"dictionary-error-{width}-{(light ? "light" : "dark")}");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task Native_dictionary_crud_restart_preview_and_failed_commit_preserve_the_saved_dictionary()
    {
        string? coreHome = Environment.GetEnvironmentVariable("VOICEVOX_TEST_HOME");
        if (coreHome == null) Assert.Ignore("Set VOICEVOX_TEST_HOME to an installed VOICEVOX Core directory.");
        var previousLoader = TtsLoader.VoiceVoxLoader.Value;
        var loader = new VoiceVoxLoader(coreHome!);
        try
        {
            await Task.Run(loader.Load);
            Assert.That(loader.IsLoaded, Is.True);
            TtsLoader.VoiceVoxLoader.Value = loader;
            using var dictionaryModel = new VoiceDictionaryViewModel();
            await dictionaryModel.Activate();
            var speakerGroups = loader.VoiceSets.SelectMany(x => x.Metadata).GroupBy(x => x.SpeakerUuid).ToArray();
            Assert.That(dictionaryModel.Voices.Value.Select(x => x.SpeakerUuid), Is.Unique);
            Assert.That(dictionaryModel.Voices.Value.Length, Is.EqualTo(speakerGroups.Length));
            foreach (var group in speakerGroups)
                Assert.That(dictionaryModel.Voices.Value.Single(x => x.SpeakerUuid == group.Key).Styles.Select(x => x.Id),
                    Is.EquivalentTo(group.SelectMany(x => x.Styles).Select(x => x.Id)));
            var selectedSpeaker = speakerGroups.FirstOrDefault(x => x.Count() > 1) ?? speakerGroups.First();
            dictionaryModel.SelectedVoice.Value = dictionaryModel.Voices.Value.Single(x => x.SpeakerUuid == selectedSpeaker.Key);
            dictionaryModel.SelectedStyle.Value = dictionaryModel.Styles.Value.Last();
            long selectedStyleId = dictionaryModel.SelectedStyle.Value.Id;
            await dictionaryModel.NewWord();
            var dictionaryView = new VoiceDictionaryView { DataContext = dictionaryModel };
            var dictionaryWindow = new Window { Width = 320, Height = 640, Content = dictionaryView };
            try
            {
                dictionaryWindow.Show();
                var previewSettings = dictionaryView.FindControl<Expander>("DictionaryPreviewSettings")!;
                previewSettings.IsExpanded = true;
                await Task.Delay(250);
                previewSettings.BringIntoView();
                await Settle();
                Assert.That(dictionaryView.FindControl<ComboBox>("DictionaryStyleSelector")!.Items.Count,
                    Is.EqualTo(selectedSpeaker.SelectMany(x => x.Styles).Count()));
                Assert.That(dictionaryView.FindControl<ComboBox>("DictionaryStyleSelector")!.SelectedItem,
                    Is.SameAs(dictionaryModel.SelectedStyle.Value));
                await dictionaryModel.Refresh();
                await Settle();
                Assert.That(dictionaryModel.SelectedVoice.Value?.SpeakerUuid, Is.EqualTo(selectedSpeaker.Key));
                Assert.That(dictionaryModel.SelectedStyle.Value?.Id, Is.EqualTo(selectedStyleId));
                dictionaryView.FindControl<ComboBox>("DictionaryStyleSelector")!.IsDropDownOpen = true;
                await Settle();
                Capture(dictionaryWindow, "dictionary-core-voices");
            }
            finally { dictionaryWindow.Close(); }
            var settings = new VoiceSettings { SpeakerUuid = dictionaryModel.SelectedVoice.Value!.SpeakerUuid, StyleId = selectedStyleId };
            var word = VoiceDictionaryWord.Create(Guid.NewGuid(), [new("架空辞書単語", "コーデックス", 1)], DictionaryWordType.All[0], 10, false);
            var id = await VoiceDictionaryService.SaveAsync(VoiceEngineConnection.Core, null, word);
            var saved = (await VoiceDictionaryService.LoadAsync(VoiceEngineConnection.Core)).Single();
            Assert.That(saved.Id, Is.EqualTo(id));
            var query = await VoiceSynthesisService.CreateQueryAsync("架空辞書単語", settings);
            Assert.That(string.Concat(query.AccentPhrases.SelectMany(x => x.Moras).Select(x => x.Text)), Does.Contain("コオデックス"));
            word = VoiceDictionaryWord.Create(id, [new("架空辞書単語", "テストジショ", 2)], DictionaryWordType.All[0], 10, false);
            await VoiceDictionaryService.SaveAsync(VoiceEngineConnection.Core, id, word);
            string original = await File.ReadAllTextAsync(VoiceDictionaryService.CorePath);
            var invalid = VoiceDictionaryWord.Create(Guid.NewGuid(), [new("無効な読み", "ッッ", 0)], DictionaryWordType.All[0], 5, false);
            await ExpectFailure<InvalidOperationException>(() => VoiceDictionaryService.SaveAsync(VoiceEngineConnection.Core, null, invalid));
            Assert.That(await File.ReadAllTextAsync(VoiceDictionaryService.CorePath), Is.EqualTo(original));

            // The file replacement can fail after native application. The previous live dictionary must be restored.
            string backup = VoiceDictionaryService.CorePath + ".backup";
            File.Move(VoiceDictionaryService.CorePath, backup);
            Directory.CreateDirectory(VoiceDictionaryService.CorePath);
            try
            {
                await ExpectFailure<IOException>(() => VoiceDictionaryService.SaveAsync(VoiceEngineConnection.Core, null, Word()));
                query = await VoiceSynthesisService.CreateQueryAsync("架空辞書単語", settings);
                Assert.That(string.Concat(query.AccentPhrases.SelectMany(x => x.Moras).Select(x => x.Text)), Does.Contain("テストジショ"));
                Assert.That(await File.ReadAllTextAsync(backup), Is.EqualTo(original));
            }
            finally
            {
                Directory.Delete(VoiceDictionaryService.CorePath);
                File.Move(backup, VoiceDictionaryService.CorePath);
            }

            var preview = await VoiceDictionaryService.PreviewAsync(VoiceEngineConnection.Core, word, settings);
            Assert.That(preview.Wave.Length, Is.GreaterThan(44));
            Assert.That(preview.Settings.SpeakerUuid, Is.EqualTo(selectedSpeaker.Key));
            Assert.That(preview.Settings.StyleId, Is.EqualTo(selectedStyleId));
            Assert.That(preview.Settings.StyleName, Is.EqualTo(dictionaryModel.SelectedStyle.Value!.Name));
            Assert.That(string.Concat(preview.Query.AccentPhrases.SelectMany(x => x.Moras).Select(x => x.Text)), Is.EqualTo("テストジショ"));
            var longVowelWord = VoiceDictionaryWord.Create(Guid.NewGuid(), [new("珈琲", "コーヒー", 0)], DictionaryWordType.All[0], 5, false);
            var longVowelPreview = await VoiceDictionaryService.PreviewAsync(VoiceEngineConnection.Core, longVowelWord, settings);
            Assert.That(longVowelPreview.Wave.Length, Is.GreaterThan(44));
            Assert.That(string.Concat(longVowelPreview.Query.AccentPhrases.SelectMany(x => x.Moras).Select(x => x.Text)), Is.EqualTo("コオヒイ"));
            foreach (int accent in new[] { 0, 1, 2, 4 })
            {
                var kana = DictionaryKana.Preview([new("珈琲", "コーヒー", accent)]).Kana;
                Assert.That(loader.Synthesizer!.CreateAudioQueryFromKana(kana, checked((uint)settings.StyleId), out string? json),
                    Is.EqualTo(ResultCode.RESULT_OK), kana);
                var accentQuery = JsonSerializer.Deserialize<AudioQueryModel>(json!)!;
                Assert.That(accentQuery.AccentPhrases.Single().Accent, Is.EqualTo(accent == 0 ? 4 : accent));
            }
            Assert.That(await File.ReadAllTextAsync(VoiceDictionaryService.CorePath), Is.EqualTo(original));
            await VoiceDictionaryService.DeleteAsync(VoiceEngineConnection.Core, id);
            Assert.That(await VoiceDictionaryService.LoadAsync(VoiceEngineConnection.Core), Is.Empty);
            var imported = VoiceDictionaryService.PrepareImport(original, VoiceEngineConnection.Core, []).Single().Word!;
            await VoiceDictionaryService.ImportAsync(VoiceEngineConnection.Core, [imported], false);
            imported.Data["priority"] = 1;
            await VoiceDictionaryService.ImportAsync(VoiceEngineConnection.Core, [imported], false);
            Assert.That((await VoiceDictionaryService.LoadAsync(VoiceEngineConnection.Core)).Single().Priority, Is.EqualTo(10));
            await VoiceDictionaryService.ImportAsync(VoiceEngineConnection.Core, [imported], true);
            Assert.That((await VoiceDictionaryService.LoadAsync(VoiceEngineConnection.Core)).Single().Priority, Is.EqualTo(1));
            loader.Unload();
            loader = new(coreHome!);
            await Task.Run(loader.Load);
            TtsLoader.VoiceVoxLoader.Value = loader;
            saved = (await VoiceDictionaryService.LoadAsync(VoiceEngineConnection.Core)).Single();
            Assert.That(saved.Id, Is.EqualTo(id));
            Assert.That(saved.Pronunciation, Is.EqualTo("テストジショ"));
            query = await VoiceSynthesisService.CreateQueryAsync("架空辞書単語", settings);
            Assert.That(string.Concat(query.AccentPhrases.SelectMany(x => x.Moras).Select(x => x.Text)), Does.Contain("テストジショ"));
        }
        finally
        {
            TtsLoader.VoiceVoxLoader.Value = previousLoader;
            loader.Unload();
        }
    }

    private static async Task ExpectFailure<T>(Func<Task> action) where T : Exception
    {
        Exception? failure = null;
        try { await action(); }
        catch (Exception ex) { failure = ex; }
        Assert.That(failure, Is.InstanceOf<T>());
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static async Task Settle()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(20);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
    }

    private static void Capture(TopLevel window, string name)
    {
        if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is not { } directory) return;
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame()!;
        frame.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions());
    }
}
