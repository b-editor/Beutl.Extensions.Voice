using System.Text.Json;
using Beutl.Extensibility;

namespace Beutl.Extensions.Voice.Services;

// What a site's page showed when it offered the speech: the text that was spoken and the voice that spoke it.
internal sealed record WebVoicePage(string Text, string VoiceId, string VoiceName)
{
    public static WebVoicePage Unknown { get; } = new("", "", "");
}

// A text-to-speech site whose downloads in the browser tab become speech elements.
internal sealed record WebVoiceSite(string Id, string Name, IReadOnlyList<string> Hosts, string PageScript)
{
    public bool Serves(Uri page) => Hosts.Contains(page.IdnHost, StringComparer.OrdinalIgnoreCase);
}

internal static class WebVoiceSites
{
    private static readonly string[] s_audioExtensions = [".mp3", ".wav", ".ogg", ".oga", ".opus", ".m4a", ".aac", ".flac"];

    // Both sites synthesize in the page and hand the file over as a blob: or data: URL whose name says little about
    // it, so the page script reads the inputs that produced it. A page with several voices has a save button per
    // voice and nothing on the page tells afterwards which one was used, so the script records the clicks from the
    // first download on.
    private const string ClickTracker = """
        if (!window.__beutlVoiceClicks) {
            window.__beutlVoiceClicks = true;
            document.addEventListener('click', event => {
                const button = event.target.closest?.(saveButton);
                if (!button) return;
                window.__beutlVoiceSelect = null;
                for (let node = button.parentElement; node; node = node.parentElement) {
                    const selects = node.querySelectorAll(voiceSelect);
                    if (selects.length > 1) break;
                    if (selects.length === 1) { window.__beutlVoiceSelect = selects[0]; break; }
                }
            }, true);
        }
        const recorded = window.__beutlVoiceSelect?.isConnected ? window.__beutlVoiceSelect : null;
        const voice = recorded ?? document.querySelector(voiceSelect);
        const value = selector => document.querySelector(selector)?.value?.trim() ?? '';
        const label = voice?.selectedOptions?.[0]?.textContent?.replace(/^声種[:：]\s*/, '').trim() ?? '';
        """;

    public static WebVoiceSite YukkuriVoiceMaker { get; } = new("siyukatu", "ゆっくりボイスメーカー", ["tts.siyukatu.com"],
        "(() => { const saveButton = '.play-btn[data-save=\"true\"]';"
        + " const voiceSelect = '#voice, .voice-group [data-tct=\"voice\"]';"
        + ClickTracker
        // The phonetic field, when filled, is what the site speaks; the caption keeps the written text.
        + " return JSON.stringify({ text: value('#text') || value('#koe'), voice: voice?.value ?? '', voiceName: label }); })()");

    public static WebVoiceSite Yukumo { get; } = new("yukumo", "ゆくも!", ["www.yukumo.net", "yukumo.net"],
        // The home page titles its save button "MP3を保存", the Pro page with a row per voice "MP3保存".
        "(() => { const saveButton = 'button[title^=\"MP3\"]';"
        + " const voiceSelect = 'select[x-model=\"selected\"], select[x-model=\"setting.type\"]';"
        + ClickTracker
        + " return JSON.stringify({ text: value('[x-model=\"kanji\"]') || value('[x-model=\"koe\"]'), voice: voice?.value ?? '', voiceName: label }); })()");

    public static IReadOnlyList<WebVoiceSite> All { get; } = [YukkuriVoiceMaker, Yukumo];

    public static WebVoiceSite? Find(string id) => All.FirstOrDefault(x => x.Id == id);

    public static string DescribeVoice(string voiceName, string voiceId) =>
        !string.IsNullOrWhiteSpace(voiceName) ? voiceName
        : !string.IsNullOrWhiteSpace(voiceId) ? voiceId : "声を判別できませんでした";

    // Only audio the page itself offered is taken over; a link to another site's file keeps the browser's options.
    public static WebVoiceSite? Find(BrowserDownloadOffer offer)
    {
        string? name = offer.SuggestedName;
        if (string.IsNullOrWhiteSpace(name) && !offer.IsGenerated) name = Uri.UnescapeDataString(offer.Source.AbsolutePath);
        if (!s_audioExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) return null;
        return All.FirstOrDefault(site => site.Serves(offer.Page) && site.Serves(offer.Source));
    }

    public static WebVoicePage ReadPage(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return WebVoicePage.Unknown;
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return WebVoicePage.Unknown;
            return new(Read("text"), Read("voice"), Read("voiceName"));

            string Read(string name) => document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!.Trim() : "";
        }
        catch (JsonException)
        {
            return WebVoicePage.Unknown;
        }
    }
}
