using Beutl.Audio.Platforms.OpenAL;
using Beutl.Audio.Platforms.XAudio2;
using Beutl.Language;
using Beutl.Logging;
using Beutl.Services;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Silk.NET.OpenAL;
using AudioContext = Beutl.Audio.Platforms.OpenAL.AudioContext;

namespace Beutl.Extensions.Voice.Services;

public class SimpleWavePlayer : IDisposable
{
    private readonly ILogger _logger = Log.CreateLogger<SimpleWavePlayer>();

    public SimpleWavePlayer(byte[] waveData)
    {
        WaveData = waveData;
        Stream = new MemoryStream(waveData);
        Reader = new WaveFileReader(Stream);
        Resampler = new WdlResamplingSampleProvider(Reader.ToSampleProvider().ToStereo(), Reader.WaveFormat.SampleRate);
    }

    public byte[] WaveData { get; }

    public MemoryStream Stream { get; }

    public WaveFileReader Reader { get; }

    public WdlResamplingSampleProvider Resampler { get; }

    public async Task Play(CancellationToken ct)
    {
        await Task.Run(async () =>
        {
            if (OperatingSystem.IsWindows())
            {
                await PlayWithXA2(ct);
            }
            else
            {
                await PlayWithOpenAL(ct);
            }
        }, ct);
    }

    private async Task PlayWithXA2(CancellationToken ct)
    {
        using var audioContext = new XAudioContext();
        var fmt = new Vortice.Multimedia.WaveFormat(Reader.WaveFormat.SampleRate, 32, 2);
        var source = new XAudioSource(audioContext);
        var primaryBuffer = new XAudioBuffer();
        var secondaryBuffer = new XAudioBuffer();
        long cur = 0;

        void PrepareBuffer(XAudioBuffer buffer)
        {
            // 1秒あたりのバイト数 * 1秒分のデータを読み込む
            var buf = new float[fmt.SampleRate * 2];
            _ = Resampler.Read(buf, 0, buf.Length);
            buffer.BufferData(buf.AsSpan(), fmt);

            source.QueueBuffer(buffer);
        }


        try
        {
            PrepareBuffer(primaryBuffer);

            cur += fmt.SampleRate;
            PrepareBuffer(secondaryBuffer);

            source.Play();

            await Task.Delay(1000, ct).ConfigureAwait(false);

            // primaryBufferが終了、secondaryが開始

            while (cur < Reader.SampleCount)
            {
                if (ct.IsCancellationRequested)
                {
                    source.Stop();
                    break;
                }

                cur += fmt.SampleRate;

                PrepareBuffer(primaryBuffer);

                // バッファを入れ替える
                (primaryBuffer, secondaryBuffer) = (secondaryBuffer, primaryBuffer);

                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            source.Stop();
        }
        catch (Exception ex)
        {
            NotificationService.ShowError(MessageStrings.UnexpectedError,
                MessageStrings.AudioPlaybackException);
            _logger.LogError(ex, "An exception occurred during audio playback.");
        }
        finally
        {
            source.Dispose();
            primaryBuffer.Dispose();
            secondaryBuffer.Dispose();
        }
    }

    private async Task PlayWithOpenAL(CancellationToken ct)
    {
        using var audioContext = new AudioContext();
        uint[] buffers = [];
        uint source = 0;

        try
        {
            audioContext.MakeCurrent();

            long cur = 0;
            buffers = audioContext.GenBuffers(2);
            source = audioContext.GenSource();

            foreach (uint buffer in buffers)
            {
                var buf = new float[Reader.WaveFormat.SampleRate * 2];
                _ = Resampler.Read(buf, 0, buf.Length);
                cur += Reader.WaveFormat.SampleRate;
                var converted = buf.Select(i => (short)(i * short.MaxValue)).ToArray();

                audioContext.BufferData(buffer, BufferFormat.Stereo16, converted.AsSpan(), Reader.WaveFormat.SampleRate);

                audioContext.SourceQueueBuffer(source, buffer);
            }

            audioContext.SourcePlay(source);

            while (!ct.IsCancellationRequested && cur < Reader.SampleCount)
            {
                audioContext.GetSource(source, GetSourceInteger.BuffersProcessed, out int processed);

                while (processed > 0)
                {
                    uint buffer = audioContext.SourceUnqueueBuffer(source);
                    var buf = new float[Reader.WaveFormat.SampleRate * 2];
                    _ = Resampler.Read(buf, 0, buf.Length);
                    cur += Reader.WaveFormat.SampleRate;
                    var converted = buf.Select(i => (short)(i * short.MaxValue)).ToArray();

                    audioContext.BufferData(buffer, BufferFormat.Stereo16, converted.AsSpan(), Reader.WaveFormat.SampleRate);

                    audioContext.SourceQueueBuffer(source, buffer);
                    processed--;
                }

                await Task.Delay(100, ct).ConfigureAwait(false);
            }

            while (audioContext.GetSourceState(source) == SourceState.Playing && !ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            NotificationService.ShowError(MessageStrings.UnexpectedError,
                MessageStrings.AudioPlaybackException);
            _logger.LogError(ex, "An exception occurred during audio playback.");
        }
        finally
        {
            audioContext.SourceStop(source);
            // https://hamken100.blogspot.com/2014/04/aldeletebuffersalinvalidoperation.html
            audioContext.Source(source, SourceInteger.Buffer, 0);
            audioContext.DeleteBuffers(buffers);
            audioContext.DeleteSource(source);
        }
    }

    public void Dispose()
    {
        Reader.Dispose();
        Stream.Dispose();
    }
}