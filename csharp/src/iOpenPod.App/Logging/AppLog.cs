using System.Collections.Concurrent;
using System.Text;

namespace IOpenPod.App.Logging;

/// <summary>日志级别。</summary>
public enum LogLevel
{
    /// <summary>调试细节。</summary>
    Debug = 0,

    /// <summary>一般信息。</summary>
    Info = 1,

    /// <summary>需要注意但不影响功能。</summary>
    Warning = 2,

    /// <summary>操作失败。</summary>
    Error = 3,
}

/// <summary>一条日志记录。</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    /// <summary>完整的中文显示文本。</summary>
    public string Display =>
        $"[{Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}] [{LevelName}] [{Category}] {Message}";

    /// <summary>级别的中文名称。</summary>
    public string LevelName => AppLog.LevelName(Level);
}

/// <summary>
/// 应用日志。
///
/// 同时写入内存环形缓冲（供界面实时查看）与磁盘文件。
/// 全部内容使用中文，便于用户直接阅读与反馈问题。
/// </summary>
public static class AppLog
{
    private const int MaxMemoryEntries = 4000;
    private const long MaxFileBytes = 8L * 1024 * 1024;

    private static readonly ConcurrentQueue<LogEntry> Entries = new();
    private static readonly object FileGate = new();
    private static string? _logDirectory;
    private static string? _currentFile;
    private static bool _initialized;

    /// <summary>是否有新的日志产生。</summary>
    public static event EventHandler<LogEntry>? EntryWritten;

    /// <summary>初始化日志目录。</summary>
    public static void Initialize(string? directory = null)
    {
        lock (FileGate)
        {
            _logDirectory = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "iOpenPod",
                "logs");

            Directory.CreateDirectory(_logDirectory);
            _currentFile = Path.Combine(_logDirectory, $"iopenpod-{DateTime.Now:yyyyMMdd}.log");
            _initialized = true;
            RollIfNeeded();
        }

        Info("日志", $"日志已启动，目录：{_logDirectory}");
    }

    public static string LogDirectory => _logDirectory ?? string.Empty;

    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "调试",
        LogLevel.Info => "信息",
        LogLevel.Warning => "警告",
        LogLevel.Error => "错误",
        _ => "未知",
    };

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message);

    public static void Info(string category, string message) => Write(LogLevel.Info, category, message);

    public static void Warning(string category, string message) => Write(LogLevel.Warning, category, message);

    public static void Error(string category, string message) => Write(LogLevel.Error, category, message);

    /// <summary>记录一次异常，包含内部异常链。</summary>
    public static void Error(string category, string message, Exception error)
    {
        var builder = new StringBuilder(message);
        var current = error;
        var depth = 0;
        while (current is not null && depth < 6)
        {
            builder.Append(depth == 0 ? "：" : " ← ");
            builder.Append(current.GetType().Name).Append("：").Append(current.Message);
            current = current.InnerException;
            depth++;
        }

        Write(LogLevel.Error, category, builder.ToString());
    }

    /// <summary>取得当前内存中的所有日志。</summary>
    public static IReadOnlyList<LogEntry> Snapshot() => Entries.ToArray();

    /// <summary>清空内存日志（磁盘日志不受影响）。</summary>
    public static void ClearMemory()
    {
        while (Entries.TryDequeue(out _))
        {
            // 逐条丢弃。
        }
    }

    /// <summary>日志文件的完整路径。</summary>
    public static string CurrentFile => _currentFile ?? string.Empty;

    private static void Write(LogLevel level, string category, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, category, message);
        Entries.Enqueue(entry);
        while (Entries.Count > MaxMemoryEntries && Entries.TryDequeue(out _))
        {
            // 维持内存上限。
        }

        try
        {
            lock (FileGate)
            {
                if (!_initialized || _currentFile is null)
                {
                    return;
                }

                RollIfNeeded();
                File.AppendAllText(_currentFile, entry.Display + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 日志写入失败绝不能影响主流程。
        }

        try
        {
            EntryWritten?.Invoke(null, entry);
        }
        catch (Exception)
        {
            // 订阅者异常同样被隔离。
        }
    }

    private static void RollIfNeeded()
    {
        if (_currentFile is null)
        {
            return;
        }

        try
        {
            var info = new FileInfo(_currentFile);
            if (!info.Exists || info.Length < MaxFileBytes)
            {
                return;
            }

            var archived = Path.Combine(
                info.DirectoryName ?? ".",
                $"{Path.GetFileNameWithoutExtension(info.Name)}-{DateTime.Now:HHmmss}.log");
            File.Move(_currentFile, archived, overwrite: true);
        }
        catch (Exception)
        {
            // 归档失败时继续追加。
        }
    }
}
