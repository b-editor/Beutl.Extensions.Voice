using Beutl.Extensibility;
using Beutl.Extensions.Voice.Services;

namespace Beutl.Extensions.Voice;

// Turns speech downloaded from a Yukkuri voice site in the browser tab into a speech element with its character and
// caption, as the speech tab's Add button does.
[Export]
public sealed class WebVoiceDownloadExtension : BrowserDownloadExtension
{
    public override string Name => "Text-to-Speech web voices";

    public override string DisplayName => "ゆっくり音声サイトの取り込み";

    public override BrowserDownloadClaim? Claim(BrowserDownloadOffer offer) => WebVoiceImport.Claim(offer);

    public override ValueTask<bool> ImportAsync(BrowserDownloadImportContext context, CancellationToken cancellationToken) =>
        WebVoiceImport.Default.ImportAsync(context, cancellationToken);
}
