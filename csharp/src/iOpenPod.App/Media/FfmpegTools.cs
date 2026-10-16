using System.Diagnostics;
using System.Text;
using IOpenPod.App.Logging;

namespace IOpenPod.App.Media;

/// <summary>ffmpeg / ffprobe 的定位结果。</summary>
public sealed record FfmpegLocation(string? FfmpegPath, string? FfprobePath, string Source)
{
    /// <summary>两个工具都可用。</summary>
    public bool IsComplete => !string.IsNullOrEmpty(FfmpegPath) && !string.IsNullOrEmpty(FfprobePath);

    /// <summary>是否至少能转码。</summary>
    public bool CanTranscode => !string.IsNullOrEmpty(FfmpegPath);

    /// <summary>是否至少能探测。</summary>
    public bool CanProbe => !string.IsNullOrEmpty(FfprobePath);

    /// <summary>面向用户的中文说明。</summary>
    public string Display => IsComplete
        ? $"ffmpeg：{FfmpegPath}{Environment.NewLine}ffprobe：{FfprobePath}"
        : CanTranscode || CanProbe
            ? "只找到部分媒体工具，视频转码与探测能力可能受限。"
            : "未找到 ffmpeg / ffprobe。视频与音频转码、精确媒体探测将不可用。";

    public static readonly FfmpegLocation Missing = new(null, null, "未找到");
}

/// <summary>
/// 外部媒体工具（FFmpeg / FFprobe）的定位与调用。
///
/// 工具不属于本程序的发行包，需要用户自行安装或用设置指定目录。
/// </summary>
public sealed class FfmpegTools
{
    private readonly FfmpegLocation _location;

    public FfmpegTools(FfmpegLocation location)
    {
        _location = location;
    }

    /// <summary>当前定位结果。</summary>
    public FfmpegLocation Location => _location;

    /// <summary>
    /// 定位 ffmpeg 与 ffprobe：优先使用显式目录，其次 PATH，最后几个常见安装位置。
    /// </summary>
    public static FfmpegLocation Locate(string? configuredDirectory = null)
    {
        var directories = new List<string>();

        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            directories.Add(configuredDirectory.Trim());
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        directories.AddRange(pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        foreach (var candidate in new[]
                 {
                     @"C:\ffmpeg\bin",
                     @"C:\Program Files\ffmpeg\bin",
                     @"C:\Program Files (x86)\ffmpeg\bin",
                     Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "iOpenPod", "tools"),
                     Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Microsoft", "WinGet", "Links"),
                 })
        {
            directories.Add(candidate);
        }

        var ffmpeg = FindExecutable("ffmpeg.exe", directories);
        var ffprobe = FindExecutable("ffprobe.exe", directories);
        var source = ffmpeg is null && ffprobe is null
            ? "未找到"
            : (string.IsNullOrWhiteSpace(configuredDirectory) ? "系统 PATH" : "自定义目录");

        var location = new FfmpegLocation(ffmpeg, ffprobe, source);
        AppLog.Info("媒体工具", location.Display.Replace(Environment.NewLine, "  "));
        return location;
    }

    private static string? FindExecutable(string fileName, IEnumerable<string> directories)
    {
        foreach (var directory in directories)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    continue;
                }

                var candidate = Path.Combine(directory.Trim().Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // 无效目录直接跳过。
            }
        }

        return null;
    }

    /// <summary>运行 ffprobe 并返回标准输出。</summary>
    public async Task<ProcessOutput> RunFfprobeAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        if (_location.FfprobePath is null)
        {
            return new ProcessOutput(false, string.Empty, "未找到 ffprobe。", -1);
        }

        return await RunAsync(_location.FfprobePath, arguments, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 运行 ffmpeg。
    /// </summary>
    /// <param name="arguments">命令行参数。</param>
    /// <param name="onStandardOutputLine">标准输出逐行回调（用于解析 -progress）。</param>
    public async Task<ProcessOutput> RunFfmpegAsync(
        IEnumerable<string> arguments,
        Action<string>? onStandardOutputLine,
        CancellationToken cancellationToken)
    {
        if (_location.FfmpegPath is null)
        {
            return new ProcessOutput(false, string.Empty, "未找到 ffmpeg。", -1);
        }

        return await RunAsync(_location.FfmpegPath, arguments, onStandardOutputLine, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ProcessOutput> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        Action<string>? onStandardOutputLine,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var outputGate = new object();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            lock (outputGate)
            {
                standardOutput.AppendLine(args.Data);
            }

            try
            {
                onStandardOutputLine?.Invoke(args.Data);
            }
            catch (Exception)
            {
                // 回调异常不影响进程。
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            lock (outputGate)
            {
                standardError.AppendLine(args.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return new ProcessOutput(false, string.Empty, "进程无法启动。", -1);
            }
        }
        catch (Exception error)
        {
            return new ProcessOutput(false, string.Empty, error.Message, -1);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        // 确保异步读取缓冲全部排空。
        process.WaitForExit();

        lock (outputGate)
        {
            return new ProcessOutput(
                process.ExitCode == 0,
                standardOutput.ToString(),
                standardError.ToString(),
                process.ExitCode);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 终止失败无需报告。
        }
    }
}

/// <summary>一次外部进程调用的结果。</summary>
public sealed record ProcessOutput(bool Success, string StandardOutput, string StandardError, int ExitCode)
{
    /// <summary>错误摘要（优先使用标准错误的后几行）。</summary>
    public string ErrorSummary
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length == 0 ? "没有输出。" : string.Join(" / ", lines.TakeLast(3));
        }
    }
}
