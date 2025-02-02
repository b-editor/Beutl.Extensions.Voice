using System.Runtime.InteropServices;
using System.Text.Json;
using Beutl.Extensions.Voice.Models;
using Beutl.Logging;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using VoicevoxCoreSharp.Core;
using VoicevoxCoreSharp.Core.Enum;
using VoicevoxCoreSharp.Core.Struct;

namespace Beutl.Extensions.Voice.Services;

public class VoiceVoxLoader(string voicevoxCorePath)
{
    private readonly ILogger _logger = Log.CreateLogger<VoiceVoxLoader>();
    private static bool _setResolver;

    public OpenJtalk? OpenJtalk { get; private set; }

    public Synthesizer? Synthesizer { get; private set; }

    public List<VoiceSet> VoiceSets { get; } = [];

    public TaskCompletionSource<bool> InitializationTcs { get; } = new();

    public bool IsLoaded { get; private set; }

    public bool IsInstalled { get; private set; }

    public void Load()
    {
        try
        {
            if (!Directory.Exists(voicevoxCorePath))
            {
                _logger.LogError("voicevox_core directory not found");
                IsInstalled = false;
                InitializationTcs.TrySetResult(false);
                return;
            }
            else
            {
                IsInstalled = true;
            }

            var openJTalkDictPath = Path.Combine(voicevoxCorePath, "open_jtalk_dic_utf_8-1.11");
            if (!_setResolver)
            {
                NativeLibrary.SetDllImportResolver(typeof(OpenJtalk).Assembly, (name, _, _) =>
                {
                    if (name == "voicevox_core")
                    {
                        var path = Path.Combine(voicevoxCorePath,
                            OperatingSystem.IsWindows() ? "libvoicevox_core.dll"
                            : OperatingSystem.IsLinux() ? "libvoicevox_core.so"
                            : OperatingSystem.IsMacOS() ? "libvoicevox_core.dylib"
                            : throw new PlatformNotSupportedException());
                        if (NativeLibrary.TryLoad(path, out var lib))
                        {
                            return lib;
                        }
                    }

                    return IntPtr.Zero;
                });
                _setResolver = true;
            }

            _logger.LogInformation("Initializing core...");

            var initializeOptions = InitializeOptions.Default();
            var result = OpenJtalk.New(openJTalkDictPath, out var openJtalk);
            OpenJtalk = openJtalk;
            if (result != ResultCode.RESULT_OK)
            {
                _logger.LogError("Failed to initialize OpenJtalk: {Result}", result.ToMessage());
                return;
            }

            result = Synthesizer.New(OpenJtalk, initializeOptions, out var synthesizer);
            Synthesizer = synthesizer;
            if (result != ResultCode.RESULT_OK)
            {
                _logger.LogError("Failed to initialize Synthesizer: {Result}", result.ToMessage());
                return;
            }

            var matcher = new Matcher();
            matcher.AddIncludePatterns(["*.vvm"]);

            foreach (var path in matcher.GetResultsInFullPath(Path.Combine(voicevoxCorePath, "model")))
            {
                _logger.LogInformation("Loading VoiceModel: {Path}", path);
                result = VoiceModel.New(path, out var voiceModel);
                if (result != ResultCode.RESULT_OK)
                {
                    _logger.LogError("Failed to create VoiceModel: {Result}", result.ToMessage());
                    return;
                }

                result = Synthesizer.LoadVoiceModel(voiceModel);
                if (result != ResultCode.RESULT_OK)
                {
                    _logger.LogError("Failed to load VoiceModel: {Result}", result.ToMessage());
                    return;
                }

                var metadatas = JsonSerializer.Deserialize<VoiceMetadata[]>(voiceModel.MetasJson);
                if (metadatas == null)
                {
                    _logger.LogError("Failed to deserialize VoiceMetadata: {Path}", path);
                    return;
                }

                VoiceSets.Add(new VoiceSet(voiceModel, metadatas));
                _logger.LogInformation("Loaded VoiceModel: {Path}", path);
            }

            _logger.LogInformation("Core initialized");
            InitializationTcs.TrySetResult(true);
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            InitializationTcs.TrySetResult(false);
            _logger.LogError(ex, "Failed to load TTS models");
            try
            {
                Unload();
            }
            catch (Exception ex2)
            {
                _logger.LogError(ex2, "Failed to unload TTS models");
            }

            throw;
        }
    }

    public void Unload()
    {
        foreach (var voiceModel in VoiceSets)
        {
            voiceModel.Model.Dispose();
        }

        VoiceSets.Clear();
        Synthesizer?.Dispose();
        OpenJtalk?.Dispose();
        Synthesizer = null;
        OpenJtalk = null;
        IsLoaded = false;
    }
}