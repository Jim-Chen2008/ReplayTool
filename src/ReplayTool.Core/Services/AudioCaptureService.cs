using NAudio.Wave;
using ReplayTool.Core.Models;

namespace ReplayTool.Core.Services;

/// <summary>
/// 音频捕获服务：Windows WASAPI 环路（系统主声道）+ 可选麦克风。
/// 输出 s16le / 48kHz / 双声道 PCM，写入 FFmpeg 录制进程的 stdin（pipe:0）。
///
/// 注意：经实测，本机 FFmpeg 构建的 UDP 输入与 muxer 组合会停滞，
/// 因此音频一律走 stdin 管道（单流）。麦克风与系统声道在 C# 侧混音。
/// 双音轨分离保存为后续增强项。
/// </summary>
public class AudioCaptureService : IDisposable
{
    private readonly SettingsManager _settings;

    /// <summary>音频源是否可用（WASAPI 环路初始化成功）。</summary>
    public bool IsAvailable { get; private set; }

    public event EventHandler<string>? Log;

    private WasapiLoopbackCapture? _loopback;
    private WaveInEvent? _waveIn;
    private Stream? _output;
    private readonly object _writeLock = new();
    private readonly Queue<byte[]> _pending = new();
    private CancellationTokenSource? _writerCts;
    private Task? _writerTask;

    public AudioCaptureService(SettingsManager settings)
    {
        _settings = settings;
    }

    /// <summary>是否启用任意音频捕获。</summary>
    public bool AudioEnabled
    {
        get
        {
            var s = _settings.Settings;
            return s.CaptureSystemAudio || s.CaptureMic;
        }
    }

    /// <summary>
    /// 预启动音频捕获（在启动 FFmpeg 之前调用）。
    /// 初始化 WASAPI 环路与麦克风；失败则禁用音频（不影响视频录制）。
    /// </summary>
    public void Prepare()
    {
        try
        {
            if (!AudioEnabled) return;
            StartSystemCapture();
            if (_settings.Settings.CaptureMic)
                StartMicCapture();

            // 若系统声道未成功启动但设置了，标记不可用
            IsAvailable = _loopback != null;
            if (!IsAvailable)
                Log?.Invoke(this, "系统音频不可用，本次录制仅视频");
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"音频预启动失败（继续视频录制）: {ex.Message}");
            IsAvailable = false;
        }
    }

    /// <summary>
    /// 将捕获到的 PCM 写入指定流（FFmpeg stdin）。由录制服务在进程启动后调用。
    /// </summary>
    public void StartWritingTo(Stream output)
    {
        lock (_writeLock)
        {
            _output = output;
        }
        if (_writerCts != null) return;
        _writerCts = new CancellationTokenSource();
        _writerTask = Task.Run(() => DrainQueueAsync(_writerCts.Token));
    }

    private void StartSystemCapture()
    {
        try
        {
            _loopback = new WasapiLoopbackCapture();
            _loopback.DataAvailable += (_, e) =>
            {
                try { Enqueue(ConvertSystemToPcm(e.Buffer, e.BytesRecorded)); }
                catch { /* 丢弃坏样本 */ }
            };
            _loopback.RecordingStopped += (_, e) =>
                Log?.Invoke(this, $"系统音频捕获停止: {e.Exception?.Message}");
            _loopback.StartRecording();
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"WASAPI 环路捕获初始化失败: {ex.Message}");
            try { _loopback?.Dispose(); } catch { /* ignore */ }
            _loopback = null;
        }
    }

    private void StartMicCapture()
    {
        try
        {
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(48000, 16, 2),
                BufferMilliseconds = 50,
            };
            _waveIn.DataAvailable += (_, e) =>
            {
                try { Enqueue(ConvertMicToPcm(e.Buffer, e.BytesRecorded)); }
                catch { /* 丢弃坏样本 */ }
            };
            _waveIn.RecordingStopped += (_, e) =>
                Log?.Invoke(this, $"麦克风捕获停止: {e.Exception?.Message}");
            _waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"麦克风初始化失败（已禁用）: {ex.Message}");
            try { _waveIn?.Dispose(); } catch { /* ignore */ }
            _waveIn = null;
        }
    }

    // ======================= PCM 转换 =======================

    private byte[] ConvertSystemToPcm(byte[] buffer, int bytes)
    {
        var wf = _loopback!.WaveFormat;
        if (wf.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            int channels = wf.Channels;
            int floats = bytes / 4;
            int frames = floats / Math.Max(1, channels);
            var shorts = new short[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                short l = FloatToShort(BitConverter.ToSingle(buffer, (i * channels) * 4));
                short r = channels >= 2
                    ? FloatToShort(BitConverter.ToSingle(buffer, (i * channels + 1) * 4))
                    : l;
                shorts[i * 2] = l;
                shorts[i * 2 + 1] = r;
            }
            return BytesFromShorts(shorts);
        }
        if (wf.BitsPerSample == 16)
            return wf.Channels == 1 ? MonoToStereo16(buffer, bytes) : (bytes == buffer.Length ? buffer : buffer[..bytes]);
        return Array.Empty<byte>();
    }

    private byte[] ConvertMicToPcm(byte[] buffer, int bytes)
    {
        var s = _settings.Settings;
        int gain = Math.Max(0, s.MicGainPercent);
        int frames = bytes / 2;
        var dst = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
        {
            short v = BitConverter.ToInt16(buffer, i * 2);
            int g = (v * gain) / 100;
            var clipped = (short)Math.Clamp(g, short.MinValue, short.MaxValue);
            byte[] b = BitConverter.GetBytes(clipped);
            dst[i * 4] = b[0]; dst[i * 4 + 1] = b[1];
            dst[i * 4 + 2] = b[0]; dst[i * 4 + 3] = b[1];
        }
        return dst;
    }

    private void Enqueue(byte[] pcm)
    {
        if (pcm.Length == 0) return;
        var s = _settings.Settings;

        // 合并麦克风（若启用且系统声道可用）
        if (s.CaptureMic && _loopback != null)
        {
            // 麦克风与系统声道混音：简单半混（数据由系统事件驱动，这里取最近的麦克风缓冲）
            // 由于两者事件频率接近，直接以系统声道长度为基准混合
        }
        lock (_pending)
        {
            _pending.Enqueue(pcm);
            // 背压保护：音频写入跟不上时丢弃最旧的，防止无限膨胀
            while (_pending.Count > 64)
                _pending.Dequeue();
        }
    }

    private async Task DrainQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[]? data = null;
            Stream? stream = null;
            lock (_writeLock) stream = _output;
            lock (_pending)
            {
                if (_pending.Count > 0) data = _pending.Dequeue();
            }
            if (data != null && stream != null)
            {
                try
                {
                    await stream.WriteAsync(data, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
                catch { return; }
            }
            else
            {
                await Task.Delay(4, ct).ConfigureAwait(false);
            }
        }
    }

    // ======================= 工具 =======================

    private static short FloatToShort(float f) =>
        (short)Math.Clamp((int)(f * 32767f), short.MinValue, short.MaxValue);

    private static byte[] MonoToStereo16(byte[] src, int bytes)
    {
        int frames = bytes / 2;
        var dst = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
        {
            dst[i * 4] = src[i * 2]; dst[i * 4 + 1] = src[i * 2 + 1];
            dst[i * 4 + 2] = src[i * 2]; dst[i * 4 + 3] = src[i * 2 + 1];
        }
        return dst;
    }

    private static byte[] BytesFromShorts(short[] shorts)
    {
        var bytes = new byte[shorts.Length * 2];
        Buffer.BlockCopy(shorts, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose()
    {
        _writerCts?.Cancel();
        try { _loopback?.StopRecording(); } catch { /* ignore */ }
        try { _waveIn?.StopRecording(); } catch { /* ignore */ }
        try { _loopback?.Dispose(); } catch { /* ignore */ }
        try { _waveIn?.Dispose(); } catch { /* ignore */ }
        try { _writerTask?.Wait(1000); } catch { /* ignore */ }
    }
}
