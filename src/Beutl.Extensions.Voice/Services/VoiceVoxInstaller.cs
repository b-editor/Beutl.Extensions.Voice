using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Beutl.Logging;
using Microsoft.Extensions.Logging;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.Services;

public class VoiceVoxInstaller
{
    private readonly ILogger _logger = Log.CreateLogger<VoiceVoxInstaller>();
    
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
            try
            {
                var home = BeutlEnvironment.GetHomeDirectoryPath();
                var voicevoxCorePath = Path.Combine(home, "voicevox_core");
                if (Directory.Exists(voicevoxCorePath))
                {
                    Directory.Delete(voicevoxCorePath, true);
                }

                Directory.CreateDirectory(voicevoxCorePath);
                await InstallVoiceVoxCore(voicevoxCorePath, ct);
                await InstallDictionary(voicevoxCorePath, ct);
                Status.Value = "ロード中 (5/5)";
                await TtsLoader.StaticLoad();
                Status.Value = "完了";
            }
            catch (Exception ex)
            {
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
        using var client = new HttpClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;

        await using var fs = File.OpenWrite(path);
        await using var download = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        
        if (!contentLength.HasValue)
        {
            IsIndeterminate.Value = true;
            await download.CopyToAsync(fs, ct).ConfigureAwait(false);
        }
        else
        {
            var bufferSize = 81920;
            var buffer = new byte[bufferSize];
            long totalBytesRead = 0;
            int bytesRead;
            ProgressMax.Value = 1;
            while ((bytesRead = await download.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                totalBytesRead += bytesRead;

                Progress.Value = totalBytesRead / (double)contentLength.Value;
            }
        }
        
        _logger.LogInformation("Downloaded {Url} to {Path}", url, path);
    }
    
    private async Task ExtractZip(string zipPath, string dstDir, CancellationToken ct)
    {
        _logger.LogInformation("Extracting {ZipPath} to {DstDir}", zipPath, dstDir);
        using var source = ZipFile.Open(zipPath, ZipArchiveMode.Read);
        ProgressMax.Value = source.Entries.Count;
        foreach (var entry in source.Entries)
        {
            if (entry.Length != 0)
            {
                var dst = Path.Combine(dstDir,
                    string.Join(Path.DirectorySeparatorChar, entry.FullName.Split('/')[1..]));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                await using var fs = File.OpenWrite(dst);
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
        await using var fs = File.OpenRead(tarGzPath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        IsIndeterminate.Value = true;
        await TarFile.ExtractToDirectoryAsync(gz, dstDir, true, ct);
        IsIndeterminate.Value = false;
        _logger.LogInformation("Extracted {TarGzPath} to {DstDir}", tarGzPath, dstDir);
    }

    private async Task InstallVoiceVoxCore(string voicevoxCorePath, CancellationToken ct)
    {
        _logger.LogInformation("Installing voicevox_core to {VoicevoxCorePath}", voicevoxCorePath);
        var tempPath = Path.GetTempFileName();
        try
        {
            var url = DetermineUrl();
            // ダウンロード
            Status.Value = "VOICEVOXをダウンロード中 (1/5)";
            await DownloadFile(url, tempPath, ct);

            // 解凍
            Status.Value = "解凍中 (2/5)";
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
    
    private async Task InstallDictionary(string voicevoxCorePath, CancellationToken ct)
    {
        _logger.LogInformation("Installing dictionary to {VoicevoxCorePath}", voicevoxCorePath);
        var tempPath = Path.GetTempFileName();
        try
        {
            var url = "https://jaist.dl.sourceforge.net/project/open-jtalk/Dictionary/open_jtalk_dic-1.11/open_jtalk_dic_utf_8-1.11.tar.gz";
            // ダウンロード
            Status.Value = "辞書をダウンロード中 (3/5)";
            await DownloadFile(url, tempPath, ct);

            // 解凍
            Status.Value = "解凍中 (4/5)";
            IsIndeterminate.Value = false;
            await ExtractTarGz(tempPath, voicevoxCorePath, ct);

            Progress.Value = ProgressMax.Value;
            _logger.LogInformation("Installed dictionary to {VoicevoxCorePath}", voicevoxCorePath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
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

        return
            $"https://github.com/VOICEVOX/voicevox_core/releases/download/0.15.0-preview.8/voicevox_core-{os}-{arch}-cpu-0.15.0-preview.8.zip";
    }
}