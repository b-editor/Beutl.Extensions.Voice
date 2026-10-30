using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beutl.Extensions.Voice.Models;

namespace Beutl.Extensions.Voice.Services;

// VOICEVOX-compatible protocol, including engines that advertise different editing capabilities.
internal static class HttpVoiceEngine
{
    private static readonly HttpClient s_client = new() { Timeout = TimeSpan.FromMinutes(5) };

    internal static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new ArgumentException("接続先のHTTP / HTTPS URLを入力してください。");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    public static async Task<VoiceEngineConnection> DiscoverAsync(VoiceEngineConnection connection,
        CancellationToken cancellationToken = default)
    {
        connection = connection with { Url = NormalizeUrl(connection.Url) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var speakersTask = GetJsonAsync(connection, "speakers", false, timeout.Token);
        var manifestTask = GetJsonAsync(connection, "engine_manifest", true, timeout.Token);
        var versionTask = GetJsonAsync(connection, "version", true, timeout.Token);
        await Task.WhenAll(speakersTask, manifestTask, versionTask);
        var voices = (await speakersTask)?.Deserialize<VoiceMetadata[]>()
            ?? throw new JsonException("話者一覧を読み込めませんでした。");
        // Singing styles cannot be passed to the speech synthesis API.
        voices = voices.Select(voice => new VoiceMetadata
        {
            Name = voice.Name, SpeakerUuid = voice.SpeakerUuid, Version = voice.Version,
            Styles = voice.Styles.Where(style => style.Type is not ("sing" or "singing_teacher" or "frame_decode")).ToArray()
        }).Where(voice => voice.Styles.Length > 0).ToArray();
        var manifest = await manifestTask as JsonObject;
        return connection with
        {
            Name = string.IsNullOrWhiteSpace(connection.Name) ? (string?)manifest?["name"] ?? new Uri(connection.Url).Authority : connection.Name.Trim(),
            EngineUuid = (string?)manifest?["uuid"] ?? connection.EngineUuid, Version = (string?)await versionTask ?? (string?)manifest?["version"] ?? "",
            Capabilities = manifest == null ? connection.Capabilities : VoiceEngineCapabilities.FromManifest(manifest), Voices = voices
        };
    }

    private static async Task<(VoiceEngineConnection Connection, VoiceSettings Settings)> ResolveAsync(
        VoiceSettings settings, CancellationToken cancellationToken)
    {
        var connection = VoiceEngineStore.Load().FirstOrDefault(x => x.Id == settings.EngineId)
            ?? throw new InvalidOperationException($"接続先「{settings.EngineName}」が見つかりません。エンジンの接続先を登録してください。");
        connection = await DiscoverAsync(connection, cancellationToken);
        var voice = connection.Voices.FirstOrDefault(x => x.SpeakerUuid == settings.SpeakerUuid);
        var style = voice?.Styles.FirstOrDefault(x => x.Id == settings.StyleId);
        if (voice == null || style == null)
            throw new InvalidOperationException($"{connection.Name} に声が見つかりません: {settings.SpeakerName} / {settings.StyleName}。話者を選び直してください。");
        return (connection, settings with
        {
            EngineName = connection.Name, EngineUuid = connection.EngineUuid, EngineVersion = connection.Version,
            SpeakerName = voice.Name, StyleName = style.Name, ModelVersion = voice.Version
        });
    }

    public static async Task<AudioQueryModel> CreateQueryAsync(string text, VoiceSettings settings, CancellationToken cancellationToken)
    {
        var (connection, resolved) = await ResolveAsync(settings, cancellationToken);
        return await CreateQueryAsync(connection, text, resolved.StyleId, cancellationToken);
    }

    private static async Task<AudioQueryModel> CreateQueryAsync(VoiceEngineConnection connection, string text,
        long styleId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(connection, HttpMethod.Post,
            $"audio_query?speaker={styleId.ToString(CultureInfo.InvariantCulture)}&text={Uri.EscapeDataString(text)}", null, cancellationToken);
        return JsonSerializer.Deserialize<AudioQueryModel>(await response.Content.ReadAsStringAsync(cancellationToken))
            ?? throw new JsonException("音声エンジンの応答を解析できませんでした。");
    }

    public static async Task<VoiceSynthesisResult> SynthesizeAsync(string text, VoiceSettings settings,
        AudioQueryModel? query, CancellationToken cancellationToken)
    {
        var (connection, resolved) = await ResolveAsync(settings, cancellationToken);
        query ??= await CreateQueryAsync(connection, text, resolved.StyleId, cancellationToken);
        resolved.ApplyTo(query, connection.Capabilities);
        string json = JsonSerializer.Serialize(query, VoiceSynthesisService.JsonOptions);
        using var response = await SendAsync(connection, HttpMethod.Post,
            $"synthesis?speaker={resolved.StyleId.ToString(CultureInfo.InvariantCulture)}", json, cancellationToken);
        var wave = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new(wave, query, json, resolved with
        {
            SpeedScale = query.SpeedScale, PitchScale = query.PitchScale, IntonationScale = query.IntonationScale,
            VolumeScale = query.VolumeScale, PrePhonemeLength = query.PrePhonemeLength,
            PostPhonemeLength = query.PostPhonemeLength, TempoDynamicsScale = query.TempoDynamicsScale
        });
    }

    private static async Task<JsonNode?> GetJsonAsync(VoiceEngineConnection connection, string endpoint, bool optional,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(connection, HttpMethod.Get, endpoint, null, cancellationToken, optional);
        if (!response.IsSuccessStatusCode) return null;
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static async Task<HttpResponseMessage> SendAsync(VoiceEngineConnection connection, HttpMethod method,
        string endpoint, string? json, CancellationToken cancellationToken, bool optional = false)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(connection.Url), endpoint));
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await s_client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || optional && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
            return response;
        string detail = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException($"{connection.Name}: HTTP {(int)status} {detail[..Math.Min(detail.Length, 300)]}", null, status);
    }
}
