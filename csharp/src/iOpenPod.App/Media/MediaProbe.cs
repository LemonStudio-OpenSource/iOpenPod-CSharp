using System.Globalization;
using System.Text.Json;
using IOpenPod.App.Logging;
using IOpenPod.DeviceRegistry;

namespace IOpenPod.App.Media;

/// <summary>一路媒体流的观测结果。</summary>
public sealed record MediaStreamInfo(
    string CodecType,
    string CodecName,
    string Profile,
    string Level,
    int Width,
    int Height,
    int BitrateKbps,
    double FrameRate,
    int SampleRate,
    int Channels,
    bool IsAttachedPicture)
{
    public bool IsVideo => string.Equals(CodecType, "video", StringComparison.OrdinalIgnoreCase);

    public bool IsAudio => string.Equals(CodecType, "audio", StringComparison.OrdinalIgnoreCase);
}

/// <summary>一次媒体探测的结果。</summary>
public sealed record MediaProbeResult(
    bool Success,
    string? Error,
    TimeSpan Duration,
    long SizeBytes,
    long BitrateKbps,
    string FormatName,
    IReadOnlyList<MediaStreamInfo> Streams)
{
    public static MediaProbeResult Failed(string error) => new(false, error, TimeSpan.Zero, 0, 0, string.Empty, []);

    /// <summary>第一路视频流。</summary>
    public MediaStreamInfo? Video =>
        Streams.FirstOrDefault(stream => stream.IsVideo && !stream.IsAttachedPicture);

    /// <summary>第一路音频流。</summary>
    public MediaStreamInfo? Audio => Streams.FirstOrDefault(stream => stream.IsAudio);

    /// <summary>是否为视频文件。</summary>
    public bool HasVideo => Video is not null;
}

/// <summary>媒体文件探测。优先使用 ffprobe，失败时退化为标签读取器的信息。</summary>
public sealed class MediaProbe
{
    private readonly FfmpegTools _tools;

    public MediaProbe(FfmpegTools tools)
    {
        _tools = tools;
    }

    /// <summary>探测一个宿主媒体文件。</summary>
    public async Task<MediaProbeResult> ProbeAsync(string hostPath, CancellationToken cancellationToken = default)
    {
        if (!_tools.Location.CanProbe)
        {
            return MediaProbeResult.Failed("未找到 ffprobe。");
        }

        try
        {
            var output = await _tools.RunFfprobeAsync(
                [
                    "-hide_banner",
                    "-loglevel", "error",
                    "-print_format", "json",
                    "-show_format",
                    "-show_streams",
                    hostPath,
                ],
                cancellationToken).ConfigureAwait(false);

            if (!output.Success)
            {
                return MediaProbeResult.Failed(output.ErrorSummary);
            }

            return Parse(output.StandardOutput, new FileInfo(hostPath).Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            AppLog.Warning("探测", $"探测失败：{hostPath}（{error.Message}）");
            return MediaProbeResult.Failed(error.Message);
        }
    }

    /// <summary>解析 ffprobe 的 JSON 输出。</summary>
    internal static MediaProbeResult Parse(string json, long sizeBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var streams = new List<MediaStreamInfo>();
            if (root.TryGetProperty("streams", out var streamArray) && streamArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streamArray.EnumerateArray())
                {
                    var codecType = ReadString(stream, "codec_type");
                    var dispositions = stream.TryGetProperty("disposition", out var disposition)
                        ? disposition
                        : default;
                    var attachedPicture = dispositions.ValueKind == JsonValueKind.Object &&
                                          ReadInt(dispositions, "attached_pic") == 1;

                    streams.Add(new MediaStreamInfo(
                        codecType,
                        ReadString(stream, "codec_name"),
                        ReadString(stream, "profile"),
                        ReadString(stream, "level"),
                        ReadInt(stream, "width"),
                        ReadInt(stream, "height"),
                        (int)(ParseBitrate(ReadString(stream, "bit_rate")) / 1000),
                        ParseFrameRate(ReadString(stream, "avg_frame_rate")),
                        ParseInt(ReadString(stream, "sample_rate")),
                        ReadInt(stream, "channels"),
                        attachedPicture));
                }
            }

            var formatName = string.Empty;
            var duration = TimeSpan.Zero;
            var bitrateKbps = 0L;

            if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object)
            {
                formatName = ReadString(format, "format_name");
                var seconds = ParseDouble(ReadString(format, "duration"));
                if (seconds > 0)
                {
                    duration = TimeSpan.FromSeconds(seconds);
                }

                bitrateKbps = ParseBitrate(ReadString(format, "bit_rate")) / 1000;
            }

            var size = sizeBytes;
            if (size <= 0 && root.TryGetProperty("format", out var formatElement))
            {
                size = ParseLong(ReadString(formatElement, "size"));
            }

            return new MediaProbeResult(true, null, duration, size, bitrateKbps, formatName, streams);
        }
        catch (Exception error)
        {
            return MediaProbeResult.Failed($"ffprobe 输出无法解析：{error.Message}");
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : 0;

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static long ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static long ParseBitrate(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;

    /// <summary>解析形如 <c>30000/1001</c> 的帧率。</summary>
    private static double ParseFrameRate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator > 0)
        {
            return numerator / denominator;
        }

        return ParseDouble(value);
    }
}

/// <summary>媒体转换的判定结果。</summary>
public enum ConversionKind
{
    /// <summary>无需转换，可直接复制。</summary>
    None,

    /// <summary>需要重新编码音频。</summary>
    Audio,

    /// <summary>需要重新编码视频（含音频）。</summary>
    Video,

    /// <summary>只需重新封装容器。</summary>
    Remux,

    /// <summary>设备不支持该媒体，且无法转换。</summary>
    Unsupported,
}

/// <summary>一次转换决策。</summary>
public sealed record ConversionDecision(ConversionKind Kind, string Reason)
{
    public bool RequiresWork => Kind is not ConversionKind.None;

    public static readonly ConversionDecision Direct = new(ConversionKind.None, "设备可直接播放。");
}

/// <summary>
/// 设备媒体兼容性策略：根据设备档案判断一个宿主文件能否直接播放，或需要怎样转换。
/// </summary>
public static class DeviceMediaPolicy
{
    /// <summary>设备原生支持的音频编码。</summary>
    private static readonly HashSet<string> NativeAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "alac", "mp3", "pcm_s16le", "pcm_s16be", "pcm_s24le", "pcm_u8", "pcm_s16le_planar",
    };

    /// <summary>设备原生支持音频的容器扩展名。</summary>
    private static readonly HashSet<string> NativeAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".m4p", ".m4r", ".aac", ".wav", ".aif", ".aiff", ".alac",
    };

    /// <summary>设备原生支持视频的容器扩展名。</summary>
    private static readonly HashSet<string> NativeVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4v", ".mp4", ".mov",
    };

    /// <summary>判断一个宿主文件在目标设备上需要怎样处理。</summary>
    public static ConversionDecision Decide(string hostPath, MediaTagInfo tags, MediaProbeResult? probe)
    {
        var extension = Path.GetExtension(hostPath);
        var isVideo = tags.IsVideo || (probe?.HasVideo ?? false) || IsLikelyVideo(extension);

        if (!isVideo)
        {
            return DecideAudio(extension, probe);
        }

        return DecideVideo(extension, probe);
    }

    /// <summary>
    /// 是否需要调用 ffprobe 做精确探测。
    ///
    /// 只对「可能是视频」或「容器不是设备原生格式」的文件探测，
    /// 避免对成千上万个普通 MP3 逐个启动外部进程。
    /// </summary>
    public static bool ShouldProbe(string hostPath, MediaTagInfo tags)
    {
        var extension = Path.GetExtension(hostPath);
        if (tags.IsVideo || IsLikelyVideo(extension))
        {
            return true;
        }

        return !NativeAudioExtensions.Contains(extension);
    }

    /// <summary>按扩展名粗略判断是否为视频候选。</summary>
    public static bool IsLikelyVideo(string extension) =>
        extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".flv", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".mpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".mpeg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".m2ts", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".vob", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".mov", StringComparison.OrdinalIgnoreCase);

    private static ConversionDecision DecideAudio(string extension, MediaProbeResult? probe)
    {
        if (!NativeAudioExtensions.Contains(extension))
        {
            return new ConversionDecision(
                ConversionKind.Audio,
                $"容器 {extension} 不是 iPod 原生支持的音频格式，需要重新编码为 AAC。");
        }

        if (probe is null || !probe.Success)
        {
            return ConversionDecision.Direct;
        }

        var audio = probe.Audio;
        if (audio is null)
        {
            return new ConversionDecision(ConversionKind.Audio, "文件里没有可用的音频流。");
        }

        if (!NativeAudioCodecs.Contains(audio.CodecName))
        {
            return new ConversionDecision(
                ConversionKind.Audio,
                $"音频编码 {audio.CodecName} 不被设备支持，需要重新编码为 AAC。");
        }

        if (audio.SampleRate > 48000)
        {
            return new ConversionDecision(
                ConversionKind.Audio,
                $"采样率 {audio.SampleRate} Hz 超出设备上限 48000 Hz，需要重新采样。");
        }

        if (audio.Channels > 2)
        {
            return new ConversionDecision(
                ConversionKind.Audio,
                $"{audio.Channels} 声道超出设备上限 2 声道，需要下混。");
        }

        return ConversionDecision.Direct;
    }

    private static ConversionDecision DecideVideo(string extension, MediaProbeResult? probe)
    {
        if (probe is null || !probe.Success)
        {
            return new ConversionDecision(
                ConversionKind.Video,
                "无法探测视频参数，按保守策略转换。");
        }

        var video = probe.Video;
        if (video is null)
        {
            return new ConversionDecision(ConversionKind.Unsupported, "文件里没有可用的视频流。");
        }

        var needsRemuxOnly = true;

        if (!string.Equals(video.CodecName, "h264", StringComparison.OrdinalIgnoreCase))
        {
            return new ConversionDecision(
                ConversionKind.Video,
                $"视频编码 {video.CodecName} 不被设备支持，需要转为 H.264。");
        }

        if (!NativeVideoExtensions.Contains(extension))
        {
            needsRemuxOnly = false;
        }

        var audio = probe.Audio;
        if (audio is not null && !NativeAudioCodecs.Contains(audio.CodecName))
        {
            return new ConversionDecision(
                ConversionKind.Video,
                $"音轨编码 {audio.CodecName} 不被设备支持，需要转为 AAC。");
        }

        // 常见 iPod 视频上限：640×480（第 5 代）到 640×480 / 720×480（classic）。
        var maxWidth = 640;
        var maxHeight = 480;

        if (video.Width > maxWidth || video.Height > maxHeight)
        {
            return new ConversionDecision(
                ConversionKind.Video,
                $"分辨率 {video.Width}×{video.Height} 超出设备上限 {maxWidth}×{maxHeight}，需要缩放。");
        }

        if (probe.BitrateKbps > 2500)
        {
            return new ConversionDecision(
                ConversionKind.Video,
                $"总码率 {probe.BitrateKbps} kbps 偏高，需要重新编码以降低码率。");
        }

        return needsRemuxOnly
            ? ConversionDecision.Direct
            : new ConversionDecision(ConversionKind.Remux, $"容器 {extension} 需要重新封装为 M4V。");
    }

    /// <summary>把设备档案里的视频能力转成中文描述，用于界面展示。</summary>
    public static string DescribeVideoCapability(DeviceProfile? profile)
    {
        if (profile is null)
        {
            return "未知设备：无法判断视频能力。";
        }

        var video = profile.Capabilities.Video;
        if (!video.Supported)
        {
            return $"{profile.ChineseDisplayName} 不支持视频播放。";
        }

        return $"{profile.ChineseDisplayName} 支持视频：最大 {video.MaxWidth}×{video.MaxHeight}，" +
               $"H.264 {video.H264Level}，最高 {video.MaxFps} fps，视频码率上限 {video.MaxBitrateKbps} kbps，" +
               $"音频 {video.MaxAudioBitrateKbps} kbps。";
    }
}
