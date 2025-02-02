using Beutl.Extensibility;
using Beutl.Extensions.Voice.Services;
using Beutl.Logging;
using Microsoft.Extensions.Logging;

namespace Beutl.Extensions.Voice;

[Export]
public class TtsLoader : Extension
{
    private readonly ILogger _logger = Log.CreateLogger<TtsLoader>();
    internal static VoiceVoxLoader VoiceVoxLoader;

    public override string Name => "TTS Loader";

    public override string DisplayName => Name;

    public override void Load()
    {
        base.Load();
        StaticLoad();
    }

    public static Task StaticLoad()
    {
        var home = BeutlEnvironment.GetHomeDirectoryPath();
        var voicevoxCorePath = Path.Combine(home, "voicevox_core");
        VoiceVoxLoader = new VoiceVoxLoader(voicevoxCorePath);
        return Task.Run(() =>
        {
            VoiceVoxLoader.Load();
        });
    }
}