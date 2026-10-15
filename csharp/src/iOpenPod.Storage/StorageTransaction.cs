using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IOpenPod.Storage;

/// <summary>事务中单个条目的动作类型。</summary>
public enum StorageAction
{
    Write,
    Delete,
}

/// <summary>事务中单个条目的持久化记录。</summary>
public sealed record StorageJournalEntry(
    string RelativePath,
    StorageAction Action,
    string StagedName,
    string BackupName,
    long Size,
    string Sha256);

/// <summary>事务日志的持久化状态。</summary>
public enum StorageJournalState
{
    Prepared,
    Publishing,
    Committed,
}

/// <summary>磁盘上的事务日志。</summary>
public sealed record StorageJournal(
    string TransactionId,
    string VolumeSerial,
    string MountPoint,
    DateTimeOffset CreatedUtc,
    StorageJournalState State,
    IReadOnlyList<StorageJournalEntry> Entries);

/// <summary>事务提交后的用户可见结果。</summary>
public sealed record StorageTransactionResult(
    string TransactionId,
    int Written,
    int Deleted,
    long BytesWritten,
    string? CleanupWarning);

/// <summary>
/// 可恢复的设备写入事务。
///
/// 先暂存全部新内容，再一次性发布写入，最后执行删除；每个被替换或删除的
/// 原文件都先移动到备份区并写入日志，因此中断后可以明确恢复。
/// </summary>
public sealed class StorageTransaction : IAsyncDisposable
{
    internal const string ControlRoot = "iPod_Control/iOpenPod";
    internal const string StagingFolder = ControlRoot + "/.iopenpod-staging";
    internal const string BackupFolder = ControlRoot + "/.iopenpod-backup";
    internal const string JournalFolder = ControlRoot + "/.iopenpod-journal";

    private static readonly JsonSerializerOptions JournalOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly FilesystemSession _session;
    private readonly List<StorageJournalEntry> _writes = new();
    private readonly List<StorageJournalEntry> _deletions = new();
    private bool _committed;
    private bool _disposed;

    internal StorageTransaction(FilesystemSession session)
    {
        _session = session;
        Id = Guid.NewGuid().ToString("N")[..16];
    }

    public string Id { get; }

    public IReadOnlyList<StorageJournalEntry> Writes => _writes;

    public IReadOnlyList<StorageJournalEntry> Deletions => _deletions;

    public int PendingCount => _writes.Count + _deletions.Count;

    /// <summary>暂存一段内存内容，提交时发布到设备。</summary>
    public void Stage(SessionPath path, ReadOnlySpan<byte> content)
    {
        EnsureMutable();
        var bytes = content.ToArray();
        var index = _writes.Count;
        var stagedHost = StagedHostPath(index, path.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedHost)!);
        File.WriteAllBytes(stagedHost, bytes);

        _deletions.RemoveAll(entry =>
            string.Equals(entry.RelativePath, path.Relative, StringComparison.Ordinal));
        _writes.RemoveAll(entry =>
            string.Equals(entry.RelativePath, path.Relative, StringComparison.Ordinal));

        _writes.Add(new StorageJournalEntry(
            path.Relative,
            StorageAction.Write,
            Path.GetFileName(stagedHost),
            BackupNameFor(index, path.FileName),
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes))));
    }

    /// <summary>暂存一个宿主文件的内容。</summary>
    public async Task StageFromFileAsync(
        SessionPath path,
        string hostSourcePath,
        CancellationToken cancellationToken = default)
    {
        EnsureMutable();
        await using var source = new FileStream(
            hostSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        await StageFromStreamAsync(path, source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>暂存一个流的内容。</summary>
    public async Task StageFromStreamAsync(
        SessionPath path,
        Stream source,
        CancellationToken cancellationToken = default)
    {
        EnsureMutable();
        var index = _writes.Count;
        var stagedHost = StagedHostPath(index, path.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedHost)!);

        await using (var destination = new FileStream(
                         stagedHost, FileMode.Create, FileAccess.Write, FileShare.None,
                         1 << 20, useAsync: true))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        _deletions.RemoveAll(entry =>
            string.Equals(entry.RelativePath, path.Relative, StringComparison.Ordinal));
        _writes.RemoveAll(entry =>
            string.Equals(entry.RelativePath, path.Relative, StringComparison.Ordinal));

        var info = new FileInfo(stagedHost);
        _writes.Add(new StorageJournalEntry(
            path.Relative,
            StorageAction.Write,
            Path.GetFileName(stagedHost),
            BackupNameFor(index, path.FileName),
            info.Length,
            await ComputeFileHashAsync(stagedHost, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>登记一个待删除的设备文件。删除在全部写入发布之后执行。</summary>
    public void MarkForDeletion(SessionPath path)
    {
        EnsureMutable();
        _writes.RemoveAll(entry =>
            string.Equals(entry.RelativePath, path.Relative, StringComparison.Ordinal));

        if (_deletions.Any(entry =>
                string.Equals(entry.RelativePath, path.Relative, StringComparison.Ordinal)))
        {
            return;
        }

        var index = _deletions.Count;
        _deletions.Add(new StorageJournalEntry(
            path.Relative,
            StorageAction.Delete,
            string.Empty,
            BackupNameFor(index, path.FileName),
            0,
            string.Empty));
    }

    /// <summary>提交事务：发布写入、执行删除、校验并清理。</summary>
    public async Task<StorageTransactionResult> CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureMutable();
        _session.ThrowIfUnusable();

        if (PendingCount == 0)
        {
            _committed = true;
            return new StorageTransactionResult(Id, 0, 0, 0, null);
        }

        var allEntries = _writes.Concat(_deletions).ToArray();
        var journal = BuildJournal(allEntries, StorageJournalState.Prepared);
        await WriteJournalAsync(journal, cancellationToken).ConfigureAwait(false);

        var written = 0;
        var deleted = 0;
        long bytesWritten = 0;

        try
        {
            journal = journal with { State = StorageJournalState.Publishing };
            await WriteJournalAsync(journal, cancellationToken).ConfigureAwait(false);

            foreach (var entry in _writes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stagedHost = Path.Combine(
                    _session.ToHostPath(SessionPath.Create(StagingFolder)), Id, entry.StagedName);
                var targetPath = SessionPath.Create(entry.RelativePath);
                var targetHost = _session.ToHostPath(targetPath);

                if (!File.Exists(stagedHost))
                {
                    throw new StorageException($"暂存文件缺失，无法发布：{entry.RelativePath}");
                }

                var actual = await ComputeFileHashAsync(stagedHost, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new StorageException($"暂存内容校验失败，已中止写入：{entry.RelativePath}");
                }

                var directory = Path.GetDirectoryName(targetHost);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(targetHost))
                {
                    var backupHost = BackupHostPath(entry.BackupName);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupHost)!);
                    File.Move(targetHost, backupHost, overwrite: true);
                }

                File.Move(stagedHost, targetHost, overwrite: true);
                written++;
                bytesWritten += entry.Size;
            }

            foreach (var entry in _deletions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetHost = _session.ToHostPath(SessionPath.Create(entry.RelativePath));
                if (!File.Exists(targetHost))
                {
                    continue;
                }

                var backupHost = BackupHostPath(entry.BackupName);
                Directory.CreateDirectory(Path.GetDirectoryName(backupHost)!);
                File.Move(targetHost, backupHost, overwrite: true);
                deleted++;
            }

            journal = journal with { State = StorageJournalState.Committed };
            await WriteJournalAsync(journal, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryRestoreAsync().ConfigureAwait(false);
            throw;
        }

        _committed = true;
        var warning = Cleanup();
        return new StorageTransactionResult(Id, written, deleted, bytesWritten, warning);
    }

    /// <summary>按日志把备份内容恢复回原位。</summary>
    public async Task<bool> TryRestoreAsync()
    {
        var journalPath = JournalHostPath();
        if (!File.Exists(journalPath))
        {
            return false;
        }

        try
        {
            var json = await File.ReadAllTextAsync(journalPath).ConfigureAwait(false);
            var journal = JsonSerializer.Deserialize<StorageJournal>(json, JournalOptions);
            if (journal is null)
            {
                return false;
            }

            foreach (var entry in journal.Entries)
            {
                var targetHost = _session.ToHostPath(SessionPath.Create(entry.RelativePath));
                var backupHost = BackupHostPath(entry.BackupName);
                if (File.Exists(backupHost))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetHost)!);
                    File.Move(backupHost, targetHost, overwrite: true);
                }
            }

            Cleanup();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>删除暂存、备份与日志。返回清理失败的警告文本。</summary>
    public string? Cleanup()
    {
        var warnings = new List<string>();

        TryDeleteDirectory(Path.Combine(
            _session.ToHostPath(SessionPath.Create(StagingFolder)), Id), warnings);

        TryDeleteDirectory(Path.Combine(
            _session.ToHostPath(SessionPath.Create(BackupFolder)), Id), warnings);

        var journalPath = JournalHostPath();
        try
        {
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }
        }
        catch (Exception)
        {
            warnings.Add("事务日志删除失败。");
        }

        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    private static void TryDeleteDirectory(string path, List<string> warnings)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            warnings.Add($"暂存目录删除失败：{path}");
        }
    }

    private StorageJournal BuildJournal(IReadOnlyList<StorageJournalEntry> entries, StorageJournalState state) =>
        new(
            Id,
            _session.Identity.Serial,
            _session.MountPoint,
            DateTimeOffset.UtcNow,
            state,
            entries);

    private async Task WriteJournalAsync(StorageJournal journal, CancellationToken cancellationToken)
    {
        var journalHost = JournalHostPath();
        Directory.CreateDirectory(Path.GetDirectoryName(journalHost)!);
        var json = JsonSerializer.Serialize(journal, JournalOptions);
        var temporary = journalHost + ".tmp";
        await File.WriteAllTextAsync(temporary, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, journalHost, overwrite: true);
    }

    private string JournalHostPath() =>
        Path.Combine(_session.ToHostPath(SessionPath.Create(JournalFolder)), Id + ".json");

    private string StagedHostPath(int index, string fileName) =>
        Path.Combine(
            _session.ToHostPath(SessionPath.Create(StagingFolder)),
            Id,
            index.ToString("D4") + "_" + Sanitize(fileName));

    private string BackupHostPath(string name) =>
        Path.Combine(_session.ToHostPath(SessionPath.Create(BackupFolder)), Id, name);

    private static string BackupNameFor(int index, string fileName) =>
        index.ToString("D4") + "_" + Sanitize(fileName);

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.Length == 0 ? "file" : builder.ToString();
    }

    private static async Task<string> ComputeFileHashAsync(string hostPath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            hostPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private void EnsureMutable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
        {
            throw new StorageException("事务已经提交，不能继续修改。");
        }

        _session.ThrowIfUnusable();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_committed)
        {
            await TryRestoreAsync().ConfigureAwait(false);
            Cleanup();
        }
    }
}
