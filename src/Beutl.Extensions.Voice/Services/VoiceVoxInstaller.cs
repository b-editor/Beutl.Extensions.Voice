using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Beutl.Logging;
using Microsoft.Extensions.Logging;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.Services;

public class VoiceVoxInstaller
{
    private readonly ILogger _logger = Log.CreateLogger<VoiceVoxInstaller>();
    private static readonly TimeSpan s_httpHeaderTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_downloadIdleTimeout = TimeSpan.FromMinutes(2);
    
    public ReactiveProperty<double> Progress { get; } = new(0);

    public ReactiveProperty<double> ProgressMax { get; } = new(1);

    public ReactiveProperty<bool> IsIndeterminate { get; } = new();

    public ReactiveProperty<string> Status { get; } = new("準備中");

    public ReactiveProperty<string> Error { get; } = new();

    public ReactiveProperty<bool> IsCompleted { get; } = new();

    public Task Install(CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            string? voicevoxHomePath = null;
            string? tempVoicevoxHomePath = null;
            string? backupVoicevoxHomePath = null;
            try
            {
                var home = BeutlEnvironment.GetHomeDirectoryPath();
                voicevoxHomePath = Path.Combine(home, "voicevox");
                tempVoicevoxHomePath = Path.Combine(home, $".voicevox-{Guid.NewGuid():N}.tmp");

                DeleteDirectoryIfExists(tempVoicevoxHomePath);
                Directory.CreateDirectory(tempVoicevoxHomePath);

                await InstallVoiceVoxCore(tempVoicevoxHomePath, ct);
                await InstallDictionary(tempVoicevoxHomePath, ct);
                await InstallOnnxRuntime(tempVoicevoxHomePath, ct);
                await InstallVoiceModels(tempVoicevoxHomePath, ct);

                if (!VoiceVoxLoader.IsValidInstallation(tempVoicevoxHomePath))
                {
                    throw new InvalidOperationException("VOICEVOXのインストール検証に失敗しました。");
                }

                backupVoicevoxHomePath = Path.Combine(home, $".voicevox-backup-{Guid.NewGuid():N}.tmp");
                DeleteDirectoryIfExists(backupVoicevoxHomePath);

                if (Directory.Exists(voicevoxHomePath))
                {
                    Directory.Move(voicevoxHomePath, backupVoicevoxHomePath);
                }

                try
                {
                    MoveDirectoryCrossDevice(tempVoicevoxHomePath, voicevoxHomePath);
                    tempVoicevoxHomePath = null;
                    DeleteDirectoryIfExists(backupVoicevoxHomePath);
                    backupVoicevoxHomePath = null;
                }
                catch
                {
                    if (Directory.Exists(backupVoicevoxHomePath))
                    {
                        if (Directory.Exists(voicevoxHomePath)
                            && !VoiceVoxLoader.IsValidInstallation(voicevoxHomePath))
                        {
                            DeleteDirectoryIfExists(voicevoxHomePath);
                        }

                        if (!Directory.Exists(voicevoxHomePath))
                        {
                            MoveDirectoryCrossDevice(backupVoicevoxHomePath, voicevoxHomePath);
                            backupVoicevoxHomePath = null;
                        }
                    }

                    throw;
                }

                Status.Value = "ロード中 (8/8)";
                IsIndeterminate.Value = true;
                await TtsLoader.StaticLoad();
                IsIndeterminate.Value = false;
                Status.Value = "完了";
            }
            catch (Exception ex)
            {
                if (tempVoicevoxHomePath != null)
                {
                    DeleteDirectoryIfExists(tempVoicevoxHomePath);
                }

                if (voicevoxHomePath != null
                    && Directory.Exists(voicevoxHomePath)
                    && !VoiceVoxLoader.IsValidInstallation(voicevoxHomePath))
                {
                    DeleteDirectoryIfExists(voicevoxHomePath);
                }

                if (backupVoicevoxHomePath != null
                    && Directory.Exists(backupVoicevoxHomePath)
                    && voicevoxHomePath != null
                    && Directory.Exists(voicevoxHomePath)
                    && VoiceVoxLoader.IsValidInstallation(voicevoxHomePath))
                {
                    DeleteDirectoryIfExists(backupVoicevoxHomePath);
                }

                Error.Value = ex.Message;
                _logger.LogError(ex, "Failed to install voicevox_core");
            }
            finally
            {
                IsCompleted.Value = true;
            }
        }, ct);
    }
    
    private async Task DownloadFile(string url, string path, CancellationToken ct)
    {
        _logger.LogInformation("Downloading {Url} to {Path}", url, path);
        using var client = new HttpClient { Timeout = s_httpHeaderTimeout };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;

        await using var fs = File.OpenWrite(path);
        await using var download = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        
        if (!contentLength.HasValue)
        {
            IsIndeterminate.Value = true;
            await CopyDownloadedStream(download, fs, ct).ConfigureAwait(false);
        }
        else
        {
            long totalBytesRead = 0;
            ProgressMax.Value = 1;
            await CopyDownloadedStream(download, fs, ct, bytesRead =>
            {
                totalBytesRead += bytesRead;
                Progress.Value = totalBytesRead / (double)contentLength.Value;
            }).ConfigureAwait(false);
        }
        
        _logger.LogInformation("Downloaded {Url} to {Path}", url, path);
    }

    private static async Task CopyDownloadedStream(
        Stream source,
        Stream destination,
        CancellationToken ct,
        Action<int>? onBytesRead = null)
    {
        var buffer = new byte[81920];
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        while (true)
        {
            timeoutCts.CancelAfter(s_downloadIdleTimeout);
            int bytesRead;
            try
            {
                bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"ダウンロードが{s_downloadIdleTimeout.TotalSeconds:0}秒以上停止しました。");
            }
            finally
            {
                timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
            }

            if (bytesRead == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            onBytesRead?.Invoke(bytesRead);
        }
    }
    
    private async Task ExtractZip(string zipPath, string dstDir, CancellationToken ct)
    {
        _logger.LogInformation("Extracting {ZipPath} to {DstDir}", zipPath, dstDir);
        using var source = ZipFile.Open(zipPath, ZipArchiveMode.Read);
        var fullDstDir = Path.GetFullPath(dstDir);
        if (!fullDstDir.EndsWith(Path.DirectorySeparatorChar))
        {
            fullDstDir += Path.DirectorySeparatorChar;
        }

        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        ProgressMax.Value = source.Entries.Count;
        foreach (var entry in source.Entries)
        {
            if (entry.Length != 0)
            {
                var entrySegments = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (entrySegments.Length < 2 || entrySegments.Any(x => x == ".."))
                {
                    throw new IOException($"Invalid zip entry path: {entry.FullName}");
                }

                var dst = Path.Combine(dstDir, Path.Combine(entrySegments[1..]));
                var fullDst = Path.GetFullPath(dst);
                if (!fullDst.StartsWith(fullDstDir, pathComparison))
                {
                    throw new IOException($"Zip entry is outside the target directory: {entry.FullName}");
                }

                var directory = Path.GetDirectoryName(fullDst);
                if (string.IsNullOrEmpty(directory))
                {
                    throw new IOException($"Invalid zip entry path: {entry.FullName}");
                }

                Directory.CreateDirectory(directory);
                await using var fs = File.OpenWrite(fullDst);
                await using var es = entry.Open();
                await es.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            Progress.Value++;
        }
        
        _logger.LogInformation("Extracted {ZipPath} to {DstDir}", zipPath, dstDir);
    }
    
    private async Task ExtractTarGz(string tarGzPath, string dstDir, CancellationToken ct)
    {
        _logger.LogInformation("Extracting {TarGzPath} to {DstDir}", tarGzPath, dstDir);

        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            await using var fs = File.OpenRead(tarGzPath);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            IsIndeterminate.Value = true;
            await TarFile.ExtractToDirectoryAsync(gz, tempDir, true, ct);
            IsIndeterminate.Value = false;

            // ルートにエントリが一つだけ（ディレクトリ）なら、その階層を飛ばす
            var rootEntries = Directory.GetFileSystemEntries(tempDir);
            var sourceDir = rootEntries.Length == 1 && Directory.Exists(rootEntries[0])
                ? rootEntries[0]
                : tempDir;

            foreach (var entry in Directory.GetFileSystemEntries(sourceDir))
            {
                var name = Path.GetFileName(entry);
                var dest = Path.Combine(dstDir, name);
                if (Directory.Exists(entry))
                    MoveDirectoryCrossDevice(entry, dest);
                else
                    MoveFileCrossDevice(entry, dest);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }

        _logger.LogInformation("Extracted {TarGzPath} to {DstDir}", tarGzPath, dstDir);
    }

    private async Task InstallVoiceVoxCore(string voicevoxHomePath, CancellationToken ct)
    {
        var voicevoxCorePath = Path.Combine(voicevoxHomePath, "core");
        if (!Directory.Exists(voicevoxCorePath))
        {
            Directory.CreateDirectory(voicevoxCorePath);
        }
        
        _logger.LogInformation("Installing voicevox_core to {VoicevoxCorePath}", voicevoxCorePath);
        var tempPath = Path.GetTempFileName();
        try
        {
            var url = DetermineUrl();
            // ダウンロード
            Status.Value = "VOICEVOXをダウンロード中 (1/8)";
            await DownloadFile(url, tempPath, ct);

            // 解凍
            Status.Value = "解凍中 (2/8)";
            IsIndeterminate.Value = false;
            await ExtractZip(tempPath, voicevoxCorePath, ct);

            Progress.Value = ProgressMax.Value;
            _logger.LogInformation("Installed voicevox_core to {VoicevoxCorePath}", voicevoxCorePath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
    
    private async Task InstallDictionary(string voicevoxHomePath, CancellationToken ct)
    {
        var openJtalkPath = Path.Combine(voicevoxHomePath, "open_jtalk");
        if (!Directory.Exists(openJtalkPath))
        {
            Directory.CreateDirectory(openJtalkPath);
        }

        _logger.LogInformation("Installing dictionary to {OpenJTalkPath}", openJtalkPath);
        var tempPath = Path.GetTempFileName();
        try
        {
            var url = "https://github.com/r9y9/open_jtalk/releases/download/v1.11.1/open_jtalk_dic_utf_8-1.11.tar.gz";
            // ダウンロード
            Status.Value = "辞書をダウンロード中 (3/8)";
            await DownloadFile(url, tempPath, ct);

            // 解凍
            Status.Value = "解凍中 (4/8)";
            IsIndeterminate.Value = false;
            await ExtractTarGz(tempPath, openJtalkPath, ct);

            Progress.Value = ProgressMax.Value;
            _logger.LogInformation("Installed dictionary to {OpenJTalkPath}", openJtalkPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task InstallOnnxRuntime(string voicevoxHomePath, CancellationToken ct)
    {
        var onnxruntimeDir = Path.Combine(voicevoxHomePath, "onnxruntime");
        if (!Directory.Exists(onnxruntimeDir))
        {
            Directory.CreateDirectory(onnxruntimeDir);
        }


        _logger.LogInformation("Installing ONNX Runtime to {Dir}", onnxruntimeDir);
        var tempPath = Path.GetTempFileName();
        try
        {
            var url = DetermineOnnxRuntimeUrl();
            Status.Value = "ONNX Runtimeをダウンロード中 (5/8)";
            await DownloadFile(url, tempPath, ct);

            Status.Value = "解凍中 (6/8)";
            IsIndeterminate.Value = false;
            await ExtractTarGz(tempPath, onnxruntimeDir, ct);

            Progress.Value = ProgressMax.Value;
            _logger.LogInformation("Installed ONNX Runtime to {Dir}", onnxruntimeDir);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task InstallVoiceModels(string voicevoxHomePath, CancellationToken ct)
    {
        var vvmDir = Path.Combine(voicevoxHomePath, "models");
        if (!Directory.Exists(vvmDir))
        {
            Directory.CreateDirectory(vvmDir);
        }

        _logger.LogInformation("Fetching VVM release information from GitHub");
        Status.Value = "音声モデル情報を取得中 (7/8)";
        IsIndeterminate.Value = true;

        using var client = new HttpClient { Timeout = s_httpHeaderTimeout };
        client.DefaultRequestHeaders.Add("User-Agent", "Beutl");

        var releasesJson = await client.GetStringAsync(
            "https://api.github.com/repos/VOICEVOX/voicevox_vvm/releases?per_page=100", ct);

        using var doc = JsonDocument.Parse(releasesJson);

        // 0.16.x (>= 0.16, < 0.17) の最新リリースを検索
        JsonElement? latestRelease = null;
        Version? latestVersion = null;
        var minVersion = new Version(0, 16);
        var maxVersion = new Version(0, 17);

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var tagName = release.GetProperty("tag_name").GetString();
            if (tagName == null || !Version.TryParse(tagName, out var version))
                continue;

            if (version >= minVersion && version < maxVersion)
            {
                if (latestVersion == null || version > latestVersion)
                {
                    latestVersion = version;
                    latestRelease = release.Clone();
                }
            }
        }

        if (latestRelease == null)
        {
            throw new InvalidOperationException("VVM release (0.16.x) が見つかりません");
        }

        _logger.LogInformation("Found VVM release: {Version}", latestVersion);

        // .vvmアセットのURLを収集
        var vvmAssets = new List<(string Name, string Url)>();
        foreach (var asset in latestRelease.Value.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString()!;
            if (name.EndsWith(".vvm", StringComparison.OrdinalIgnoreCase))
            {
                var url = asset.GetProperty("browser_download_url").GetString()!;
                vvmAssets.Add((name, url));
            }
        }

        // ダウンロード
        IsIndeterminate.Value = false;
        ProgressMax.Value = vvmAssets.Count;
        Progress.Value = 0;

        for (var i = 0; i < vvmAssets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (name, url) = vvmAssets[i];
            var filePath = Path.Combine(vvmDir, name);
            Status.Value = $"音声モデルをダウンロード中 (7/8) [{i + 1}/{vvmAssets.Count}]";

            _logger.LogInformation("Downloading VVM: {Name}", name);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var fs = File.OpenWrite(filePath);
            await using var download = await response.Content.ReadAsStreamAsync(ct);
            await CopyDownloadedStream(download, fs, ct);

            Progress.Value = i + 1;
        }

        _logger.LogInformation("Installed {Count} VVM files to {Dir}", vvmAssets.Count, vvmDir);
    }

    private void DeleteDirectoryIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete directory: {Path}", path);
        }
    }

    private static void MoveDirectoryCrossDevice(string source, string dest)
    {
        try
        {
            Directory.Move(source, dest);
        }
        catch (IOException)
        {
            CopyDirectory(source, dest);
            Directory.Delete(source, true);
        }
    }

    private static void MoveFileCrossDevice(string source, string dest)
    {
        try
        {
            File.Move(source, dest);
        }
        catch (IOException)
        {
            File.Copy(source, dest, true);
            File.Delete(source);
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }

    private static string DetermineUrl()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "osx"
            : throw new PlatformNotSupportedException();
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x64"
            : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64"
            : throw new PlatformNotSupportedException();

        return $"https://github.com/VOICEVOX/voicevox_core/releases/download/0.16.3/voicevox_core-{os}-{arch}-0.16.3.zip";
    }

    private static string DetermineOnnxRuntimeUrl()
    {
        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "osx"
            : throw new PlatformNotSupportedException();

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => OperatingSystem.IsMacOS() ? "x86_64" : "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException()
        };

        return $"https://github.com/VOICEVOX/onnxruntime-builder/releases/download/voicevox_onnxruntime-1.17.3/voicevox_onnxruntime-{os}-{arch}-1.17.3.tgz";
    }
}
