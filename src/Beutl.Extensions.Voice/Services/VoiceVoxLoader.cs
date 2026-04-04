using System.Runtime.InteropServices;
using System.Text.Json;
using Beutl.Extensions.Voice.Models;
using Beutl.Language;
using Beutl.Logging;
using Beutl.Services;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using VoicevoxCoreSharp.Core;
using VoicevoxCoreSharp.Core.Enum;
using VoicevoxCoreSharp.Core.Struct;

namespace Beutl.Extensions.Voice.Services;

public class VoiceVoxLoader(string voicevoxHomePath)
{
    private readonly ILogger _logger = Log.CreateLogger<VoiceVoxLoader>();
    private static bool _setResolver;
    private readonly object _loadLock = new();
    private readonly HashSet<VoiceSet> _loadedVoiceModels = [];

    public OpenJtalk? OpenJtalk { get; private set; }

    public Onnxruntime? Onnxruntime { get; private set; }

    public Synthesizer? Synthesizer { get; private set; }

    public List<VoiceSet> VoiceSets { get; } = [];

    public TaskCompletionSource<bool> InitializationTcs { get; } = new();

    public bool IsLoaded { get; private set; }

    public bool IsInstalled { get; private set; }

    public void Load()
    {
        try
        {
            if (!Directory.Exists(voicevoxHomePath))
            {
                _logger.LogError("voicevox directory not found");
                IsInstalled = false;
                InitializationTcs.TrySetResult(false);
                return;
            }
            else
            {
                IsInstalled = true;
            }

            if (!_setResolver)
            {
                NativeLibrary.SetDllImportResolver(typeof(OpenJtalk).Assembly, (name, _, _) =>
                {
                    _logger.LogInformation("Resolving native library: {Name}", name);
                    if (name == "voicevox_core")
                    {
                        var path = Path.Combine(voicevoxHomePath, "core", "lib",
                            OperatingSystem.IsWindows() ? "voicevox_core.dll"
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

            var openJTalkDictPath = Path.Combine(voicevoxHomePath, "open_jtalk");
            var result = OpenJtalk.New(openJTalkDictPath, out var openJtalk);
            OpenJtalk = openJtalk;
            if (result != ResultCode.RESULT_OK)
            {
                _logger.LogError("Failed to initialize OpenJtalk: {Result}", result.ToMessage());
                InitializationTcs.TrySetResult(false);
                return;
            }

            var onnxRuntimePath = Path.Combine(voicevoxHomePath, "onnxruntime", "lib",
                OperatingSystem.IsWindows() ? "voicevox_onnxruntime.dll"
                : OperatingSystem.IsLinux() ? "libvoicevox_onnxruntime.so"
                : OperatingSystem.IsMacOS() ? "libvoicevox_onnxruntime.dylib"
                : throw new PlatformNotSupportedException("Unsupported OS"));
            var loadOnnxruntimeOptions = new LoadOnnxruntimeOptions(onnxRuntimePath);
            result = Onnxruntime.LoadOnce(loadOnnxruntimeOptions, out var onnxruntime);
            Onnxruntime = onnxruntime;
            if (result != ResultCode.RESULT_OK)
            {
                _logger.LogError("Failed to load ONNX Runtime: {Result}", result.ToMessage());
                NotificationService.ShowError(Strings.Error, result.ToMessage());
                InitializationTcs.TrySetResult(false);
                return;
            }

            var initializeOptions = InitializeOptions.Default();
            result = Synthesizer.New(Onnxruntime, OpenJtalk, initializeOptions, out var synthesizer);
            Synthesizer = synthesizer;
            if (result != ResultCode.RESULT_OK)
            {
                _logger.LogError("Failed to initialize Synthesizer: {Result}", result.ToMessage());
                NotificationService.ShowError(Strings.Error, result.ToMessage());
                InitializationTcs.TrySetResult(false);
                return;
            }

            var matcher = new Matcher();
            matcher.AddIncludePatterns(["*.vvm"]);

            foreach (var path in matcher.GetResultsInFullPath(Path.Combine(voicevoxHomePath, "models")))
            {
                var fileName = Path.GetFileName(path);
                // ソング用のモデルは読み込まない
                if (fileName.StartsWith('s')) continue;

                _logger.LogInformation("Opening VoiceModel: {Path}", path);
                result = VoiceModelFile.Open(path, out var voiceModel);
                if (result != ResultCode.RESULT_OK)
                {
                    _logger.LogError("Failed to create VoiceModel: {Result}", result.ToMessage());
                    continue;
                }

                var metadatas = JsonSerializer.Deserialize<VoiceMetadata[]>(voiceModel.MetasJson);
                if (metadatas == null)
                {
                    _logger.LogError("Failed to deserialize VoiceMetadata: {Path}", path);
                    voiceModel.Dispose();
                    continue;
                }

                // 実際の音声モデルはSynthesizerに読み込まず、利用時まで遅延させる
                VoiceSets.Add(new VoiceSet(voiceModel, metadatas));
                _logger.LogInformation("Opened VoiceModel metadata: {Path}", path);
            }

            _logger.LogInformation("Core initialized");
            InitializationTcs.TrySetResult(true);
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            InitializationTcs.TrySetResult(false);
            _logger.LogError(ex, "Failed to load TTS models");
            NotificationService.ShowError("VoiceVoxLoader", ex.Message);
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

    public bool EnsureVoiceModelLoaded(uint styleId)
    {
        if (Synthesizer == null)
        {
            _logger.LogError("Synthesizer is not initialized");
            return false;
        }

        lock (_loadLock)
        {
            var voiceSet = VoiceSets.FirstOrDefault(
                vs => vs.Metadata.Any(m => m.Styles.Any(s => s.Id == styleId)));
            if (voiceSet == null)
            {
                _logger.LogError("VoiceSet not found for styleId: {StyleId}", styleId);
                return false;
            }

            if (_loadedVoiceModels.Contains(voiceSet))
            {
                return true;
            }

            _logger.LogInformation("Loading VoiceModel for styleId {StyleId}", styleId);
            var result = Synthesizer.LoadVoiceModel(voiceSet.Model);
            if (result != ResultCode.RESULT_OK)
            {
                _logger.LogError("Failed to load VoiceModel: {Result}", result.ToMessage());
                return false;
            }

            _loadedVoiceModels.Add(voiceSet);
            _logger.LogInformation("Loaded VoiceModel for styleId {StyleId}", styleId);
            return true;
        }
    }

    public void Unload()
    {
        lock (_loadLock)
        {
            _loadedVoiceModels.Clear();
        }

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