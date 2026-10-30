using System.ComponentModel.DataAnnotations;
using Beutl.Extensibility;

namespace Beutl.Extensions.Voice;

public sealed class VoiceExtensionSettings : ExtensionSettings
{
    private bool _skippedForSession;

    public VoiceExtensionSettings()
    {
        ScanProperties<VoiceExtensionSettings>();
        HideCoreInstallPrompt.ValueChanged += (_, _) =>
        {
            _skippedForSession = false;
            OnChanged();
            PromptChanged?.Invoke();
        };
    }

    [Display(Name = "VOICEVOX Coreのインストール案内を表示しない")]
    public IProperty<bool> HideCoreInstallPrompt { get; } = Property.CreateValue(false);

    internal bool ShouldPrompt => !_skippedForSession && !HideCoreInstallPrompt.Value;
    internal event Action? PromptChanged;

    internal void BeginSession()
    {
        _skippedForSession = false;
        PromptChanged?.Invoke();
    }

    internal void Skip(bool doNotShowAgain)
    {
        if (doNotShowAgain) HideCoreInstallPrompt.Value = true;
        _skippedForSession = true;
        PromptChanged?.Invoke();
    }
}
