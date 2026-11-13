using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Beutl.Extensions.Voice.Services;

public sealed record LocalVoiceEngine(string Name, string ApplicationName, string Url);

internal static class LocalVoiceEngines
{
    internal static readonly LocalVoiceEngine[] Known =
    [
        new("VOICEVOX ENGINE", "VOICEVOX", "http://127.0.0.1:50021/"),
        new("AivisSpeech Engine", "AivisSpeech", "http://127.0.0.1:10101/"),
        new("SHAREVOX Engine", "SHAREVOX", "http://127.0.0.1:50025/")
    ];

    internal static string CurrentPlatform => OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux";

    internal static string? FindApplication(LocalVoiceEngine engine)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string platform = CurrentPlatform;
        string[] roots = platform switch
        {
            "macos" => ["/Applications", Path.Combine(home, "Applications")],
            "windows" =>
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            ],
            _ => [Path.Combine(home, ".local", "bin"), Path.Combine(home, "Applications"), "/opt", "/usr/local/bin", "/usr/bin"]
        };
        return FindApplication(engine, roots, platform) ?? (OperatingSystem.IsWindows() ? FindWindowsInstallation(engine) : null);
    }

    internal static string? FindApplication(LocalVoiceEngine engine, IEnumerable<string> roots, string platform)
    {
        string name = engine.ApplicationName;
        string[] relativePaths = platform switch
        {
            "macos" => [name + ".app"],
            "windows" => [Path.Combine(name, name + ".exe")],
            _ => [name.ToLowerInvariant(), name + ".AppImage", Path.Combine(name, name.ToLowerInvariant()),
                Path.Combine(name.ToLowerInvariant(), name.ToLowerInvariant())]
        };
        return roots.Where(x => !string.IsNullOrWhiteSpace(x))
            .SelectMany(root => relativePaths.Select(path => Path.Combine(root, path)))
            .FirstOrDefault(path => platform == "macos"
                ? File.Exists(Path.Combine(path, "Contents", "Info.plist"))
                    && File.Exists(Path.Combine(path, "Contents", "MacOS", name))
                : File.Exists(path));
    }

    // What was chosen for an app: the app or its executable, or a folder that holds the app, as the folder of a standalone
    // engine holds its run executable. A macOS app is chosen as the folder it is. Null when the folder holds neither.
    internal static string? ResolveChosenApplication(LocalVoiceEngine engine, string path, string platform)
    {
        // The macOS folder picker gives a folder with a separator at the end.
        path = Path.TrimEndingDirectorySeparator(path);
        if (!Directory.Exists(path) || path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return path;
        string run = Path.Combine(path, platform == "windows" ? "run.exe" : "run");
        return FindApplication(engine, [path], platform) ?? (File.Exists(run) ? run : null);
    }

    [SupportedOSPlatform("windows")]
    private static string? FindWindowsInstallation(LocalVoiceEngine engine)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) continue;
                foreach (string key in uninstall.GetSubKeyNames())
                {
                    using var item = uninstall.OpenSubKey(key);
                    if (item?.GetValue("DisplayName") is not string name
                        || !(name.Equals(engine.ApplicationName, StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith(engine.ApplicationName + " ", StringComparison.OrdinalIgnoreCase))
                        || item.GetValue("InstallLocation") is not string directory) continue;
                    string path = Path.Combine(directory, engine.ApplicationName + ".exe");
                    if (File.Exists(path)) return path;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return null;
    }

    internal static ProcessStartInfo CreateStartInfo(string applicationPath, bool macOS)
    {
        string path = Path.GetFullPath(applicationPath);
        if (macOS && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            var start = new ProcessStartInfo("/usr/bin/open");
            start.ArgumentList.Add("-a");
            start.ArgumentList.Add(path);
            return start;
        }
        return new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = false };
    }

    internal static void StartApplication(string path) => Process.Start(CreateStartInfo(path, OperatingSystem.IsMacOS()))?.Dispose();
}
