using System.Text.Json;
using System.Text.Json.Nodes;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;

namespace Beutl.Extensions.Voice;

public sealed partial class VoiceMcpToolExtension
{
    private static async Task<VoiceEngineConnection> DictionaryEngine(string? id, CancellationToken cancellationToken)
    {
        if (id == null || id == VoiceSettings.CoreEngineId) return VoiceEngineConnection.Core;
        var engine = VoiceEngineStore.Load().FirstOrDefault(item => item.Id == id)
            ?? throw new ArgumentException($"Voice engine '{id}' is not registered. Call voice_list_engines.");
        return await HttpVoiceEngine.DiscoverAsync(engine, cancellationToken);
    }

    private static DictionaryWordType[] DictionaryWordTypes(VoiceEngineConnection engine) =>
        VoiceDictionaryService.IsAivis(engine) ? DictionaryWordType.All : DictionaryWordType.All[..5];

    private static async Task<McpToolResult> ListDictionaryWords(JsonElement arguments, CancellationToken cancellationToken)
    {
        var request = arguments.Deserialize<DictionaryListRequest>(s_json)!;
        if (request.Offset < 0 || request.Limit < 1)
            throw new ArgumentException("offset must be non-negative and limit must be positive.");
        var engine = await DictionaryEngine(request.EngineId, cancellationToken);
        var words = await VoiceDictionaryService.LoadAsync(engine, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string search = request.Search?.Trim() ?? "";
        var matches = words.Where(word => word.Surface.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || word.Pronunciation.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(word => word.Surface).ThenBy(word => word.Id).ToArray();
        var page = matches.Skip(request.Offset).Take(request.Limit).ToArray();
        int end = request.Offset + page.Length;
        return Json(new
        {
            engineId = engine.Id, supportsCompoundAccent = VoiceDictionaryService.IsAivis(engine),
            wordTypes = DictionaryWordTypes(engine).Select(type => new { type.Id, type.Name }).ToArray(),
            totalCount = matches.Length, request.Offset, request.Limit,
            nextOffset = end < matches.Length ? (int?)end : null,
            words = page.Select(word => new
            {
                wordId = word.Id, word.Surface, word.Pronunciation, word.Priority,
                wordType = word.WordType?.Id, word.Phrases
            }).ToArray()
        });
    }

    private static async Task<VoiceDictionaryWord> DictionaryWord(VoiceEngineConnection engine, DictionaryWordRequest request,
        CancellationToken cancellationToken)
    {
        VoiceDictionaryWord? original = null;
        if (request.WordId is { } id)
        {
            original = (await VoiceDictionaryService.LoadAsync(engine, cancellationToken)).FirstOrDefault(word => word.Id == id)
                ?? throw new ArgumentException($"Dictionary word '{id}' was not found. Call voice_list_dictionary_words.");
        }
        var phrases = request.Phrases ?? original?.Phrases
            ?? throw new ArgumentException("Creating a new dictionary word requires phrases.");
        string typeId = request.WordType ?? original?.WordType?.Id
            ?? (original == null ? "PROPER_NOUN" : throw new ArgumentException("Specify wordType for this word."));
        var type = DictionaryWordTypes(engine).FirstOrDefault(item => item.Id == typeId)
            ?? throw new ArgumentException($"Word type '{typeId}' is not supported by this engine. Call voice_list_dictionary_words for supported types.");
        return VoiceDictionaryWord.Create(original?.Id ?? Guid.NewGuid(), phrases, type,
            request.Priority ?? original?.Priority ?? 5, VoiceDictionaryService.IsAivis(engine), original?.Data);
    }

    private static async Task<McpToolResult> SaveDictionaryWord(JsonElement arguments, CancellationToken cancellationToken)
    {
        var request = arguments.Deserialize<DictionaryWordRequest>(s_json)!;
        var engine = await DictionaryEngine(request.EngineId, cancellationToken);
        var word = await DictionaryWord(engine, request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Guid id = await VoiceDictionaryService.SaveAsync(engine, request.WordId, word, cancellationToken);
        return Json(new { engineId = engine.Id, wordId = id });
    }

    private static async Task<McpToolResult> DeleteDictionaryWord(JsonElement arguments, CancellationToken cancellationToken)
    {
        var engine = await DictionaryEngine(arguments.TryGetProperty("engineId", out var engineId) ? engineId.GetString() : null, cancellationToken);
        Guid id = arguments.GetProperty("wordId").GetGuid();
        await VoiceDictionaryService.DeleteAsync(engine, id, cancellationToken);
        return Json(new { engineId = engine.Id, wordId = id, deleted = true });
    }

    private static async Task<McpToolResult> ExportDictionary(JsonElement arguments, CancellationToken cancellationToken)
    {
        var engine = await DictionaryEngine(arguments.TryGetProperty("engineId", out var engineId) ? engineId.GetString() : null, cancellationToken);
        var words = await VoiceDictionaryService.LoadAsync(engine, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Json(new { engineId = engine.Id, dictionary = VoiceDictionaryService.ToJson(words) });
    }

    private static async Task<McpToolResult> ImportDictionary(JsonElement arguments, CancellationToken cancellationToken)
    {
        var request = arguments.Deserialize<DictionaryImportRequest>(s_json)!;
        var engine = await DictionaryEngine(request.EngineId, cancellationToken);
        var existing = await VoiceDictionaryService.LoadAsync(engine, cancellationToken);
        var entries = VoiceDictionaryService.PrepareImport(request.Dictionary.ToJsonString(), engine, existing.Select(word => word.Id));
        var applicable = entries.Where(entry => entry.Word != null && (!entry.Duplicate || request.Overwrite)).Select(entry => entry.Word!).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.DryRun)
            await VoiceDictionaryService.ImportAsync(engine, applicable, request.Overwrite, cancellationToken);
        return Json(new
        {
            engineId = engine.Id, request.DryRun, applicableCount = applicable.Length,
            submittedCount = request.DryRun ? 0 : applicable.Length,
            entries = entries.Select(entry => new
            {
                wordId = entry.Word?.Id, surface = entry.Name, entry.Duplicate, entry.Error
            }).ToArray()
        });
    }

    private sealed record DictionaryListRequest
    {
        public string EngineId { get; init; } = VoiceSettings.CoreEngineId;
        public string? Search { get; init; }
        public int Offset { get; init; }
        public int Limit { get; init; } = 50;
    }

    private sealed record DictionaryWordRequest
    {
        public string EngineId { get; init; } = VoiceSettings.CoreEngineId;
        public Guid? WordId { get; init; }
        public VoiceDictionaryPhrase[]? Phrases { get; init; }
        public string? WordType { get; init; }
        public int? Priority { get; init; }
    }

    private sealed record DictionaryImportRequest
    {
        public string EngineId { get; init; } = VoiceSettings.CoreEngineId;
        public required JsonObject Dictionary { get; init; }
        public bool Overwrite { get; init; }
        public bool DryRun { get; init; }
    }
}
