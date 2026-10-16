using System.Globalization;
using IOpenPod.App.Logging;
using IOpenPod.DeviceRegistry;

namespace IOpenPod.App.Media;

/// <summary>转码选项。</summary>
public sealed record MediaTranscodeOptions
{
    /// <summary>ffmpeg 所在目录；留空则自动查找。</summary>
    public string? FfmpegDirectory { get; init; }

    /// <summary>音频转码目标格式：<c>preserve</c> / <c>aac</c> / <c>mp3</c> / <c>alac</c>。</summary>
    public string AudioFormat { get; init; } = "preserve";

    /// <summary>音频转码目标码率（kbps）。</summary>
    public int AudioBitrateKbps { get; init; } = 256;

    /// <summary>是否允许重新编码音频。</summary>
    public bool AllowAudioConversion { get; init; } = true;

    /// <summary>是否允许重新编码视频。</summary>
    public bool AllowVideoConversion { get; init; } = true;

    /// <summary>视频目标码率（kbps）。</summary>
    public int VideoBitrateKbps { get; init; } = 1500;

    /// <summary>视频目标最大宽度。</summary>
    public int VideoMaxWidth { get; init; } = 640;

    /// <summary>视频目标最大高度。</summary>
    public int VideoMaxHeight { get; init; } = 480;

    /// <summary>视频目标帧率上限。</summary>
    public int VideoMaxFps { get; init; } = 30;

    /// <summary>转码临时文件目录。</summary>
    public string? WorkDirectory { get; init; }
}

/// <summary>转码进度。</summary>
public sealed record TranscodeProgress(string FileName, TimeSpan Position, TimeSpan Total, double Speed)
{
    public double Fraction => Total.TotalSeconds <= 0
        ? 0
        : Math.Clamp(Position.TotalSeconds / Total.TotalSeconds, 0, 1);

    public string Display => Total.TotalSeconds <= 0
        ? $"{FileName}：{Position:mm\\:ss}"
        : $"{FileName}：{Position:mm\\:ss} / {Total:mm\\:ss}（{Fraction:P0}{(Speed > 0 ? $"，{Speed:F1}×" : string.Empty)}）";
}

/// <summary>一次转码的结果。</summary>
public sealed record TranscodeResult(bool Success, string? OutputPath, string Message, TimeSpan Elapsed)
{
    public static TranscodeResult Failed(string message) => new(false, null, message, TimeSpan.Zero);
}

/// <summary>
/// 媒体转码器：把宿主媒体转换成目标 iPod 可以直接播放的格式。
///
/// 转码参数由设备档案（分辨率、码率、帧率、编码档次）推导，输出为临时文件，
/// 由调用方决定何时删除。
/// </summary>
public sealed class MediaTranscoder
{
    private readonly FfmpegTools _tools;
    private readonly MediaProbe _probe;
    private readonly string _workDirectory;

    public MediaTranscoder(FfmpegTools tools, string workDirectory)
    {
        _tools = tools;
        _probe = new MediaProbe(tools);
        _workDirectory = workDirectory;
        Directory.CreateDirectory(_workDirectory);
    }

    /// <summary>ffmpeg 是否可用。</summary>
    public bool IsAvailable => _tools.Location.CanTranscode;

    /// <summary>面向用户的能力说明。</summary>
    public string Availability => _tools.Location.Display;

    /// <summary>探测一个文件。</summary>
    public Task<MediaProbeResult> ProbeAsync(string hostPath, CancellationToken cancellationToken = default) =>
        _probe.ProbeAsync(hostPath, cancellationToken);

    /// <summary>
    /// 按决策转换一个宿主文件；返回可直接交给设备写入的临时文件路径。
    /// </summary>
    public async Task<TranscodeResult> ConvertAsync(
        string hostPath,
        ConversionDecision decision,
        MediaTranscodeOptions options,
        DeviceProfile? profile = null,
        IProgress<TranscodeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (decision.Kind == ConversionKind.None)
        {
            return new TranscodeResult(true, hostPath, "无需转换。", TimeSpan.Zero);
        }

        if (!IsAvailable)
        {
            return TranscodeResult.Failed("未找到 ffmpeg，无法转换媒体格式。");
        }

        var isVideo = decision.Kind is ConversionKind.Video;
        if (isVideo && !options.AllowVideoConversion)
        {
            return TranscodeResult.Failed("设置中已关闭视频转换，该文件将被跳过。");
        }

        if (!isVideo && decision.Kind == ConversionKind.Audio && !options.AllowAudioConversion)
        {
            return TranscodeResult.Failed("设置中已关闭音频转换，该文件将被跳过。");
        }

        var probe = await ProbeAsync(hostPath, cancellationToken).ConfigureAwait(false);
        var duration = probe.Success ? probe.Duration : TimeSpan.Zero;

        var extension = isVideo || decision.Kind == ConversionKind.Remux ? ".m4v" : ".m4a";
        var outputName = Sanitize(Path.GetFileNameWithoutExtension(hostPath)) + "-" +
                         Guid.NewGuid().ToString("N")[..8] + extension;
        var outputPath = Path.Combine(options.WorkDirectory ?? _workDirectory, outputName);

        var arguments = BuildArguments(hostPath, outputPath, decision, options, profile, probe);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var fileName = Path.GetFileName(hostPath);
        var speed = 0.0;

        AppLog.Info("转码", $"开始转换 {fileName} → {Path.GetFileName(outputPath)}（{decision.Reason}）");

        var output = await _tools.RunFfmpegAsync(
            arguments,
            line => OnProgressLine(line, fileName, duration, progress, ref speed),
            cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();

        if (!output.Success)
        {
            TryDelete(outputPath);
            var message = $"转换失败：{output.ErrorSummary}";
            AppLog.Error("转码", $"{fileName} {message}");
            return new TranscodeResult(false, null, message, stopwatch.Elapsed);
        }

        if (!File.Exists(outputPath))
        {
            return new TranscodeResult(false, null, "转换失败：没有生成输出文件。", stopwatch.Elapsed);
        }

        var outputProbe = await ProbeAsync(outputPath, cancellationToken).ConfigureAwait(false);
        if (!outputProbe.Success || (isVideo && outputProbe.Video is null))
        {
            TryDelete(outputPath);
            return new TranscodeResult(false, null, "转换失败：输出文件无法验证。", stopwatch.Elapsed);
        }

        AppLog.Info("转码", $"{fileName} 转换完成（{stopwatch.Elapsed.TotalSeconds:F1} 秒）。");
        return new TranscodeResult(true, outputPath, "转换完成。", stopwatch.Elapsed);
    }

    private static void OnProgressLine(
        string line,
        string fileName,
        TimeSpan duration,
        IProgress<TranscodeProgress>? progress,
        ref double speed)
    {
        if (progress is null)
        {
            return;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return;
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();

        switch (key)
        {
            case "out_time_ms":
            case "out_time_us":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micro))
                {
                    progress.Report(new TranscodeProgress(
                        fileName, TimeSpan.FromMicroseconds(Math.Max(0, micro)), duration, speed));
                }

                break;

            case "speed":
                var text = value.TrimEnd('x', 'X');
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    speed = parsed;
                }

                break;
        }
    }

    /// <summary>构造 ffmpeg 命令行。</summary>
    internal static List<string> BuildArguments(
        string input,
        string output,
        ConversionDecision decision,
        MediaTranscodeOptions options,
        DeviceProfile? profile,
        MediaProbeResult probe)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-y",
            "-i", input,
        };

        var maxWidth = options.VideoMaxWidth;
        var maxHeight = options.VideoMaxHeight;
        var maxFps = options.VideoMaxFps;

        if (profile is not null && profile.Capabilities.Video.Supported)
        {
            // 设备档案给出的上限更权威。
            maxWidth = Math.Min(maxWidth, profile.Capabilities.Video.MaxWidth);
            maxHeight = Math.Min(maxHeight, profile.Capabilities.Video.MaxHeight);
            maxFps = Math.Min(maxFps, profile.Capabilities.Video.MaxFps);
        }

        switch (decision.Kind)
        {
            case ConversionKind.Video:
                arguments.AddRange(
                [
                    "-c:v", "libx264",
                    "-profile:v", "baseline",
                    "-level", "3.0",
                    "-pix_fmt", "yuv420p",
                    "-vf", $"scale={maxWidth}:{maxHeight}:force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1",
                    "-r", maxFps.ToString(CultureInfo.InvariantCulture),
                    "-b:v", $"{options.VideoBitrateKbps}k",
                    "-maxrate", $"{options.VideoBitrateKbps * 4 / 3}k",
                    "-bufsize", $"{options.VideoBitrateKbps * 8 / 3}k",
                    "-c:a", "aac",
                    "-b:a", $"{Math.Clamp(options.AudioBitrateKbps, 64, 160)}k",
                    "-ac", "2",
                    "-ar", "44100",
                    "-sn",
                    "-dn",
                ]);
                break;

            case ConversionKind.Remux:
                arguments.AddRange(["-c:v", "copy"]);
                if (probe.Audio is not null)
                {
                    arguments.AddRange(["-c:a", "copy"]);
                }

                arguments.AddRange(["-sn", "-dn"]);
                break;

            case ConversionKind.Audio:
                arguments.AddRange(["-vn", "-sn", "-dn"]);
                arguments.AddRange(BuildAudioArguments(options, probe));
                break;
        }

        arguments.AddRange(["-progress", "pipe:1", "-nostats", "-movflags", "+faststart", output]);
        return arguments;
    }

    private static List<string> BuildAudioArguments(MediaTranscodeOptions options, MediaProbeResult probe)
    {
        var bitrate = Math.Clamp(options.AudioBitrateKbps, 64, 320);
        var arguments = new List<string>();

        switch (options.AudioFormat)
        {
            case "mp3":
                arguments.AddRange(["-c:a", "libmp3lame", "-b:a", $"{bitrate}k"]);
                break;

            case "alac":
                arguments.AddRange(["-c:a", "alac"]);
                break;

            case "preserve":
                // 保持原格式时，只有当原编码设备不支持才回退为 AAC。
                var codec = probe.Audio?.CodecName ?? string.Empty;
                if (codec is "aac" or "alac")
                {
                    arguments.AddRange(["-c:a", "copy"]);
                }
                else if (codec is "mp3")
                {
                    arguments.AddRange(["-c:a", "copy"]);
                }
                else
                {
                    arguments.AddRange(["-c:a", "aac", "-b:a", $"{bitrate}k"]);
                }

                break;

            default:
                arguments.AddRange(["-c:a", "aac", "-b:a", $"{bitrate}k"]);
                break;
        }

        var sampleRate = probe.Audio?.SampleRate ?? 0;
        if (sampleRate > 48000 || sampleRate <= 0)
        {
            arguments.AddRange(["-ar", "44100"]);
        }

        if ((probe.Audio?.Channels ?? 0) > 2)
        {
            arguments.AddRange(["-ac", "2"]);
        }

        return arguments;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.Length == 0 ? "media" : builder.ToString();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响结果。
        }
    }
}
