using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Beutl.Audio.Effects;
using Beutl.Extensions.Voice.Models;
using Beutl.Logging;
using Microsoft.Extensions.Logging;

namespace Beutl.Extensions.Voice.Services;

// Where AquesTalkPlayer keeps its presets and how its command line takes options differ between its Windows and
// macOS versions; there is no Linux version.
internal sealed record AquesTalkPlayerHost(string Platform, string Home)
{
    public static AquesTalkPlayerHost Current { get; } = new(
        OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux",
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public bool IsMacOS => Platform == "macos";
    public bool IsWindows => Platform == "windows";
    public bool IsSupported => IsMacOS || IsWindows;
}

// AQUEST's AquesTalkPlayer, which the user installs, speaking through the command line its manual documents for other
// applications. Its presets are the voices, and the app is neither bundled nor started in any other way.
internal static partial class AquesTalkPlayerEngine
{
    public const string ApplicationName = "AquesTalkPlayer";
    public const string AnalysisNeedsCore =
        "AquesTalkPlayerのアクセントの解析にはVOICEVOX Coreを使います。「設定」からVOICEVOX Coreをインストールしてください。";

    private static readonly ILogger s_logger = Log.CreateLogger(typeof(AquesTalkPlayerEngine));
    // One synthesis at a time: the app is not documented to run several instances at once.
    private static readonly SemaphoreSlim s_gate = new(1, 1);
    private static readonly Encoding s_shiftJis = CodePagesEncodingProvider.Instance.GetEncoding(932,
        new EncoderReplacementFallback(""), DecoderFallback.ReplacementFallback)!;

    // Characters that Unicode text uses where Shift_JIS has a look-alike of its own.
    private static readonly Dictionary<char, char> s_shiftJisVariants = new()
    {
        ['〜'] = '～', ['‖'] = '∥', ['−'] = '－', ['¢'] = '￠',
        ['£'] = '￡', ['¬'] = '￢', ['—'] = '―'
    };

    // The command line takes the voice by preset name and nothing else of it, so speed, pitch and volume stay with the
    // preset. The silence around the speech is adjusted afterwards.
    public static VoiceEngineCapabilities Capabilities { get; } = new()
    {
        AdjustSpeed = false, AdjustPitch = false, AdjustIntonation = false, AdjustVolume = false,
        // Accents analyzed by VOICEVOX reach the app as phonetic symbols; pitches have no way there.
        EditAccent = true
    };

    // AquesTalkPlayer speaks at its engines' own rates, such as 8 kHz, and like the speech from the Yukumo site it
    // sounds muffled through the conversion that otherwise follows the effects. Its speech is converted with this both
    // in the preview and on the timeline.
    public const ResamplingMethod Resampling = ResamplingMethod.Lagrange;

    internal static AquesTalkPlayerHost Host { get; set; } = AquesTalkPlayerHost.Current;
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    public static string? FindApplication()
    {
        var host = Host;
        string home = host.Home;
        string[] roots = host.IsMacOS
            ? ["/Applications", Path.Combine(home, "Applications")]
            : host.IsWindows
                ?
                [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Path.Combine(home, "Downloads"),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    home,
                    Path.GetPathRoot(Environment.SystemDirectory) ?? ""
                ]
                : [];
        return FindApplication(roots, host.Platform);
    }

    internal static string? FindApplication(IEnumerable<string> roots, string platform)
    {
        foreach (string root in roots.Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root)))
        {
            if (platform == "macos")
            {
                string bundle = Path.Combine(root, ApplicationName + ".app");
                if (File.Exists(Path.Combine(bundle, "Contents", "MacOS", ApplicationName))) return bundle;
            }
            else if (platform == "windows")
            {
                // The package is a zip extracted anywhere. Its aquestalkplayer folder holds the executable, often in a
                // folder named after the zip, such as aquestalkplayer_20261027.
                foreach (string folder in WindowsFolders(root))
                {
                    string path = Path.Combine(folder, ApplicationName + ".exe");
                    if (File.Exists(path)) return path;
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> WindowsFolders(string root)
    {
        string[] archives;
        try
        {
            archives = Directory.GetDirectories(root, "aquestalkplayer*", new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true
            });
            Array.Sort(archives, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { archives = []; }
        foreach (string archive in archives)
        {
            yield return archive;
            yield return Path.Combine(archive, "aquestalkplayer");
        }
    }

    // A chosen app bundle or folder means the executable inside it.
    internal static string ResolveExecutable(string applicationPath, AquesTalkPlayerHost host)
    {
        if (string.IsNullOrWhiteSpace(applicationPath))
            throw new InvalidOperationException("AquesTalkPlayerが登録されていません。「設定」の「音声エンジンを探す」から追加してください。");
        string path = Path.GetFullPath(applicationPath);
        if (Directory.Exists(path))
            path = host.IsMacOS ? Path.Combine(path, "Contents", "MacOS", ApplicationName) : Path.Combine(path, ApplicationName + ".exe");
        if (!File.Exists(path))
            throw new FileNotFoundException($"AquesTalkPlayerが見つかりません: {applicationPath}。「設定」の「音声エンジンを探す」でアプリを選び直してください。", path);
        return path;
    }

    internal static string PresetPath(string executable, AquesTalkPlayerHost host) => host.IsMacOS
        ? Path.Combine(host.Home, "Library", "Application Support", ApplicationName, "params.json")
        : Path.Combine(Path.GetDirectoryName(executable)!, ApplicationName + ".preset");

    public static VoiceEngineConnection Discover(string applicationPath, VoiceEngineConnection? registered = null) =>
        Discover(applicationPath, registered, Host);

    internal static VoiceEngineConnection Discover(string applicationPath, VoiceEngineConnection? registered, AquesTalkPlayerHost host)
    {
        string executable = ResolveExecutable(applicationPath, host);
        string presetPath = PresetPath(executable, host);
        AquesTalkPlayerPreset[] presets;
        try { presets = AquesTalkPlayerPresets.Load(presetPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        {
            throw new InvalidOperationException($"AquesTalkPlayerのプリセットを読み込めませんでした（{presetPath}）: {ex.Message}", ex);
        }
        return new VoiceEngineConnection
        {
            Id = VoiceSettings.AquesTalkPlayerEngineId,
            Name = string.IsNullOrWhiteSpace(registered?.Name) ? ApplicationName : registered.Name,
            ApplicationPath = Path.GetFullPath(applicationPath), Version = ReadVersion(executable, host),
            Capabilities = Capabilities, Voices = presets.Select(preset => preset.ToVoice()).ToArray()
        };
    }

    // Only shown beside the name, so a version that cannot be read is left out.
    private static string ReadVersion(string executable, AquesTalkPlayerHost host)
    {
        try
        {
            if (host.IsWindows) return FileVersionInfo.GetVersionInfo(executable).FileVersion?.Trim() ?? "";
            // Contents/MacOS/AquesTalkPlayer, beside Contents/Info.plist.
            string plist = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(executable)!)!, "Info.plist");
            return File.Exists(plist) && BundleVersion().Match(File.ReadAllText(plist)) is { Success: true } match
                ? match.Groups[1].Value.Trim() : "";
        }
        catch (Exception ex)
        {
            s_logger.LogDebug(ex, "Could not read the AquesTalkPlayer version");
            return "";
        }
    }

    [GeneratedRegex(@"<key>CFBundleShortVersionString</key>\s*<string>([^<]*)</string>")]
    private static partial Regex BundleVersion();

    // Without an analysis the app reads the text itself; with one, it reads the analysis as phonetic symbols.
    public static Task<VoiceSynthesisResult> SynthesizeAsync(string text, VoiceSettings settings, AudioQueryModel? query,
        CancellationToken cancellationToken) =>
        SynthesizeAsync(text, settings, query, Host, cancellationToken);

    internal static async Task<VoiceSynthesisResult> SynthesizeAsync(string text, VoiceSettings settings, AudioQueryModel? query,
        AquesTalkPlayerHost host, CancellationToken cancellationToken)
    {
        string spoken = query == null ? text : AquesTalkPhonetic.FromQuery(query);
        if (string.IsNullOrEmpty(settings.SpeakerUuid)) throw new InvalidOperationException("AquesTalkPlayerのプリセットを選択してください。");
        var registered = VoiceEngineStore.Load().FirstOrDefault(connection => connection.IsAquesTalkPlayer)
            ?? throw new InvalidOperationException("AquesTalkPlayerが登録されていません。「設定」の「音声エンジンを探す」から追加してください。");
        string executable = ResolveExecutable(registered.ApplicationPath, host);
        await s_gate.WaitAsync(cancellationToken);
        try
        {
            // The presets only describe the voice; the command line decides whether the preset exists.
            var connection = registered;
            try { connection = await Task.Run(() => Discover(registered.ApplicationPath, registered, host), cancellationToken); }
            catch (InvalidOperationException ex) { s_logger.LogWarning(ex, "Could not read the AquesTalkPlayer presets"); }
            var preset = connection.Voices.FirstOrDefault(item => item.SpeakerUuid == settings.SpeakerUuid);
            // Values tuned in Beutl reach the app through a temporary preset; untuned, the preset speaks as it is.
            var tuned = settings.AquesTalkPlayerVoice?.Clamped();
            byte[] wave = tuned == null || tuned == preset?.AquesTalkPlayer
                ? await RunAsync(executable, settings.SpeakerUuid, spoken, host, cancellationToken)
                : await AquesTalkPlayerTemporaryPreset.UseAsync(PresetPath(executable, host), tuned,
                    name => RunAsync(executable, name, spoken, host, cancellationToken), cancellationToken);
            wave = VoiceWave.AdjustSilence(wave, settings.PrePhonemeLength, settings.PostPhonemeLength);
            var voice = tuned ?? preset?.AquesTalkPlayer;
            string json = query == null ? "" : JsonSerializer.Serialize(query, VoiceSynthesisService.JsonOptions);
            return new(wave, query, json, settings.WithSupportedTuning() with
            {
                EngineName = connection.Name, EngineUuid = "", EngineVersion = connection.Version,
                SpeakerName = preset?.Name ?? settings.SpeakerName,
                StyleName = voice != null ? $"{voice.Engine} {voice.Voice}".Trim() : settings.StyleName,
                ModelVersion = preset?.Version ?? settings.ModelVersion, AquesTalkPlayerVoice = voice
            });
        }
        finally { s_gate.Release(); }
    }

    private static async Task<byte[]> RunAsync(string executable, string preset, string text, AquesTalkPlayerHost host,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), "beutl-aquestalkplayer-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(directory, "speech.wav");
        Directory.CreateDirectory(directory);
        try
        {
            using (var process = new Process { StartInfo = CreateStartInfo(executable, preset, text, output, host) })
            {
                try { process.Start(); }
                catch (Win32Exception ex) { throw new InvalidOperationException($"AquesTalkPlayerを起動できませんでした: {ex.Message}", ex); }
                // Read what it writes, so that a full pipe cannot stop it; error output can contain the text.
                var drained = Task.WhenAll(process.StandardOutput.ReadToEndAsync(CancellationToken.None),
                    process.StandardError.ReadToEndAsync(CancellationToken.None));
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(Timeout);
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException)
                    {
                        Kill(process);
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("AquesTalkPlayerが応答しませんでした。AquesTalkPlayerを一度起動して、確認の画面などが出ていないか確かめてください。");
                    }
                }
                await drained;
                if (process.ExitCode != 0) throw new InvalidOperationException(DescribeExitCode(process.ExitCode, preset, host));
            }
            // An empty text exits normally without writing anything.
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new InvalidOperationException("AquesTalkPlayerが音声を出力しませんでした。読み上げられる文字が含まれているか確認してください。");
            byte[] wave = await File.ReadAllBytesAsync(output, cancellationToken);
            if (!VoiceWave.IsWave(wave)) throw new InvalidOperationException("AquesTalkPlayerが出力した音声を読み込めませんでした。");
            return wave;
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                s_logger.LogWarning(ex, "Could not delete the AquesTalkPlayer output folder {Path}", directory);
            }
        }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string preset, string text, string output, AquesTalkPlayerHost host)
    {
        // The Windows version takes /T-style options and the macOS version -T-style ones.
        string prefix = host.IsWindows ? "/" : "-";
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in (string[])[prefix + "P", preset, prefix + "T", PrepareText(text, host), prefix + "W", output])
            start.ArgumentList.Add(argument);
        return start;
    }

    // The Windows version reads its arguments as Shift_JIS, where a character without a Shift_JIS form arrives as "?",
    // which it reads as a question, and a double quote ends the text early.
    internal static string PrepareText(string text, AquesTalkPlayerHost host)
    {
        string lines = text.ReplaceLineEndings(host.IsWindows ? "\r\n" : "\n");
        if (!host.IsWindows) return lines;
        var mapped = new StringBuilder(lines.Length);
        foreach (char c in lines)
            if (c != '"') mapped.Append(s_shiftJisVariants.GetValueOrDefault(c, c));
        return s_shiftJis.GetString(s_shiftJis.GetBytes(mapped.ToString()));
    }

    internal static string DescribeExitCode(int code, string preset, AquesTalkPlayerHost host)
    {
        if (host.IsWindows ? code is 2019 or 2021 : code == 29)
            return $"AquesTalkPlayerにプリセット「{preset}」が見つかりません。AquesTalkPlayerでプリセット名を確認し、エンジンの「接続確認・再読み込み」で一覧を更新してください。";
        string detail = (host.IsWindows ? s_windowsErrors : s_macErrors).GetValueOrDefault(code, "不明なエラー");
        return $"AquesTalkPlayerで音声を生成できませんでした（終了コード {code}: {detail}）。";
    }

    private static readonly Dictionary<int, string> s_windowsErrors = new()
    {
        [2000] = "予期せぬエラー", [2001] = "システム辞書(aqdic.bin)が不正", [2002] = "ユーザ辞書(aq_user.dic)が不正",
        [2003] = "AqKanji2Koeの初期化エラー", [2004] = "音声記号列が指定されていない", [2005] = "DLLの読み込みに失敗",
        [2006] = "不正なDLL", [2007] = "PHONTファイルの読み込みに失敗", [2008] = "音声記号列に未定義の読み記号が指定された",
        [2009] = "音声記号列のタグの指定が正しくない", [2010] = "発話文が長すぎる", [2011] = "Phontファイルが不正",
        [2012] = "音声記号列が正しくない", [2013] = "メモリ不足", [2014] = "テキストが空",
        [2015] = "AqKanji2Koeの変換エラー", [2016] = "WAVファイルを書き込めない", [2017] = "プリセットファイルが壊れている",
        [2018] = "プリセットファイルを開けない", [2020] = "プリセットファイルの書き込みに失敗", [2022] = "テキストファイルを開けない"
    };

    private static readonly Dictionary<int, string> s_macErrors = new()
    {
        [10] = "予期せぬエラー", [11] = "辞書ファイルのコピーに失敗", [14] = "音声記号列が指定されていない",
        [15] = "DLLの読み込みに失敗", [16] = "不正なDLL", [17] = "PHONTファイルの読み込みに失敗", [21] = "Phontデータが正しくない",
        [24] = "テキストが空", [26] = "WAVファイルを書き込めない", [32] = "テキストファイルを開けない",
        [100] = "AquesTalkのその他のエラー", [101] = "メモリ不足", [102] = "音声記号列に未定義の読み記号が指定された",
        [103] = "音声記号列の指定が正しくない（語頭の長音、促音の連続など）", [104] = "音声記号列に有効な読みがない",
        [105] = "音声記号列に未定義の読み記号が指定された", [106] = "音声記号列のタグの指定が正しくない",
        [107] = "タグの長さが制限を超えている", [108] = "タグ内の値の指定が正しくない", [120] = "音声記号列が長すぎる",
        [121] = "1つのフレーズの読み記号が多すぎる", [122] = "音声記号列が長すぎる", [130] = "Kanji2Koeのその他のエラー",
        [131] = "引数が指定されていない", [134] = "Kanji2Koeが初期化されていない", [135] = "テキストが長すぎる",
        [136] = "システム辞書が指定されていない", [137] = "変換できない文字が含まれている", [138] = "システム辞書が壊れている",
        [139] = "ユーザ辞書が壊れている", [200] = "音声記号列が長すぎる", [201] = "1つのフレーズの読み記号が多すぎる",
        [202] = "音声記号列が長すぎる", [203] = "メモリ不足", [204] = "音声記号列が長すぎる"
    };
}
