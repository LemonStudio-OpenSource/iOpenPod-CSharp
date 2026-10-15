using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using IOpenPod.App.Devices;
using IOpenPod.App.Logging;
using IOpenPod.Storage;

namespace IOpenPod.App.Backup;

/// <summary>备份清单中的一个文件。</summary>
public sealed record BackupFileEntry(string Path, long Size, DateTimeOffset ModifiedUtc, string? Sha256);

/// <summary>备份快照清单。</summary>
public sealed record BackupManifest(
    int FormatVersion,
    string DeviceName,
    string ModelNumber,
    string SerialNumber,
    DateTimeOffset CreatedUtc,
    int TrackCount,
    long TotalBytes,
    IReadOnlyList<BackupFileEntry> Files);

/// <summary>一个备份快照。</summary>
public sealed record BackupSnapshot(string Directory, BackupManifest Manifest)
{
    public string Name => Path.GetFileName(Directory.TrimEnd(Path.DirectorySeparatorChar));

    public string Display =>
        $"{Manifest.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Manifest.DeviceName} · " +
        $"{Manifest.TrackCount} 首 · {Manifest.TotalBytes / 1024.0 / 1024.0:F1} MB";

    public int FileCount => Manifest.Files.Count;
}

/// <summary>备份进度。</summary>
public sealed record BackupProgress(string Phase, int Completed, int Total, string CurrentFile)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);

    public string Display => $"{Phase}（{Completed}/{Total}）{CurrentFile}";
}

/// <summary>备份与还原。</summary>
public sealed class BackupManager
{
    private const int FormatVersion = 1;
    private const long HashLimitBytes = 32L * 1024 * 1024;
    private const string ManifestName = "iopenpod-backup.json";

    private static readonly string[] ManagedRoots = ["iPod_Control", "Photos"];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>为设备创建一个新的备份快照。</summary>
    public async Task<BackupSnapshot> CreateAsync(
        ActiveDevice device,
        string backupRoot,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = EnumerateDeviceFiles(device);
        var snapshotName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(device.Name)}";
        var directory = Path.Combine(backupRoot, Sanitize(device.Name), snapshotName);
        Directory.CreateDirectory(directory);

        AppLog.Info("备份", $"开始备份 {device.Name}，共 {files.Count} 个文件 → {directory}");

        var entries = new List<BackupFileEntry>(files.Count);
        long totalBytes = 0;
        var index = 0;

        foreach (var relative in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            var sourcePath = SessionPath.Create(relative);
            var sourceHost = device.Session.ToHostPath(sourcePath);
            var targetHost = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetHost)!);
                var info = new FileInfo(sourceHost);
                await using (var source = new FileStream(
                                 sourceHost, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                 1 << 20, useAsync: true))
                await using (var target = new FileStream(
                                 targetHost, FileMode.Create, FileAccess.Write, FileShare.None,
                                 1 << 20, useAsync: true))
                {
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }

                string? hash = null;
                if (info.Length <= HashLimitBytes)
                {
                    hash = await ComputeHashAsync(targetHost, cancellationToken).ConfigureAwait(false);
                }

                entries.Add(new BackupFileEntry(
                    relative, info.Length, info.LastWriteTimeUtc, hash));
                totalBytes += info.Length;

                progress?.Report(new BackupProgress("正在备份", index, files.Count, relative));
            }
            catch (Exception error)
            {
                AppLog.Error("备份", $"文件备份失败：{relative}", error);
            }
        }

        var manifest = new BackupManifest(
            FormatVersion,
            device.Name,
            device.Profile?.ModelNumber ?? string.Empty,
            device.SerialNumber,
            DateTimeOffset.Now,
            device.TrackCount,
            totalBytes,
            entries);

        await File.WriteAllTextAsync(
            Path.Combine(directory, ManifestName),
            JsonSerializer.Serialize(manifest, Options),
            cancellationToken).ConfigureAwait(false);

        ApplyRetention(backupRoot, device.Name);

        AppLog.Info("备份", $"备份完成：{entries.Count} 个文件，{totalBytes / 1024.0 / 1024.0:F1} MB。");
        progress?.Report(new BackupProgress("备份完成", files.Count, files.Count, string.Empty));
        return new BackupSnapshot(directory, manifest);
    }

    /// <summary>列出全部备份快照。</summary>
    public IReadOnlyList<BackupSnapshot> List(string backupRoot)
    {
        var results = new List<BackupSnapshot>();
        if (!Directory.Exists(backupRoot))
        {
            return results;
        }

        foreach (var directory in Directory.EnumerateDirectories(backupRoot))
        {
            foreach (var snapshot in Directory.EnumerateDirectories(directory))
            {
                var manifestPath = Path.Combine(snapshot, ManifestName);
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                try
                {
                    var manifest = JsonSerializer.Deserialize<BackupManifest>(
                        File.ReadAllText(manifestPath), Options);
                    if (manifest is not null)
                    {
                        results.Add(new BackupSnapshot(snapshot, manifest));
                    }
                }
                catch (Exception error)
                {
                    AppLog.Warning("备份", $"无法读取快照清单 {manifestPath}：{error.Message}");
                }
            }
        }

        return results.OrderByDescending(snapshot => snapshot.Manifest.CreatedUtc).ToArray();
    }

    /// <summary>把快照还原到设备；还原前会强制创建一次安全快照。</summary>
    public async Task<string> RestoreAsync(
        ActiveDevice device,
        BackupSnapshot snapshot,
        string backupRoot,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!device.CanWrite)
        {
            return $"该设备当前不可写入：{device.RestrictionReason}";
        }

        AppLog.Warning("还原", $"即将把 {snapshot.Name} 还原到 {device.Name}。");

        // 先强制创建安全快照。
        await CreateAsync(device, backupRoot, progress, cancellationToken).ConfigureAwait(false);

        var sourceRoot = snapshot.Directory;
        var restored = 0;
        var index = 0;

        foreach (var entry in snapshot.Manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            var sourceHost = Path.Combine(sourceRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourceHost))
            {
                continue;
            }

            if (!SessionPath.TryCreate(entry.Path, out var target))
            {
                continue;
            }

            try
            {
                var targetHost = device.Session.ToHostPath(target);
                Directory.CreateDirectory(Path.GetDirectoryName(targetHost)!);
                File.Copy(sourceHost, targetHost, overwrite: true);
                restored++;
                progress?.Report(new BackupProgress("正在还原", index, snapshot.FileCount, entry.Path));
            }
            catch (Exception error)
            {
                AppLog.Error("还原", $"还原失败：{entry.Path}", error);
            }
        }

        var message = $"已还原 {restored} 个文件。请断开并重新连接 iPod 以刷新缓存。";
        AppLog.Info("还原", message);
        progress?.Report(new BackupProgress("还原完成", snapshot.FileCount, snapshot.FileCount, string.Empty));
        return message;
    }

    /// <summary>删除一个备份快照。</summary>
    public bool Delete(BackupSnapshot snapshot)
    {
        try
        {
            Directory.Delete(snapshot.Directory, recursive: true);
            AppLog.Info("备份", $"已删除快照：{snapshot.Name}");
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error("备份", "删除快照失败", error);
            return false;
        }
    }

    private static List<string> EnumerateDeviceFiles(ActiveDevice device)
    {
        var files = new List<string>();
        foreach (var root in ManagedRoots)
        {
            if (!SessionPath.TryCreate(root, out var path))
            {
                continue;
            }

            foreach (var file in device.Session.EnumerateFiles(path, recursive: true))
            {
                // 事务暂存与日志不属于用户数据，不进入备份。
                var relative = file.Relative;
                if (relative.Contains("/.iopenpod-staging/", StringComparison.Ordinal) ||
                    relative.Contains("/.iopenpod-backup/", StringComparison.Ordinal) ||
                    relative.Contains("/.iopenpod-journal/", StringComparison.Ordinal))
                {
                    continue;
                }

                files.Add(relative);
            }
        }

        return files;
    }

    private static async Task<string> ComputeHashAsync(string hostPath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            hostPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.Length == 0 ? "iPod" : builder.ToString();
    }

    private static void ApplyRetention(string backupRoot, string deviceName)
    {
        try
        {
            var directory = Path.Combine(backupRoot, Sanitize(deviceName));
            if (!Directory.Exists(directory))
            {
                return;
            }

            var snapshots = Directory.EnumerateDirectories(directory)
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .Skip(20)
                .ToArray();

            foreach (var old in snapshots)
            {
                Directory.Delete(old, recursive: true);
                AppLog.Info("备份", $"按保留策略移除旧快照：{Path.GetFileName(old)}");
            }
        }
        catch (Exception error)
        {
            AppLog.Warning("备份", $"清理旧快照失败：{error.Message}");
        }
    }
}
