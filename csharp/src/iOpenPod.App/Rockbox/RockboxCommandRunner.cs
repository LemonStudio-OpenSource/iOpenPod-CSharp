using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using IOpenPod.App.Logging;

namespace IOpenPod.App.Rockbox;

/// <summary>一次外部命令的执行结果。</summary>
internal sealed record RockboxCommandResult(
    string FileName,
    string Arguments,
    int ExitCode,
    string Output,
    bool Elevated,
    bool Cancelled,
    bool Started);

/// <summary>
/// ipodpatcher 与 mks5lboot 的调用包装。
///
/// ipodpatcher 在 Windows 下必须提权，提权通过 <c>Verb = "runas"</c> 触发 UAC；
/// 提权进程的标准输出无法重定向，因此输出统一记录到日志文件。
/// </summary>
internal sealed class RockboxCommandRunner
{
    private readonly StringBuilder _log = new();
    private readonly Action<string>? _onElevation;

    public RockboxCommandRunner(Action<string>? onElevation = null)
    {
        _onElevation = onElevation;
    }

    /// <summary>累计的日志文本。</summary>
    public string LogText => _log.ToString();

    /// <summary>把一行写入内存日志与磁盘日志。</summary>
    public void Write(string line)
    {
        _log.AppendLine(line);
        AppLog.Info("Rockbox", line);
    }

    /// <summary>
    /// 执行命令。
    ///
    /// <paramref name="elevate"/> 为真时使用 UAC 提权；用户拒绝 UAC 不会抛异常，而是返回
    /// <c>Started=false</c> 的结果，由调用方给出中文提示。
    /// </summary>
    public async Task<RockboxCommandResult> RunAsync(
        string fileName,
        string arguments,
        bool elevate,
        CancellationToken cancellationToken)
    {
        var script = IsScript(fileName);
        var useElevation = elevate && !script;
        Write($"命令行：\"{fileName}\" {arguments}{(useElevation ? "（需要管理员权限，正在请求 UAC 提权）" : string.Empty)}");

        if (useElevation)
        {
            _onElevation?.Invoke("接下来的步骤会弹出 Windows UAC 提示，请选择“是”以允许修改设备固件。");
        }

        if (!File.Exists(fileName))
        {
            Write($"错误：找不到可执行文件 {fileName}");
            return new RockboxCommandResult(fileName, arguments, -1, "找不到可执行文件。", useElevation, false, false);
        }

        if (script && elevate)
        {
            // .cmd / .bat 不能通过 runas 提权，只能用 shell 启动，因此不会请求 UAC。
            Write("提示：该路径是脚本（.cmd/.bat），无法通过 UAC 提权运行，将以当前用户权限启动并捕获输出。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = useElevation,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory,
        };

        if (useElevation)
        {
            startInfo.Verb = "runas";
        }
        else
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Write("错误：进程未能启动。");
                return new RockboxCommandResult(fileName, arguments, -1, "进程未能启动。", useElevation, false, false);
            }

            string output = string.Empty;
            if (useElevation)
            {
                // 提权进程无法重定向输出，只能等待退出码。
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                output = (await standardOutput.ConfigureAwait(false)) + (await standardError.ConfigureAwait(false));
            }

            if (!string.IsNullOrWhiteSpace(output))
            {
                Write("---- 输出开始 ----");
                foreach (var line in output.Split('\n'))
                {
                    Write("  " + line.TrimEnd('\r'));
                }

                Write("---- 输出结束 ----");
            }

            Write($"退出码：{process.ExitCode}");
            return new RockboxCommandResult(fileName, arguments, process.ExitCode, output, useElevation, false, true);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED：用户在 UAC 对话框中选择了“否”。
            Write("用户取消了管理员权限请求（UAC），该步骤未执行。");
            return new RockboxCommandResult(
                fileName, arguments, -1, "用户取消了管理员权限请求（UAC）。", useElevation, false, false);
        }
        catch (OperationCanceledException)
        {
            Write("命令已被取消。");
            throw;
        }
        catch (Exception error)
        {
            Write($"命令执行失败：{error.GetType().Name}：{error.Message}");
            return new RockboxCommandResult(fileName, arguments, -1, error.Message, useElevation, false, false);
        }
    }

    /// <summary>判断路径是否为批处理脚本（这类文件无法通过 UAC 提权启动）。</summary>
    private static bool IsScript(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 以普通权限执行一条命令并捕获输出，不需要用户交互。用于状态探测与自检。
    /// </summary>
    public static async Task<RockboxCommandResult> RunCapturedAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(fileName))
        {
            return new RockboxCommandResult(fileName, arguments, -1, "找不到可执行文件。", false, false, false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new RockboxCommandResult(fileName, arguments, -1, "进程未能启动。", false, false, false);
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = (await standardOutput.ConfigureAwait(false)) + (await standardError.ConfigureAwait(false));

            AppLog.Info("Rockbox", $"只读探测 \"{fileName}\" {arguments} 退出码 {process.ExitCode}。");
            return new RockboxCommandResult(fileName, arguments, process.ExitCode, output, false, false, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            AppLog.Warning("Rockbox", $"只读探测失败：{fileName}（{error.Message}）");
            return new RockboxCommandResult(fileName, arguments, -1, error.Message, false, false, false);
        }
    }

    /// <summary>把日志写入指定文件。</summary>
    public void Flush(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _log.ToString(), Encoding.UTF8);
        }
        catch (Exception error)
        {
            AppLog.Warning("Rockbox", $"写入日志文件失败：{path}（{error.Message}）");
        }
    }
}
