using System.Text.Json;
using System.Text.Json.Nodes;
using Beutl.Extensibility;

namespace Beutl.Extensions.Voice;

public sealed partial class VoiceMcpToolExtension
{
    private const string VoiceSchema = """
        {"type":"object","description":"Voice selection and optional tuning. AquesTalkPlayer applies prePhonemeLength, postPhonemeLength and aquesTalkPlayerVoice instead of the other scales.","properties":{
          "engineId":{"type":"string","default":"voicevox-core"},
          "speakerUuid":{"type":"string","minLength":1},
          "styleId":{"type":"integer"},
          "speedScale":{"type":"number","exclusiveMinimum":0,"default":1},
          "pitchScale":{"type":"number","default":0},
          "intonationScale":{"type":"number","minimum":0,"default":1},
          "volumeScale":{"type":"number","minimum":0,"default":1},
          "prePhonemeLength":{"type":"number","minimum":0,"default":0.1},
          "postPhonemeLength":{"type":"number","minimum":0,"default":0.1},
          "tempoDynamicsScale":{"type":"number","minimum":0,"description":"AivisSpeech tempo dynamics."},
          "aquesTalkPlayerVoice":{"type":"object","description":"AquesTalkPlayer values in the app's units, spoken through a temporary preset. Omit to speak the preset as the app has it; voice_list_voices returns each preset's values to start from.","properties":{
            "engine":{"type":"string"},"voice":{"type":"string"},"flatReading":{"type":"boolean"},
            "speed":{"type":"integer","minimum":50,"maximum":300},"volume":{"type":"integer","minimum":0,"maximum":300},
            "height":{"type":"integer","minimum":20,"maximum":200,"description":"AquesTalk10 only."},
            "accent":{"type":"integer","minimum":0,"maximum":200,"description":"AquesTalk10 only."},
            "quality":{"type":"integer","minimum":0,"maximum":200,"description":"AquesTalk10 only."},
            "frequencyScale":{"type":"integer","minimum":50,"maximum":200,"description":"Pitch by sample rate; also changes the speed."}
          },"required":["engine","voice"],"additionalProperties":false}
        },"required":["speakerUuid","styleId"],"additionalProperties":false}
        """;

    private static IReadOnlyList<McpToolDefinition> CreateTools() => Array.AsReadOnly<McpToolDefinition>(
    [
        new("voice_list_engines", "Lists VOICEVOX Core, registered HTTP voice engines and a registered AquesTalkPlayer without connecting to them.")
        {
            ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false
        },
        new("voice_list_voices", "Lists live speakers, speech styles and capabilities. AquesTalkPlayer lists each of its presets as a speaker with styleId 0 and the preset's aquesTalkPlayerVoice. Does not install or start engines.", Input("""
            {"engineId":{"type":"string","default":"voicevox-core","description":"ID from voice_list_engines."}}
            """, []))
        {
            ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = true
        },
        new("voice_list_presets", "Lists preset IDs, voice settings and lip sync/subtitle availability.")
        {
            ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false
        },
        new("voice_generate", "Synthesizes speech and adds a new element to the scene selected at call start. Save the project or scene first. Supply exactly one of presetId or voice. Presets apply lip sync and subtitles. Optional analysis is an edited result from voice_analyze. Avoids locked/occupied layers and supports Undo/Redo. Returns actual placement and IDs.", Input("""
            {"text":{"type":"string","minLength":1},
             "presetId":{"type":"string","format":"uuid"},
             "startSeconds":{"type":"number","minimum":0,"description":"Defaults to the playhead at call start."},
             "layer":{"type":"integer","minimum":0,"default":1,"description":"Preferred layer; actual placement is returned."}}
            """, ["text"], voice: true, analysis: true))
        {
            ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = false, OpenWorldHint = true
        },
        new("voice_regenerate", "Regenerates an existing speech element by elementId, preserving placement, length, trim and effects. Omit text and voice selection to use its pending settings. Optional presetId or voice replaces the voice; optional analysis supplies edited accents. Updates linked subtitles atomically with Undo/Redo.", Input("""
            {"elementId":{"type":"string","format":"uuid"},
             "text":{"type":"string","minLength":1},
             "presetId":{"type":"string","format":"uuid"}}
            """, ["elementId"], voice: true, analysis: true))
        {
            ReadOnlyHint = false, DestructiveHint = true, IdempotentHint = false, OpenWorldHint = true
        },
        new("voice_generate_script", "Generates a whole script using speaker-to-preset assignments in the scene selected at call start. Save first. Uses actual WAV durations, applies preset characters/subtitles, and places the batch as one Undo operation. Failure or cancellation removes the entire batch and its generated files.", Input("""
            {"script":{"type":"string","minLength":1},
             "speakerPresets":{"type":"object","additionalProperties":{"type":"string","format":"uuid"},"description":"Speaker name to preset ID. Use the empty-string key for lines without speaker names."},
             "hasSpeakerNames":{"type":"boolean","default":true},
             "startSeconds":{"type":"number","minimum":0,"description":"Defaults to the playhead at call start."},
             "gapSeconds":{"type":"number","minimum":0,"default":0.2},
             "firstLayer":{"type":"integer","minimum":0,"default":1},
             "separateLayers":{"type":"boolean","default":false}}
            """, ["script", "speakerPresets"]))
        {
            ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = false, OpenWorldHint = true
        },
        new("voice_save_preset", "Creates or updates a global preset. Create with name and either voice or elementId. elementId captures the speech element's pending voice and current lip sync/subtitle snapshots. presetId updates an existing preset; omitted fields retain current values. Explicit null lipSyncTargetId/subtitleElementId clears that association. Does not change existing speech and has no scene Undo.", Input("""
            {"presetId":{"type":"string","format":"uuid"},
             "name":{"type":"string","minLength":1},
             "elementId":{"type":"string","format":"uuid"},
             "lipSyncTargetId":{"type":["string","null"],"format":"uuid"},
             "subtitleElementId":{"type":["string","null"],"format":"uuid"},
             "useDefaultSubtitle":{"type":"boolean","default":false}}
            """, [], voice: true))
        {
            ReadOnlyHint = false, DestructiveHint = true, IdempotentHint = false, OpenWorldHint = false
        },
        new("voice_analyze", "Analyzes text into an AudioQuery without generating audio or editing the scene. Supply exactly one of presetId or voice. Returns analysis with text, voice settings and audioQuery; edit accent_phrases/moras/pitch and pass analysis to voice_generate or voice_regenerate. Keep the text and voice IDs associated with the query. For AquesTalkPlayer, VOICEVOX Core analyzes the text and only accent positions, accent phrase boundaries, pauses, readings and interrogatives reach the app (as phonetic symbols); pitches and lengths are ignored. Without an analysis AquesTalkPlayer reads the text itself.", Input("""
            {"text":{"type":"string","minLength":1},"presetId":{"type":"string","format":"uuid"}}
            """, ["text"], voice: true))
        {
            ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = true
        },
        new("voice_register_engine", "Connects to a VOICEVOX-compatible HTTP engine and registers it globally. Reuses an existing connection ID for the same URL, preserving its name. Returns the ID for voice_list_voices. Does not install or start the engine; no scene Undo.", Input("""
            {"url":{"type":"string"},"name":{"type":"string"}}
            """, ["url"]))
        {
            ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = true
        },
        new("voice_set_lip_sync", "Reads or changes an existing speech element's lip sync. Omit targetId to list current links and candidate target IDs. Set targetId to a candidate ID to connect, or explicitly null to disconnect. Changes use Undo/Redo and restore original character inputs when the final voice is disconnected.", Input("""
            {"elementId":{"type":"string","format":"uuid"},"targetId":{"type":["string","null"],"format":"uuid"}}
            """, ["elementId"]))
        {
            ReadOnlyHint = false, DestructiveHint = true, IdempotentHint = true, OpenWorldHint = false
        },
        new("voice_list_dictionary_words", "Searches and pages through an engine's user dictionary, returning word IDs, editable phrases and supported word types. Defaults to VOICEVOX Core and 50 words per page. Use nextOffset for the next page; voice_export_dictionary returns the complete native JSON. No open scene is needed.", Input("""
            {"engineId":{"type":"string","default":"voicevox-core"},
             "search":{"type":"string","description":"Case-insensitive substring search in surface or pronunciation."},
             "offset":{"type":"integer","minimum":0,"default":0},
             "limit":{"type":"integer","minimum":1,"default":50}}
            """, []))
        {
            ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = true
        },
        new("voice_save_dictionary_word", "Creates or updates a word in an engine's user dictionary. Omit wordId to create; supply wordId to update, preserving omitted phrases, wordType and priority. Dictionary changes are global, notify the voice UI, and have no scene Undo. Existing analyzed queries and generated audio remain unchanged until explicitly analyzed/regenerated.", Input("""
            {"engineId":{"type":"string","default":"voicevox-core"},
             "wordId":{"type":"string","format":"uuid","description":"Existing word ID. Omit to create a new word."},
             "phrases":{"type":"array","minItems":1,"description":"Required for a new word. Multiple phrases require compound accent support.","items":{
               "type":"object","properties":{"surface":{"type":"string","minLength":1},
                 "pronunciation":{"type":"string","minLength":1,"description":"Full-width katakana."},
                 "accentType":{"type":"integer","minimum":0,"description":"0 for flat accent, otherwise the accent mora position."}},
               "required":["surface","pronunciation","accentType"],"additionalProperties":false}},
             "wordType":{"type":"string","description":"ID from voice_list_dictionary_words. Defaults to PROPER_NOUN for new words."},
             "priority":{"type":"integer","minimum":0,"maximum":10,"description":"Defaults to 5 for new words. Omitted fields retain stored values on update."}}
            """, []))
        {
            ReadOnlyHint = false, DestructiveHint = true, IdempotentHint = false, OpenWorldHint = true
        },
        new("voice_delete_dictionary_word", "Deletes a user dictionary word from the specified engine. Defaults to VOICEVOX Core. This global dictionary edit has no scene Undo; existing generated speech remains unchanged.", Input("""
            {"engineId":{"type":"string","default":"voicevox-core"},"wordId":{"type":"string","format":"uuid"}}
            """, ["wordId"]))
        {
            ReadOnlyHint = false, DestructiveHint = true, IdempotentHint = true, OpenWorldHint = true
        },
        new("voice_export_dictionary", "Returns an engine's complete user dictionary as a UUID-keyed JSON object. Preserves word IDs, engine-specific fields and compound accents. Pass the returned dictionary to voice_import_dictionary. No file or scene is created.", Input("""
            {"engineId":{"type":"string","default":"voicevox-core"}}
            """, []))
        {
            ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = true
        },
        new("voice_import_dictionary", "Imports a UUID-keyed dictionary JSON object using the UI's compatibility and conversion rules. Unsupported entries are reported and skipped. Existing IDs are skipped unless overwrite=true. dryRun=true reports applicability without writing. Returns per-entry errors/duplicates and submittedCount. Dictionary edits have no scene Undo.", Input("""
            {"engineId":{"type":"string","default":"voicevox-core"},
             "dictionary":{"type":"object","additionalProperties":true,"description":"Native UUID-keyed dictionary from voice_export_dictionary or an engine JSON export. Invalid entries are reported individually."},
             "overwrite":{"type":"boolean","default":false},"dryRun":{"type":"boolean","default":false}}
            """, ["dictionary"]))
        {
            ReadOnlyHint = false, DestructiveHint = true, IdempotentHint = true, OpenWorldHint = true
        }
    ]);

    private static JsonElement Input(string properties, string[] required, bool voice = false, bool analysis = false)
    {
        var fields = JsonNode.Parse(properties)!.AsObject();
        if (voice) fields["voice"] = JsonNode.Parse(VoiceSchema);
        if (analysis) fields["analysis"] = JsonNode.Parse("""
            {"type":"object","description":"Edited analysis returned by voice_analyze. Its text and voice must match this generation request.",
             "properties":{"text":{"type":"string"},
               "settings":{"type":"object","properties":{"engineId":{"type":"string"},"speakerUuid":{"type":"string"},"styleId":{"type":"integer"}},"required":["engineId","speakerUuid","styleId"],"additionalProperties":true},
               "audioQuery":{"type":"object","additionalProperties":true}},
             "required":["text","settings","audioQuery"],"additionalProperties":false}
            """);
        return JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = fields, required, additionalProperties = false
        });
    }
}
