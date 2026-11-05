namespace ReplayTool.Core.Models;

/// <summary>编码格式枚举。</summary>
public enum VideoCodecFormat
{
    H264,
    HEVC,
}

/// <summary>编码器选择结果：编码器名 + 是否为硬件编码。</summary>
public record EncoderSelection(string EncoderName, VideoCodecFormat Format, bool IsHardware, string Reason);

/// <summary>捕获配置（供编码参数拼接使用）。</summary>
public class CaptureConfig
{
    public string Mode { get; set; } = "fullscreen";
    public string? WindowTitle { get; set; }
    public int MonitorIndex { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Framerate { get; set; } = 60;
}

/// <summary>一次回放保存会话的上下文信息。</summary>
public class ReplaySession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public string? ForegroundProcess { get; set; }
    public int DurationSeconds { get; set; }
    public string OutputPath { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string Resolution { get; set; } = "";
}
