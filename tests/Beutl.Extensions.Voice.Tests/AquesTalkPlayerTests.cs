using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Beutl.Audio;
using Beutl.Audio.Effects;
using Beutl.Editor.Observers;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;

namespace Beutl.Extensions.Voice.Tests;

// AquesTalkPlayer, which the user installs, speaks through its command line with its presets as the voices. A script
// stands in for the app: it records its arguments and answers as the app does.
public class AquesTalkPlayerTests
{
    private string _directory = null!;
    private string _home = null!;
    private string? _previousHome;
    private AquesTalkPlayerHost _previousHost = null!;
    private TimeSpan _previousTimeout;

    private string ArgumentsPath => Path.Combine(_directory, "arguments");
    private string ProcessIdPath => Path.Combine(_directory, "pid");
    private string PresetPath => Path.Combine(_home, "Library", "Application Support", "AquesTalkPlayer", "params.json");

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "voice-aquestalkplayer-" + Guid.NewGuid());
        _home = Path.Combine(_directory, "home");
        Directory.CreateDirectory(_home);
        _previousHome = Environment.GetEnvironmentVariable("BEUTL_HOME");
        Environment.SetEnvironmentVariable("BEUTL_HOME", _directory);
        _previousHost = AquesTalkPlayerEngine.Host;
        _previousTimeout = AquesTalkPlayerEngine.Timeout;
        // The macOS version's layout, with its settings in a home of the test's own.
        AquesTalkPlayerEngine.Host = new("macos", _home);
    }

    [TearDown]
    public void TearDown()
    {
        AquesTalkPlayerEngine.Host = _previousHost;
        AquesTalkPlayerEngine.Timeout = _previousTimeout;
        Environment.SetEnvironmentVariable("BEUTL_HOME", _previousHome);
        Directory.Delete(_directory, true);
    }

    private TestEditor Editor() => new(Path.Combine(_directory, "scene.bescene"));

    private static VoiceSettings Voice(string preset) => new()
    {
        EngineId = VoiceSettings.AquesTalkPlayerEngineId, EngineName = "AquesTalkPlayer", SpeakerUuid = preset, SpeakerName = preset
    };

    private static object Preset(string name, string engine, string voice, bool flat = false, int speed = 100) => new
    {
        fsc = 100, engine, spd = speed, voice, vol = 100, lmd = 100, acc = 100, name, memo = "", pit = 100, boyomi = flat
    };

    // The preset file as the app saw it while it spoke.
    private string SeenPresetPath => Path.Combine(_directory, "seen-presets");

    private void WritePresets(params object[] presets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PresetPath)!);
        File.WriteAllText(PresetPath, JsonSerializer.Serialize(presets));
    }

    // An app bundle whose executable records its arguments, then speaks, fails, says nothing or never ends.
    private string FakeApp(string behavior, int sampleRate = 8000)
    {
        string bundle = Path.Combine(_directory, "AquesTalkPlayer.app");
        string executable = Path.Combine(bundle, "Contents", "MacOS", "AquesTalkPlayer");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
            	<key>CFBundleShortVersionString</key>
            	<string>1.0.2</string>
            </dict></plist>
            """);
        string wave = Path.Combine(_directory, "app-speech.wav");
        File.WriteAllBytes(wave, Wave(sampleRate, 400, 1000, 600));
        string action = behavior switch
        {
            "speak" => $"cp '{wave}' \"$out\"",
            "missing preset" => "exit 29",
            "silent" => "exit 0",
            "hang" => $"echo $$ > '{ProcessIdPath}'; exec sleep 30",
            _ => throw new ArgumentOutOfRangeException(nameof(behavior))
        };
        File.WriteAllText(executable, $"""
            #!/bin/sh
            printf '%s\0' "$@" > '{ArgumentsPath}'
            rm -f '{SeenPresetPath}'
            cp '{PresetPath}' '{SeenPresetPath}' 2>/dev/null
            out=""
            while [ $# -gt 0 ]; do
              if [ "$1" = "-W" ]; then out="$2"; fi
              shift
            done
            {action}

            """.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return bundle;
    }

    private static VoiceEngineConnection Register(string application)
    {
        var connection = AquesTalkPlayerEngine.Discover(application);
        VoiceEngineStore.Save(connection);
        return connection;
    }

    private string[] ReadArguments() => File.ReadAllText(ArgumentsPath).Split('\0')[..^1];

    internal static byte[] Wave(int sampleRate, int leading, int speech, int trailing)
    {
        int frames = leading + speech + trailing;
        var wave = new byte[44 + frames * 2];
        var header = wave.AsSpan();
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + frames * 2);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], frames * 2);
        for (int i = 0; i < speech; i++)
            BinaryPrimitives.WriteInt16LittleEndian(header[(44 + (leading + i) * 2)..], (short)(i % 2 == 0 ? 1000 : -1000));
        return wave;
    }

    private static short[] Samples(byte[] wave)
    {
        Assert.That(VoiceWave.TryReadFormat(wave, out var format), Is.True);
        var data = wave.AsSpan(format.DataOffset, format.DataLength);
        var samples = new short[data.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]);
        return samples;
    }

    private static async Task Settle()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
    }

    private static void Capture(TopLevel topLevel, string name)
    {
        if (Environment.GetEnvironmentVariable("VOICE_TEST_CAPTURES") is not { } directory) return;
        Directory.CreateDirectory(directory);
        using var frame = topLevel.CaptureRenderedFrame()!;
        frame.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions());
    }

    [Test]
    public void Reads_the_windows_presets_from_the_shift_jis_csv_beside_the_app()
    {
        string folder = Path.Combine(_directory, "aquestalkplayer");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "AquesTalkPlayer.exe"), []);
        string csv = "名前,棒読み,エンジン,声種,話速,音量,高さ,アクセント,声質,音程,メモ\r\n"
            + "デフォルト,0,AquesTalk10,F1E,100,100,100,100,100,100,AquesTalk10 女声\r\n"
            + "れいむ,1,AquesTalk1,f1,100,100,100,100,100,100,ゆっくり霊夢\r\n"
            + "\"話者, 引用\",true,AquesTalk2,aq_yukkuri,90,100,100,100,100,100,\r\n"
            + "れいむ,0,AquesTalk1,f2,100,100,100,100,100,100,\r\n";
        File.WriteAllBytes(Path.Combine(folder, "AquesTalkPlayer.preset"), CodePagesEncodingProvider.Instance.GetEncoding(932)!.GetBytes(csv));

        var connection = AquesTalkPlayerEngine.Discover(folder, null, new("windows", _home));

        Assert.Multiple(() =>
        {
            Assert.That(connection.Id, Is.EqualTo(VoiceSettings.AquesTalkPlayerEngineId));
            Assert.That(connection.IsAquesTalkPlayer, Is.True);
            Assert.That(connection.Location, Is.EqualTo(Path.GetFullPath(folder)));
            // The command line speaks the first preset of a name.
            Assert.That(connection.Voices.Select(x => x.SpeakerUuid), Is.EqualTo(new[] { "デフォルト", "れいむ", "話者, 引用" }));
            Assert.That(connection.Voices.Select(x => x.Styles.Single().Name),
                Is.EqualTo(new[] { "AquesTalk10 F1E", "AquesTalk1 f1", "AquesTalk2 aq_yukkuri" }));
            Assert.That(connection.Voices[2].AquesTalkPlayer, Is.EqualTo(new AquesTalkPlayerVoice
            {
                Engine = "AquesTalk2", Voice = "aq_yukkuri", FlatReading = true, Speed = 90
            }));
            Assert.That(connection.Voices[1].AquesTalkPlayer!.FlatReading, Is.True);
            Assert.That(connection.Capabilities, Is.EqualTo(AquesTalkPlayerEngine.Capabilities));
        });
    }

    [Test]
    public void Reads_the_mac_presets_and_uses_the_shipped_ones_until_one_is_saved()
    {
        string bundle = FakeApp("speak");

        var shipped = AquesTalkPlayerEngine.Discover(bundle);
        Assert.That(shipped.Voices.Select(x => x.Name), Is.EqualTo(AquesTalkPlayerPresets.Defaults.Select(x => x.Name)));
        Assert.That(shipped.Voices, Has.Length.EqualTo(31));
        Assert.That(shipped.Version, Is.EqualTo("1.0.2"));

        WritePresets(Preset("れいむ", "AquesTalk1", "f1", flat: true), Preset("自作の声", "AquesTalk10", "M1E"), new { engine = "AquesTalk1" });
        var saved = AquesTalkPlayerEngine.Discover(bundle);
        Assert.That(saved.Voices.Select(x => x.Name), Is.EqualTo(new[] { "れいむ", "自作の声" }));
        Assert.That(saved.Voices[0].Styles.Single().Name, Is.EqualTo("AquesTalk1 f1"));
        Assert.That(saved.Voices[0].AquesTalkPlayer!.FlatReading, Is.True);
        Assert.That(shipped.Voices.Single(x => x.Name == "こいし").AquesTalkPlayer,
            Is.EqualTo(new AquesTalkPlayerVoice { Engine = "AquesTalk1", Voice = "f2", FlatReading = true, Speed = 75, FrequencyScale = 181 }));

        File.WriteAllText(PresetPath, "{ broken");
        var error = Assert.Throws<InvalidOperationException>(() => AquesTalkPlayerEngine.Discover(bundle))!;
        Assert.That(error.Message, Does.Contain("params.json"));
        Assert.That(() => AquesTalkPlayerEngine.Discover(Path.Combine(_directory, "missing.app")),
            Throws.InstanceOf<FileNotFoundException>().With.Message.Contains("見つかりません"));
    }

    [TestCase("aquestalkplayer")]
    [TestCase("AquesTalkPlayer")]
    [TestCase("aquestalkplayer_20261027/aquestalkplayer")]
    public void Finds_the_windows_app_where_its_zip_was_extracted(string folder)
    {
        string root = Path.Combine(_directory, "Downloads");
        string path = Path.Combine(root, folder, "AquesTalkPlayer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Assert.That(AquesTalkPlayerEngine.FindApplication([root], "windows"), Is.Null);
        File.WriteAllBytes(path, []);
        Assert.That(AquesTalkPlayerEngine.FindApplication([Path.Combine(_directory, "missing"), root], "windows"), Is.EqualTo(path));
    }

    [Test]
    public void Finds_the_mac_app_only_with_its_executable()
    {
        string root = Path.Combine(_directory, "Applications");
        string bundle = Path.Combine(root, "AquesTalkPlayer.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        Assert.That(AquesTalkPlayerEngine.FindApplication([root], "macos"), Is.Null);
        File.WriteAllBytes(Path.Combine(bundle, "Contents", "MacOS", "AquesTalkPlayer"), []);
        Assert.That(AquesTalkPlayerEngine.FindApplication([root], "macos"), Is.EqualTo(bundle));
        Assert.That(AquesTalkPlayerEngine.FindApplication([root], "linux"), Is.Null, "There is no Linux version.");
    }

    [Test]
    public void Builds_each_versions_documented_command_line()
    {
        var windows = AquesTalkPlayerEngine.CreateStartInfo("/apps/aquestalkplayer/AquesTalkPlayer.exe", "れいむ", "本文", "/tmp/out.wav", new("windows", _home));
        var mac = AquesTalkPlayerEngine.CreateStartInfo("/Applications/AquesTalkPlayer.app/Contents/MacOS/AquesTalkPlayer", "まりさ", "本文", "/tmp/out.wav", new("macos", _home));
        Assert.Multiple(() =>
        {
            Assert.That(windows.ArgumentList, Is.EqualTo(new[] { "/P", "れいむ", "/T", "本文", "/W", "/tmp/out.wav" }));
            Assert.That(mac.ArgumentList, Is.EqualTo(new[] { "-P", "まりさ", "-T", "本文", "-W", "/tmp/out.wav" }));
            Assert.That(mac.WorkingDirectory, Is.EqualTo("/Applications/AquesTalkPlayer.app/Contents/MacOS"));
            Assert.That(mac.UseShellExecute, Is.False);
        });
    }

    [Test]
    public void Keeps_the_windows_text_within_shift_jis_and_its_quoting()
    {
        const string text = "「こんにちは」\"引用\"〜🎉\nつぎ−の行";
        Assert.That(AquesTalkPlayerEngine.PrepareText(text, new("windows", _home)), Is.EqualTo("「こんにちは」引用～\r\nつぎ－の行"));
        Assert.That(AquesTalkPlayerEngine.PrepareText(text, new("macos", _home)), Is.EqualTo(text));
    }

    [Test]
    public void Describes_exit_codes_in_each_versions_terms()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AquesTalkPlayerEngine.DescribeExitCode(2021, "れいむ", new("windows", _home)), Does.Contain("プリセット「れいむ」が見つかりません"));
            Assert.That(AquesTalkPlayerEngine.DescribeExitCode(29, "れいむ", new("macos", _home)), Does.Contain("プリセット「れいむ」が見つかりません"));
            Assert.That(AquesTalkPlayerEngine.DescribeExitCode(2010, "れいむ", new("windows", _home)), Does.Contain("2010: 発話文が長すぎる"));
            Assert.That(AquesTalkPlayerEngine.DescribeExitCode(105, "れいむ", new("macos", _home)), Does.Contain("105: 音声記号列に未定義の読み記号"));
            Assert.That(AquesTalkPlayerEngine.DescribeExitCode(250, "れいむ", new("macos", _home)), Does.Contain("250: 不明なエラー"));
        });
    }

    [Test]
    public void Replaces_the_silence_around_speech_and_leaves_other_audio_alone()
    {
        var samples = Samples(VoiceWave.AdjustSilence(Wave(8000, 400, 1000, 600), 0.1, 0.25));
        Assert.Multiple(() =>
        {
            Assert.That(samples, Has.Length.EqualTo(800 + 1000 + 2000));
            Assert.That(samples.Take(800), Is.All.Zero);
            Assert.That(samples[800], Is.Not.Zero);
            Assert.That(samples[1799], Is.Not.Zero);
            Assert.That(samples.Skip(1800), Is.All.Zero);
        });
        byte[] silent = Wave(8000, 100, 0, 100);
        byte[] notWave = [1, 2, 3];
        Assert.That(VoiceWave.AdjustSilence(silent, 0.1, 0.1), Is.SameAs(silent));
        Assert.That(VoiceWave.AdjustSilence(notWave, 0.1, 0.1), Is.SameAs(notWave));
    }

    [Test]
    public void Settings_keep_aquestalkplayer_values_in_place_of_the_other_engines_tuning()
    {
        var settings = Voice("れいむ") with { SpeedScale = 1.5, VolumeScale = 0.5, TempoDynamicsScale = 1.2, PrePhonemeLength = 0.3, StyleName = "AquesTalk1 f1" };
        var supported = settings.WithSupportedTuning();
        var tuned = supported with
        {
            AquesTalkPlayerVoice = new() { Engine = "AquesTalk10", Voice = "F1E", Speed = 400, Height = 77, FlatReading = true }
        };
        Assert.Multiple(() =>
        {
            Assert.That(supported.SpeedScale, Is.EqualTo(1));
            Assert.That(supported.VolumeScale, Is.EqualTo(1));
            Assert.That(supported.TempoDynamicsScale, Is.Null);
            Assert.That(supported.PrePhonemeLength, Is.EqualTo(0.3));
            Assert.That(supported.Describe(), Does.Not.Contain("話速"));
            Assert.That(settings.Describe(), Does.Not.Contain("話速").And.Not.Contain("緩急"));
            Assert.That(supported.Describe(), Does.Contain("前無音 0.3 秒"));
            Assert.That(settings.DescribeDifferences(supported), Is.EqualTo("現在の音声設定と一致しています。"));
            Assert.That(tuned.WithSupportedTuning().AquesTalkPlayerVoice!.Speed, Is.EqualTo(300), "Values stay within the app's range.");
            Assert.That(tuned.WithSupportedTuning().Describe().Split('\n')[1], Is.EqualTo("話速 300・音量 100・高さ 77・アクセント 100・声質 100・音程 100・棒読み"));
            Assert.That(supported.DescribeDifferences(tuned), Does.StartWith("AquesTalkPlayer: プリセットのまま → AquesTalk10 F1E"));
            Assert.That(JsonSerializer.Serialize(settings), Does.Not.Contain(nameof(VoiceSettings.IsAquesTalkPlayer)).And.Not.Contain(nameof(VoiceSettings.AquesTalkPlayerVoice)));
            Assert.That(JsonSerializer.Deserialize<VoiceSettings>(JsonSerializer.Serialize(tuned)), Is.EqualTo(tuned));
        });
        var other = new VoiceSettings { SpeedScale = 1.5 };
        Assert.That(other.WithSupportedTuning(), Is.SameAs(other));
        Assert.That((other with { AquesTalkPlayerVoice = new() }).WithSupportedTuning().AquesTalkPlayerVoice, Is.Null,
            "Other engines have none of AquesTalkPlayer's values.");
    }

    [Test]
    public void Analysis_needs_voicevox_core()
    {
        var previous = TtsLoader.VoiceVoxLoader.Value;
        TtsLoader.VoiceVoxLoader.Value = null;
        try
        {
            Assert.That(() => VoiceSynthesisService.CreateQueryAsync("テスト", Voice("れいむ")),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("VOICEVOX Core"));
            Assert.That(() => VoiceSynthesisService.ReplaceMoraPitchAsync([], Voice("れいむ")),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("VOICEVOX Core"));
        }
        finally { TtsLoader.VoiceVoxLoader.Value = previous; }
    }

    private static AccentPhraseModel Phrase(string moras, int accent, bool pause = false, bool question = false) => new()
    {
        // "カナ:vowel" per mora, with "-" for a mora without a consonant.
        Moras = [.. moras.Split(' ').Select(mora => mora.Split(':')).Select(part => new MoraModel
        {
            Text = part[0], Vowel = part[2], Consonant = part[1] == "-" ? null : part[1]
        })],
        Accent = accent, PauseMora = pause ? new MoraModel { Text = "、", Vowel = "pau" } : null, IsInterrogative = question
    };

    [Test]
    public void Writes_voicevox_analysis_in_the_apps_phonetic_symbols()
    {
        static string Write(params AccentPhraseModel[] phrases) => AquesTalkPhonetic.FromQuery(new AudioQueryModel { AccentPhrases = [.. phrases] });
        Assert.Multiple(() =>
        {
            // 橋を渡る: the nucleus after シ, and a flat phrase (VOICEVOX's accent on the last mora) left unmarked.
            Assert.That(Write(Phrase("ハ:h:a シ:sh:i オ:-:o", 2), Phrase("ワ:w:a タ:t:a ル:r:u", 3)), Is.EqualTo("#>はし'お/わたる。"));
            // A pause, a question, and a long vowel spelled as the vowel again, which cannot begin a phrase.
            Assert.That(Write(Phrase("キョ:ky:o オ:-:o", 1, pause: true), Phrase("オ:-:o オ:-:o キ:k:i イ:-:i", 3, question: true)),
                Is.EqualTo("#>きょ'ー、おーき'ー？"));
            // Readings without a symbol of their own.
            Assert.That(Write(Phrase("ヴァ:v:a イ:-:i ヅ:z:u", 1)), Is.EqualTo("#>ば'いず。"));
            Assert.That(() => Write(), Throws.InvalidOperationException);
        });
    }

    [Test, Platform(Exclude = "Win")]
    public async Task An_analysis_speaks_as_phonetic_symbols_and_is_kept_with_the_speech()
    {
        WritePresets(Preset("れいむ", "AquesTalk1", "f1", flat: true));
        Register(FakeApp("speak"));
        var query = new AudioQueryModel { AccentPhrases = [Phrase("ハ:h:a シ:sh:i オ:-:o", 1), Phrase("ツ:ts:U カ:k:a ウ:-:u", 3)] };

        var output = await VoiceSynthesisService.SynthesizeAsync("箸を使う", Voice("れいむ"), query);

        Assert.That(ReadArguments()[..4], Is.EqualTo(new[] { "-P", "れいむ", "-T", "#>は'しお/つかう。" }));
        Assert.That(output.Query, Is.SameAs(query));
        Assert.That(JsonSerializer.Deserialize<AudioQueryModel>(output.Json)!.AccentPhrases[0].Accent, Is.EqualTo(1));
    }

    [AvaloniaTest, Platform(Exclude = "Win")]
    public async Task Speech_tab_reads_the_text_itself_until_it_is_analyzed()
    {
        WritePresets(Preset("れいむ", "AquesTalk1", "f1", flat: true));
        Register(FakeApp("speak"));
        using var editor = Editor();
        using var model = new TtsTabViewModel(new(), editor);
        model.SelectedEngine.Value = model.Engines.Value.Single(x => x.IsAquesTalkPlayer);
        model.SelectedVoice.Value = model.Voice.Value.Single();
        Assert.That(model.EngineCapabilities.Value.EditAccent, Is.True);
        model.Text.Value = "箸を使う";
        // What "テキストを解析" publishes, with the accent then moved.
        model.AudioQuery.Value = new AudioQueryModel { AccentPhrases = [Phrase("ハ:h:a シ:sh:i オ:-:o", 1), Phrase("ツ:ts:U カ:k:a ウ:-:u", 3)] };

        await model.Generate();

        Assert.That(model.Message.Value, Is.Empty);
        Assert.That(ReadArguments()[3], Is.EqualTo("#>は'しお/つかう。"));
        var generation = editor.Scene.Children.Single().Objects.OfType<TtsController>().Single().Generation.Value!;
        Assert.That(generation.AudioQueryJson, Is.Not.Empty, "Regenerating keeps the accents.");

        model.Text.Value = "箸を使います";
        Assert.That(model.AudioQuery.Value, Is.Null, "New text drops the analysis of the old.");
        await model.Generate();
        Assert.That(ReadArguments()[3], Is.EqualTo("箸を使います"));
    }

    [AvaloniaTest, Platform(Exclude = "Win")]
    public async Task Speech_tab_adds_the_presets_speech_with_its_silence_and_resampling_in_one_undo()
    {
        WritePresets(Preset("れいむ", "AquesTalk1", "f1", flat: true));
        Register(FakeApp("speak"));
        using var editor = Editor();
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        using var model = new TtsTabViewModel(new(), editor);
        model.SelectedEngine.Value = model.Engines.Value.Single(x => x.IsAquesTalkPlayer);
        Assert.Multiple(() =>
        {
            Assert.That(model.IsAquesTalkPlayer.Value, Is.True);
            Assert.That(model.IsExternalEngine.Value, Is.True);
            Assert.That(model.CanEditConnection.Value, Is.False, "There is no URL to edit.");
            Assert.That(model.EngineCapabilities.Value, Is.EqualTo(AquesTalkPlayerEngine.Capabilities));
            Assert.That(model.IsEnabled.Value, Is.True);
        });
        model.SelectedVoice.Value = model.Voice.Value.Single();
        Assert.That(model.SelectedStyle.Value!.Name, Is.EqualTo("AquesTalk1 f1"));
        Assert.That(model.AquesTalkFlatReading.Value, Is.True, "Choosing a preset starts from its values.");
        model.Text.Value = "ゆっくり\nしていってね";
        model.SpeedScale.Value = 1.5;
        model.PrePhonemeLength.Value = 0.2;
        model.PostPhonemeLength.Value = 0.05;

        await model.Generate();

        Assert.That(model.Message.Value, Is.Empty);
        string[] arguments = ReadArguments();
        var element = editor.Scene.Children.Single();
        var sound = element.Objects.OfType<SourceSound>().Single();
        var generation = element.Objects.OfType<TtsController>().Single().Generation.Value!;
        var samples = Samples(File.ReadAllBytes(sound.Source.Value!.Uri.LocalPath));
        Assert.Multiple(() =>
        {
            Assert.That(arguments[..4], Is.EqualTo(new[] { "-P", "れいむ", "-T", "ゆっくり\nしていってね" }), "An untuned preset speaks as it is.");
            Assert.That(arguments[4], Is.EqualTo("-W"));
            Assert.That(Directory.Exists(Path.GetDirectoryName(arguments[5])), Is.False, "The app's output folder is removed.");
            Assert.That(((AudioEffectGroup)sound.Effect.Value!).Children.Select(x => (x as ResamplingEffect)?.Method.Value),
                Is.EqualTo(new ResamplingMethod?[] { ResamplingMethod.Lagrange }), "AquesTalkPlayer speech is resampled first.");
            Assert.That(samples, Has.Length.EqualTo(1600 + 1000 + 400));
            Assert.That(element.Length.Value.ToSeconds(), Is.EqualTo(3000 / 8000.0).Within(0.02));
            Assert.That(generation.Settings.EngineId, Is.EqualTo(VoiceSettings.AquesTalkPlayerEngineId));
            Assert.That(generation.Settings.SpeakerUuid, Is.EqualTo("れいむ"));
            Assert.That(generation.Settings.StyleName, Is.EqualTo("AquesTalk1 f1"));
            Assert.That(generation.Settings.AquesTalkPlayerVoice, Is.EqualTo(new AquesTalkPlayerVoice { Engine = "AquesTalk1", Voice = "f1", FlatReading = true }));
            Assert.That(generation.Settings.EngineVersion, Is.EqualTo("1.0.2"));
            Assert.That(generation.Settings.SpeedScale, Is.EqualTo(1), "AquesTalkPlayer has a speed of its own.");
            Assert.That(generation.Settings.PrePhonemeLength, Is.EqualTo(0.2));
            Assert.That(generation.AudioQueryJson, Is.Empty);
            Assert.That(model.AudioQuery.Value, Is.Null);
            Assert.That(model.SpeedScale.Value, Is.EqualTo(1.5), "The hidden slider keeps its value for other engines.");
            Assert.That(editor.History.UndoCount, Is.EqualTo(1));
        });
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(editor.Scene.Children, Is.Empty);
    }

    [AvaloniaTest, Platform(Exclude = "Win")]
    public async Task Speech_at_a_fixed_high_rate_is_resampled_too()
    {
        Register(FakeApp("speak", sampleRate: 44000));
        using var editor = Editor();
        using var model = new TtsTabViewModel(new(), editor);
        model.SelectedEngine.Value = model.Engines.Value.Single(x => x.IsAquesTalkPlayer);
        model.SelectedVoice.Value = model.Voice.Value.First(x => x.Name == "まりさ");
        model.Text.Value = "設定で周波数を固定した音声";
        await model.Generate();
        Assert.That(model.Message.Value, Is.Empty);
        var sound = editor.Scene.Children.Single().Objects.OfType<SourceSound>().Single();
        Assert.That(((AudioEffectGroup)sound.Effect.Value!).Children.Select(x => (x as ResamplingEffect)?.Method.Value),
            Is.EqualTo(new ResamplingMethod?[] { ResamplingMethod.Lagrange }));
    }

    [AvaloniaTest]
    public void Preview_converts_aquestalkplayer_speech_to_the_projects_rate_with_lagrange()
    {
        using var editor = Editor();
        using var model = new TtsTabViewModel(new(), editor);
        byte[] wave = Wave(8000, 0, 400, 0);
        var aquesTalk = new VoiceSynthesisResult(wave, null, "", Voice("れいむ"));
        var http = new VoiceSynthesisResult(wave, null, "", new VoiceSettings { EngineId = "http-engine" });

        Assert.That(model.PreviewWave(http), Is.SameAs(wave), "Other engines play as they speak.");
        byte[] preview = model.PreviewWave(aquesTalk);
        Assert.That(VoiceWave.TryReadFormat(preview, out var format), Is.True);
        // A scene outside a project plays at Beutl's default rate.
        Assert.That(format.SampleRate, Is.EqualTo(44100));
        var input = Samples(wave);
        var output = Samples(preview);
        Assert.Multiple(() =>
        {
            Assert.That(output, Has.Length.EqualTo((int)Math.Ceiling(400 * 44100 / 8000.0)));
            // At a sample of the input the cubic passes through it.
            Assert.That(output[0], Is.EqualTo(input[0]));
            Assert.That(output[441], Is.EqualTo(input[80]));
        });
        // Halfway between samples the four neighbors weigh -1/16, 9/16, 9/16 and -1/16; before and after is silence.
        var doubled = Samples(VoiceWave.ResampleLagrange(Wave(8000, 0, 4, 0), 16000));
        Assert.That(doubled, Is.EqualTo(new short[] { 1000, -62, -1000, 0, 1000, 62, -1000, -625 }));
    }

    [Test, Platform(Exclude = "Win")]
    public async Task Tuned_values_speak_through_a_temporary_preset_and_leave_the_file_as_it_was()
    {
        WritePresets(Preset("れいむ", "AquesTalk1", "f1", flat: true), Preset("自作の声", "AquesTalk10", "M1E"));
        byte[] before = File.ReadAllBytes(PresetPath);
        Register(FakeApp("speak"));
        var tuned = new AquesTalkPlayerVoice { Engine = "AquesTalk1", Voice = "f1", FlatReading = false, Speed = 150, FrequencyScale = 120 };

        var output = await VoiceSynthesisService.SynthesizeAsync("テスト", Voice("れいむ") with { AquesTalkPlayerVoice = tuned }, null);

        Assert.That(ReadArguments()[..2], Is.EqualTo(new[] { "-P", AquesTalkPlayerTemporaryPreset.Name }));
        using (var seen = JsonDocument.Parse(File.ReadAllBytes(SeenPresetPath)))
        {
            var presets = seen.RootElement.EnumerateArray().ToArray();
            Assert.That(presets.Select(x => x.GetProperty("name").GetString()), Is.EqualTo(new[] { "れいむ", "自作の声", AquesTalkPlayerTemporaryPreset.Name }));
            var written = presets[^1];
            Assert.Multiple(() =>
            {
                Assert.That(written.GetProperty("engine").GetString(), Is.EqualTo("AquesTalk1"));
                Assert.That(written.GetProperty("voice").GetString(), Is.EqualTo("f1"));
                Assert.That(written.GetProperty("boyomi").GetBoolean(), Is.False);
                Assert.That(written.GetProperty("spd").GetInt32(), Is.EqualTo(150));
                Assert.That(written.GetProperty("fsc").GetInt32(), Is.EqualTo(120));
                Assert.That(written.GetProperty("vol").GetInt32(), Is.EqualTo(100));
            });
        }
        Assert.That(File.ReadAllBytes(PresetPath), Is.EqualTo(before), "The preset file is put back byte for byte.");
        Assert.That(output.Settings.AquesTalkPlayerVoice, Is.EqualTo(tuned));
        Assert.That(AquesTalkPlayerEngine.Discover(Path.Combine(_directory, "AquesTalkPlayer.app")).Voices.Select(x => x.Name),
            Does.Not.Contain(AquesTalkPlayerTemporaryPreset.Name));

        // Values equal to the preset's speak through the preset without writing.
        File.Delete(SeenPresetPath);
        var preset = new AquesTalkPlayerVoice { Engine = "AquesTalk1", Voice = "f1", FlatReading = true };
        await VoiceSynthesisService.SynthesizeAsync("テスト", Voice("れいむ") with { AquesTalkPlayerVoice = preset }, null);
        Assert.That(ReadArguments()[..2], Is.EqualTo(new[] { "-P", "れいむ" }));
        Assert.That(File.ReadAllBytes(SeenPresetPath), Is.EqualTo(before));
    }

    [Test, Platform(Exclude = "Win")]
    public async Task Without_a_saved_preset_file_the_shipped_presets_are_written_with_it_and_the_file_removed_again()
    {
        Register(FakeApp("speak"));
        var tuned = new AquesTalkPlayerVoice { Engine = "AquesTalk10", Voice = "F2E", Speed = 80, Height = 60, Accent = 150, Quality = 90 };

        await VoiceSynthesisService.SynthesizeAsync("テスト", Voice("デフォルト") with { AquesTalkPlayerVoice = tuned }, null);

        using (var seen = JsonDocument.Parse(File.ReadAllBytes(SeenPresetPath)))
        {
            var names = seen.RootElement.EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray();
            Assert.That(names, Is.EqualTo(AquesTalkPlayerPresets.Defaults.Select(x => x.Name).Append(AquesTalkPlayerTemporaryPreset.Name)),
                "The app keeps its shipped presets while the file exists.");
            var written = seen.RootElement[names.Length - 1];
            Assert.That(written.GetProperty("pit").GetInt32(), Is.EqualTo(60));
            Assert.That(written.GetProperty("acc").GetInt32(), Is.EqualTo(150));
            Assert.That(written.GetProperty("lmd").GetInt32(), Is.EqualTo(90));
        }
        Assert.That(File.Exists(PresetPath), Is.False);
    }

    [Test, Platform(Exclude = "Win")]
    public void A_temporary_preset_left_behind_is_removed_and_a_failure_still_restores_the_file()
    {
        WritePresets(Preset("れいむ", "AquesTalk1", "f1", flat: true), Preset(AquesTalkPlayerTemporaryPreset.Name, "AquesTalk1", "f2"));
        Register(FakeApp("missing preset"));
        var tuned = new AquesTalkPlayerVoice { Engine = "AquesTalk1", Voice = "f1", Speed = 200 };

        Assert.CatchAsync<InvalidOperationException>(() =>
            VoiceSynthesisService.SynthesizeAsync("テスト", Voice("れいむ") with { AquesTalkPlayerVoice = tuned }, null));

        using var saved = JsonDocument.Parse(File.ReadAllBytes(PresetPath));
        Assert.That(saved.RootElement.EnumerateArray().Select(x => x.GetProperty("name").GetString()), Is.EqualTo(new[] { "れいむ" }));
    }

    [TestCase("0", "1")]
    [TestCase("false", "true")]
    [TestCase("False", "True")]
    public void Windows_presets_get_a_row_in_the_apps_own_form_and_lose_it_again(string off, string on)
    {
        string path = Path.Combine(_directory, "AquesTalkPlayer.preset");
        string csv = "名前,棒読み,エンジン,声種,話速,音量,高さ,アクセント,声質,音程,メモ\r\n"
            + $"デフォルト,{off},AquesTalk10,F1E,100,100,100,100,100,100,AquesTalk10 女声\r\n"
            + $"れいむ,{on},AquesTalk1,f1,100,100,100,100,100,100,ゆっくり霊夢\r\n";
        byte[] original = AquesTalkPlayerPresets.ShiftJis.GetBytes(csv);
        var voice = new AquesTalkPlayerVoice { Engine = "AquesTalk2", Voice = "aq_yukkuri", FlatReading = true, Speed = 120, Volume = 90, FrequencyScale = 110 };

        byte[] added = AquesTalkPlayerTemporaryPreset.Add(original, voice, path);

        Assert.That(AquesTalkPlayerPresets.ShiftJis.GetString(added),
            Is.EqualTo(csv + $"{AquesTalkPlayerTemporaryPreset.Name},{on},AquesTalk2,aq_yukkuri,120,90,100,100,100,110,\r\n"));
        Assert.That(AquesTalkPlayerPresets.ParseCsv(added).Last().Voice, Is.EqualTo(voice));
        Assert.That(AquesTalkPlayerTemporaryPreset.Remove(added, path), Is.EqualTo(original));
        Assert.That(AquesTalkPlayerTemporaryPreset.Remove(original, path), Is.Null, "A file without the preset is left as it is.");
        Assert.That(() => AquesTalkPlayerTemporaryPreset.Add(null, voice, path), Throws.InstanceOf<FileNotFoundException>());
    }

    [AvaloniaTest, Platform(Exclude = "Win")]
    public async Task Regenerating_as_aquestalkplayer_speech_adds_resampling_unless_it_is_there()
    {
        WritePresets(Preset("れいむ", "AquesTalk1", "f1", flat: true));
        Register(FakeApp("speak"));
        using var editor = Editor();
        string original = Path.Combine(_directory, "original.wav");
        File.WriteAllBytes(original, Wave(24000, 2400, 2400, 2400));
        var http = new VoiceSettings { EngineId = "http-engine", SpeakerUuid = "speaker", StyleId = 1 };
        var element = VoiceElementFactory.Create(editor.Scene, original, new VoiceGeneration { Text = "元の本文", Settings = http }, MediaTime.Zero, 1);
        editor.Scene.AddChild(element);
        using var observer = new CoreObjectOperationObserver(null, editor.Scene, editor.Sequence);
        using var subscription = editor.History.Subscribe(observer);
        var controller = element.Objects.OfType<TtsController>().Single();
        var effects = (AudioEffectGroup)element.Objects.OfType<SourceSound>().Single().Effect.Value!;
        Assert.That(effects.Children, Is.Empty, "VOICEVOX-compatible speech keeps the usual conversion.");
        var request = new VoiceGeneration { Text = "新しい本文", Settings = Voice("れいむ") };

        await VoiceRegenerationService.RegenerateAsync(controller, request, null, () => (editor.Scene, editor.History));

        Assert.That(effects.Children.Single(), Is.TypeOf<ResamplingEffect>());
        Assert.That(controller.Generation.Value!.Settings.IsAquesTalkPlayer, Is.True);
        Assert.That(editor.History.Undo(), Is.True);
        Assert.That(effects.Children, Is.Empty);
        Assert.That(controller.Generation.Value!.Settings.EngineId, Is.EqualTo("http-engine"));
        Assert.That(editor.History.Redo(), Is.True);
        var resampling = (ResamplingEffect)effects.Children.Single();

        // A disabled resampling stays as the user left it, and is not added twice.
        resampling.IsEnabled.Value = false;
        await VoiceRegenerationService.RegenerateAsync(controller, request with { Text = "もう一度" }, null, () => (editor.Scene, editor.History));
        Assert.That(effects.Children, Is.EqualTo(new[] { resampling }));
        Assert.That(resampling.IsEnabled.Value, Is.False);
        Assert.That(controller.Text.Value, Is.EqualTo("もう一度"));
    }

    [Test, Platform(Exclude = "Win")]
    [TestCase("missing preset", "プリセット「れいむ」が見つかりません")]
    [TestCase("silent", "音声を出力しませんでした")]
    public void Reports_what_the_app_could_not_do(string behavior, string message)
    {
        Register(FakeApp(behavior));
        var error = Assert.CatchAsync<InvalidOperationException>(() => VoiceSynthesisService.SynthesizeAsync("テスト", Voice("れいむ"), null))!;
        Assert.That(error.Message, Does.Contain(message));
    }

    [Test]
    public void Speaking_needs_a_registered_app()
    {
        var error = Assert.CatchAsync<InvalidOperationException>(() => VoiceSynthesisService.SynthesizeAsync("テスト", Voice("れいむ"), null))!;
        Assert.That(error.Message, Does.Contain("登録されていません"));
    }

    [Test, Platform(Exclude = "Win")]
    public async Task Cancelling_or_timing_out_stops_the_app()
    {
        Register(FakeApp("hang"));
        var watch = Stopwatch.StartNew();
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
            Assert.CatchAsync<OperationCanceledException>(() => VoiceSynthesisService.SynthesizeAsync("テスト", Voice("れいむ"), null, cancellation.Token));
        await AssertStopped();
        AquesTalkPlayerEngine.Timeout = TimeSpan.FromMilliseconds(500);
        Assert.CatchAsync<TimeoutException>(() => VoiceSynthesisService.SynthesizeAsync("テスト", Voice("れいむ"), null));
        await AssertStopped();
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(15)));

        async Task AssertStopped()
        {
            int id = int.Parse(File.ReadAllText(ProcessIdPath).Trim());
            File.Delete(ProcessIdPath);
            for (int i = 0; i < 50 && Running(id); i++) await Task.Delay(50);
            Assert.That(Running(id), Is.False);
        }

        static bool Running(int id)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
        }
    }

    [AvaloniaTest]
    public async Task Settings_page_registers_the_app_and_keeps_where_the_user_put_it()
    {
        WritePresets(Preset("れいむ", "AquesTalk1", "f1", flat: true), Preset("まりさ", "AquesTalk1", "f2", flat: true));
        string bundle = FakeApp("speak");
        string? found = null;
        var engine = LocalVoiceEngines.Known.Single(x => x.IsAquesTalkPlayer);
        using (var model = new LocalEngineSetupViewModel([engine], _ => found))
        {
            await model.Refresh();
            var entry = model.Engines.Single();
            Assert.That(entry.CanUse.Value, Is.False);
            Assert.That(entry.Status.Value, Does.Contain("アプリを選択"));
            found = bundle;
            await model.Refresh();
            Assert.That(entry.CanUse.Value, Is.True);
            Assert.That(entry.Status.Value, Does.Contain("1.0.2（プリセット 2 件）"));
            Assert.That(entry.ActionText.Value, Is.EqualTo("追加"));
            Assert.That(await model.Use(entry), Is.True, model.Message.Value);
            Assert.That(model.SelectedConnection!.IsAquesTalkPlayer, Is.True);
        }
        var stored = VoiceEngineStore.Load().Single();
        Assert.That(stored.Id, Is.EqualTo(VoiceSettings.AquesTalkPlayerEngineId));
        Assert.That(stored.ApplicationPath, Is.EqualTo(bundle));
        Assert.That(stored.Voices.Select(x => x.Name), Is.EqualTo(new[] { "れいむ", "まりさ" }));

        found = Path.Combine(_directory, "another", "AquesTalkPlayer.app");
        using (var model = new LocalEngineSetupViewModel([engine], _ => found))
        {
            await model.Refresh();
            var entry = model.Engines.Single();
            Assert.That(entry.ApplicationPath.Value, Is.EqualTo(bundle), "The registered app is kept over another copy.");
            Assert.That(entry.ActionText.Value, Is.EqualTo("更新"));
            WritePresets(Preset("れいむ", "AquesTalk1", "f1", flat: true));
            Assert.That(await model.Use(entry), Is.True, model.Message.Value);
        }
        Assert.That(VoiceEngineStore.Load().Single().Voices.Select(x => x.Name), Is.EqualTo(new[] { "れいむ" }),
            "Updating replaces the one registration with the presets read again.");
    }

    [AvaloniaTest]
    public async Task Reloading_the_engine_reads_presets_added_in_the_app()
    {
        WritePresets(Preset("れいむ", "AquesTalk1", "f1", flat: true));
        Register(FakeApp("speak"));
        using var editor = Editor();
        using var model = new TtsTabViewModel(new(), editor);
        model.SelectedEngine.Value = model.Engines.Value.Single(x => x.IsAquesTalkPlayer);
        model.SelectedVoice.Value = model.Voice.Value.Single();
        WritePresets(Preset("れいむ", "AquesTalk1", "f1", flat: true), Preset("追加した声", "AquesTalk10", "F2E"));

        await model.ReloadEngine();
        await Settle();

        Assert.That(model.Message.Value, Does.Contain("プリセットを読み込みました（2 件）"));
        Assert.That(model.Voice.Value.Select(x => x.Name), Is.EqualTo(new[] { "れいむ", "追加した声" }));
        Assert.That(model.SelectedVoice.Value!.Name, Is.EqualTo("れいむ"), "The selection is kept.");
    }

    [AvaloniaTest, Platform(Exclude = "Win")]
    public async Task Presets_registered_without_their_values_are_read_again_so_tuning_reaches_the_app()
    {
        WritePresets(Preset("れいむ", "AquesTalk1", "f1", flat: true));
        var registered = Register(FakeApp("speak"));
        // What an earlier version kept: the presets without their values.
        VoiceEngineStore.UpdateMetadata(registered with
        {
            Voices = [.. registered.Voices.Select(x => new VoiceMetadata { Name = x.Name, Version = x.Version, SpeakerUuid = x.SpeakerUuid, Styles = x.Styles })]
        });
        using var editor = Editor();
        using var model = new TtsTabViewModel(new(), editor);
        model.SelectedEngine.Value = model.Engines.Value.Single(x => x.IsAquesTalkPlayer);
        await Settle();
        model.SelectedVoice.Value = model.Voice.Value.Single();
        Assert.That(model.AquesTalkFlatReading.Value, Is.True, "The preset's values are shown once read again.");
        model.AquesTalkSpeed.Value = 200;
        model.Text.Value = "速く話します";

        await model.Generate();

        Assert.That(model.Message.Value, Is.Empty);
        Assert.That(ReadArguments()[..2], Is.EqualTo(new[] { "-P", AquesTalkPlayerTemporaryPreset.Name }));
        using var seen = JsonDocument.Parse(File.ReadAllBytes(SeenPresetPath));
        var written = seen.RootElement.EnumerateArray().Last();
        Assert.Multiple(() =>
        {
            Assert.That(written.GetProperty("spd").GetInt32(), Is.EqualTo(200));
            Assert.That(written.GetProperty("boyomi").GetBoolean(), Is.True);
            Assert.That(VoiceEngineStore.Load().Single().Voices.Single().AquesTalkPlayer, Is.Not.Null, "The values are kept for next time.");
        });
    }

    [AvaloniaTest]
    public void Dictionary_page_leaves_the_apps_dictionary_to_the_app()
    {
        var connection = Register(FakeApp("speak"));
        using var dictionary = new VoiceDictionaryViewModel();
        Assert.That(dictionary.Engines.Value.Select(x => x.Id), Does.Not.Contain(VoiceSettings.AquesTalkPlayerEngineId));
        var error = Assert.CatchAsync<NotSupportedException>(() => VoiceDictionaryService.LoadAsync(connection))!;
        Assert.That(error.Message, Does.Contain("AquesTalkPlayer"));
    }

    [AvaloniaTest]
    public async Task Mcp_lists_the_app_and_its_presets_and_leaves_analysis_to_voicevox_core()
    {
        WritePresets(Preset("れいむ", "AquesTalk1", "f1", flat: true));
        string bundle = FakeApp("speak");
        Register(bundle);
        var extension = new VoiceMcpToolExtension();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        async Task<McpToolResult> Invoke(string name, object arguments) => await extension.InvokeAsync(new McpToolCall(name,
            JsonSerializer.SerializeToElement(arguments, options), null, new Services()), CancellationToken.None);

        var engines = (await Invoke("voice_list_engines", new { })).StructuredContent!.Value.GetProperty("engines");
        var listed = engines.EnumerateArray().Single(x => x.GetProperty("engineId").GetString() == VoiceSettings.AquesTalkPlayerEngineId);
        Assert.Multiple(() =>
        {
            Assert.That(listed.GetProperty("kind").GetString(), Is.EqualTo("aquestalkplayer"));
            Assert.That(listed.GetProperty("applicationPath").GetString(), Is.EqualTo(bundle));
            Assert.That(listed.GetProperty("url").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(listed.GetProperty("capabilities").GetProperty("adjustSpeed").GetBoolean(), Is.False);
        });
        var voices = (await Invoke("voice_list_voices", new { engineId = VoiceSettings.AquesTalkPlayerEngineId })).StructuredContent!.Value;
        var voice = voices.GetProperty("voices").EnumerateArray().Single();
        Assert.That(voice.GetProperty("speakerUuid").GetString(), Is.EqualTo("れいむ"));
        Assert.That(voice.GetProperty("styles")[0].GetProperty("styleId").GetInt64(), Is.Zero);
        Assert.That(voice.GetProperty("aquesTalkPlayerVoice").GetProperty("flatReading").GetBoolean(), Is.True);
        Assert.That(voice.GetProperty("aquesTalkPlayerVoice").GetProperty("speed").GetInt32(), Is.EqualTo(100));
        var analysis = await Invoke("voice_analyze", new
        {
            text = "テスト", voice = new { engineId = VoiceSettings.AquesTalkPlayerEngineId, speakerUuid = "れいむ", styleId = 0 }
        });
        // The tests run without VOICEVOX Core, which analyzes AquesTalkPlayer's text.
        Assert.That(analysis.IsError, Is.True);
        Assert.That(string.Join("\n", analysis.Content.OfType<McpTextContent>().Select(x => x.Text)), Does.Contain("VOICEVOX Core"));
    }

    [AvaloniaTest]
    [TestCase(320)]
    [TestCase(460)]
    public async Task Speech_and_settings_pages_render_the_app_and_its_presets(int width)
    {
        WritePresets(Preset("れいむ", "AquesTalk1", "f1", flat: true), Preset("自作の声", "AquesTalk10", "F2E", speed: 120));
        string bundle = FakeApp("speak");
        Register(bundle);
        using var editor = Editor();
        using var speech = new TtsTabViewModel(new(), editor);
        speech.SelectedEngine.Value = speech.Engines.Value.Single(x => x.IsAquesTalkPlayer);
        speech.SelectedVoice.Value = speech.Voice.Value.First();
        speech.Text.Value = "ゆっくりしていってね";
        var view = new TtsTabView { DataContext = speech };
        var window = new Window { Width = width, Height = 900, Content = view, RequestedThemeVariant = ThemeVariant.Dark };
        using var setup = new LocalEngineSetupViewModel([LocalVoiceEngines.Known.Single(x => x.IsAquesTalkPlayer)], _ => bundle);
        try
        {
            window.Show();
            var editorView = view.FindControl<VoiceEditorView>("VoiceEditor")!;
            editorView.FindControl<Expander>("TuningExpander")!.IsExpanded = true;
            await Settle();
            string?[] VisibleSliders() => editorView.GetVisualDescendants().OfType<Slider>().Where(x => x.IsEffectivelyVisible)
                .Select(x => Avalonia.Automation.AutomationProperties.GetName(x)).ToArray();
            var flat = editorView.GetVisualDescendants().OfType<CheckBox>().Single(x => x.IsEffectivelyVisible);
            Assert.Multiple(() =>
            {
                Assert.That(VisibleSliders(), Is.EqualTo(new[] { "話速", "音量", "音程", "前無音（秒）", "後無音（秒）" }),
                    "AquesTalk1 has no height, accent or voice quality.");
                Assert.That(flat.Content, Is.EqualTo("棒読み"));
                Assert.That(flat.IsChecked, Is.True);
                Assert.That(editorView.FindControl<Expander>("AccentExpander")!.IsVisible, Is.True);
            });
            Capture(window, $"aquestalkplayer-speech-{width}");

            // Analyzed accents without the pitches, which AquesTalkPlayer does not take.
            editorView.FindControl<Expander>("TuningExpander")!.IsExpanded = false;
            var accent = editorView.FindControl<Expander>("AccentExpander")!;
            accent.IsExpanded = true;
            speech.AudioQuery.Value = new AudioQueryModel
            {
                AccentPhrases = [Phrase("ユ:y:u ッ:-:cl ク:k:u リ:r:i", 3), Phrase("シ:sh:I テ:t:e", 2), Phrase("イ:-:i ッ:-:cl テ:t:e ネ:n:e", 4)]
            };
            await Settle();
            var panel = editorView.FindControl<AccentPhrasePanel>("AccentPanel")!;
            Assert.That(panel.ShowPitch, Is.False);
            Assert.That(panel.Bounds.Height, Is.LessThan(100));
            bool FlatNotice() => accent.GetVisualDescendants().OfType<TextBlock>()
                .Any(x => x.IsEffectivelyVisible && x.Text == "棒読みでは、アクセントは読みに反映されません。");
            Assert.That(FlatNotice(), Is.True, "れいむ reads flat.");
            Capture(window, $"aquestalkplayer-accent-{width}");
            speech.AquesTalkFlatReading.Value = false;
            await Settle();
            Assert.That(FlatNotice(), Is.False);
            speech.AquesTalkFlatReading.Value = true;
            accent.IsExpanded = false;
            speech.AudioQuery.Value = null;
            editorView.FindControl<Expander>("TuningExpander")!.IsExpanded = true;

            editorView.FindControl<ComboBox>("VoiceSelector")!.SelectedItem = speech.Voice.Value.Single(x => x.Name == "自作の声");
            await Settle();
            Assert.That(speech.SelectedVoice.Value?.Name, Is.EqualTo("自作の声"));
            Assert.That(speech.SelectedStyle.Value?.Name, Is.EqualTo("AquesTalk10 F2E"));
            Assert.That(editorView.FindControl<ComboBox>("StyleSelector")!.SelectedItem, Is.SameAs(speech.SelectedStyle.Value));
            Assert.That(VisibleSliders(), Is.EqualTo(new[] { "話速", "音量", "高さ", "アクセント", "声質", "音程", "前無音（秒）", "後無音（秒）" }));
            Assert.That(speech.AquesTalkSpeed.Value, Is.EqualTo(120), "The preset's values are loaded.");
            Assert.That(flat.IsChecked, Is.False);
            Capture(window, $"aquestalkplayer-speech-aquestalk10-{width}");

            speech.SelectedPage.Value = 3;
            var content = view.FindControl<LocalEngineSetupView>("EngineSetup")!;
            (content.DataContext as LocalEngineSetupViewModel)?.Dispose();
            content.DataContext = setup;
            await setup.Refresh();
            await Settle();
            var card = setup.Engines.Single();
            Assert.That(card.Status.Value, Does.Contain("プリセット 2 件"));
            Capture(window, $"aquestalkplayer-settings-{width}");
            var use = content.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "UseEngineButton");
            Assert.That(use.Content, Is.EqualTo("更新"));
            use.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (speech.SelectedPage.Value != 0 && DateTime.UtcNow < deadline) await Settle();
            Assert.That(speech.SelectedPage.Value, Is.Zero, setup.Message.Value);
            Assert.That(speech.SelectedEngine.Value!.IsAquesTalkPlayer, Is.True);
        }
        finally { window.Close(); }
    }

    // Set VOICEVOX_TEST_HOME to an installed VOICEVOX Core and VOICE_AQUESTALKPLAYER_PATH to the installed app.
    [Test]
    public async Task Installed_app_speaks_accents_analyzed_by_voicevox_core()
    {
        if (Environment.GetEnvironmentVariable("VOICE_AQUESTALKPLAYER_PATH") is not { Length: > 0 } application
            || Environment.GetEnvironmentVariable("VOICEVOX_TEST_HOME") is not { Length: > 0 } coreHome)
        {
            Assert.Ignore("Set VOICEVOX_TEST_HOME and VOICE_AQUESTALKPLAYER_PATH to test with the installed VOICEVOX Core and AquesTalkPlayer.");
            return;
        }
        AquesTalkPlayerEngine.Host = AquesTalkPlayerHost.Current;
        Register(application);
        var previous = TtsLoader.VoiceVoxLoader.Value;
        var loader = new VoiceVoxLoader(coreHome);
        try
        {
            await Task.Run(loader.Load);
            TtsLoader.VoiceVoxLoader.Value = loader;
            // A preset reading flat (棒読み), such as れいむ, speaks every accent alike.
            var settings = Voice("デフォルト");
            var query = await VoiceSynthesisService.CreateQueryAsync("箸を使う", settings);
            Assert.That(AquesTalkPhonetic.FromQuery(query), Is.EqualTo("#>は'しお/つかう。"));
            var asAnalyzed = await VoiceSynthesisService.SynthesizeAsync("箸を使う", settings, query);

            // Moving the accent as the panel does draws it again and makes the app speak differently.
            query.AccentPhrases[0].Accent = 2;
            query.AccentPhrases = await VoiceSynthesisService.ReplaceMoraPitchAsync(query.AccentPhrases, settings);
            Assert.That(AquesTalkPhonetic.FromQuery(query), Is.EqualTo("#>はし'お/つかう。"));
            var moved = await VoiceSynthesisService.SynthesizeAsync("箸を使う", settings, query);
            Assert.That(Samples(moved.Wave), Is.Not.EqualTo(Samples(asAnalyzed.Wave)));
            Assert.That(Samples(asAnalyzed.Wave).Length, Is.GreaterThan(8000 * 0.5));
        }
        finally
        {
            TtsLoader.VoiceVoxLoader.Value = previous;
            loader.Unload();
        }
    }

    // Set VOICE_AQUESTALKPLAYER_PATH to the installed app (the .app bundle, or the folder or executable on Windows).
    [Test]
    public async Task Installed_app_speaks_its_presets()
    {
        if (Environment.GetEnvironmentVariable("VOICE_AQUESTALKPLAYER_PATH") is not { Length: > 0 } application)
        {
            Assert.Ignore("Set VOICE_AQUESTALKPLAYER_PATH to test with the installed AquesTalkPlayer.");
            return;
        }
        AquesTalkPlayerEngine.Host = AquesTalkPlayerHost.Current;
        var connection = Register(application);
        Assert.That(connection.Voices.Select(x => x.Name), Does.Contain("れいむ"));
        string presetPath = AquesTalkPlayerEngine.PresetPath(AquesTalkPlayerEngine.ResolveExecutable(application, AquesTalkPlayerHost.Current), AquesTalkPlayerHost.Current);
        byte[]? before = File.Exists(presetPath) ? File.ReadAllBytes(presetPath) : null;
        const string text = "ゆっくりしていってね。";
        var output = await VoiceSynthesisService.SynthesizeAsync(text, Voice("れいむ") with { PostPhonemeLength = 0.2 }, null);
        Assert.That(VoiceWave.TryReadFormat(output.Wave, out var format), Is.True);
        var samples = Samples(output.Wave);
        Assert.Multiple(() =>
        {
            Assert.That(format.IsPcm16, Is.True);
            Assert.That(samples.Take((int)(format.SampleRate * 0.1)), Is.All.Zero);
            Assert.That(samples.TakeLast((int)(format.SampleRate * 0.2)), Is.All.Zero);
            Assert.That(samples.Length, Is.GreaterThan(format.SampleRate * 0.5));
            Assert.That(output.Settings.StyleName, Is.EqualTo("AquesTalk1 f1"));
            Assert.That(output.Settings.EngineVersion, Is.Not.Empty);
        });

        // Twice the speed through a temporary preset speaks in about half the time, and leaves the app's file as it was.
        var faster = output.Settings.AquesTalkPlayerVoice! with { Speed = 200 };
        var tuned = await VoiceSynthesisService.SynthesizeAsync(text, Voice("れいむ") with { PostPhonemeLength = 0.2, AquesTalkPlayerVoice = faster }, null);
        Assert.That(VoiceWave.TryReadFormat(tuned.Wave, out var tunedFormat), Is.True);
        double Speech(int frames, int rate) => (double)frames / rate - 0.3;
        Assert.That(Speech(Samples(tuned.Wave).Length, tunedFormat.SampleRate), Is.EqualTo(Speech(samples.Length, format.SampleRate) / 2).Within(15).Percent);
        Assert.That(File.Exists(presetPath) ? File.ReadAllBytes(presetPath) : null, Is.EqualTo(before));

        var error = Assert.CatchAsync<InvalidOperationException>(() => VoiceSynthesisService.SynthesizeAsync("テスト", Voice("存在しないプリセット"), null))!;
        Assert.That(error.Message, Does.Contain("見つかりません"));
    }

    private sealed class Services : IEditorContextServices
    {
        public IExtensionProvider ExtensionProvider => throw new NotSupportedException();
        public bool TryGetService<T>([NotNullWhen(true)] out T? service) where T : class
        {
            service = null;
            return false;
        }
    }
}
