using System.Text.Json;
using System.Text.Json.Serialization;

namespace IOpenPod.Storage;

/// <summary>在设备上发现的一个未完成写入事务。</summary>
public sealed record PendingRecovery(
    string TransactionId,
    StorageJournalState State,
    DateTimeOffset CreatedUtc,
    int EntryCount,
    IReadOnlyList<string> Paths);

/// <summary>
/// 未完成事务的发现与恢复。
/// 恢复完全由设备上的日志推导，绝不写入宿主设置。
/// </summary>
public static class StorageRecovery
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>列出设备上全部尚未完成恢复的事务。</summary>
    public static IReadOnlyList<PendingRecovery> Scan(FilesystemSession session)
    {
        var results = new List<PendingRecovery>();
        var folder = SessionPath.Create(StorageTransaction.JournalFolder);
        var host = session.ToHostPath(folder);

        if (!Directory.Exists(host))
        {
            return results;
        }

        foreach (var file in Directory.EnumerateFiles(host, "*.json"))
        {
            try
            {
                var journal = JsonSerializer.Deserialize<StorageJournal>(File.ReadAllText(file), Options);
                if (journal is null || journal.State == StorageJournalState.Committed)
                {
                    continue;
                }

                results.Add(new PendingRecovery(
                    journal.TransactionId,
                    journal.State,
                    journal.CreatedUtc,
                    journal.Entries.Count,
                    journal.Entries.Select(entry => entry.RelativePath).ToArray()));
            }
            catch (Exception)
            {
                // 无法解析的日志保持原样，不视为可恢复事务。
            }
        }

        return results;
    }

    /// <summary>恢复指定的事务；返回是否成功。</summary>
    public static bool Restore(FilesystemSession session, string transactionId)
    {
        session.ThrowIfUnusable();
        var journalPath = Path.Combine(
            session.ToHostPath(SessionPath.Create(StorageTransaction.JournalFolder)),
            transactionId + ".json");

        if (!File.Exists(journalPath))
        {
            return false;
        }

        try
        {
            var journal = JsonSerializer.Deserialize<StorageJournal>(File.ReadAllText(journalPath), Options);
            if (journal is null)
            {
                return false;
            }

            foreach (var entry in journal.Entries)
            {
                var targetHost = session.ToHostPath(SessionPath.Create(entry.RelativePath));
                var backupHost = Path.Combine(
                    session.ToHostPath(SessionPath.Create(StorageTransaction.BackupFolder)),
                    journal.TransactionId,
                    entry.BackupName);

                if (File.Exists(backupHost))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetHost)!);
                    File.Move(backupHost, targetHost, overwrite: true);
                }
            }

            Discard(session, transactionId);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>放弃恢复：清除暂存、备份与日志，保留设备当前内容。</summary>
    public static bool Discard(FilesystemSession session, string transactionId)
    {
        try
        {
            var staging = Path.Combine(
                session.ToHostPath(SessionPath.Create(StorageTransaction.StagingFolder)), transactionId);
            var backup = Path.Combine(
                session.ToHostPath(SessionPath.Create(StorageTransaction.BackupFolder)), transactionId);

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            var journal = Path.Combine(
                session.ToHostPath(SessionPath.Create(StorageTransaction.JournalFolder)),
                transactionId + ".json");
            if (File.Exists(journal))
            {
                File.Delete(journal);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>清理设备上所有遗留的空暂存目录与已提交日志。</summary>
    public static int CleanupCompleted(FilesystemSession session)
    {
        var removed = 0;
        try
        {
            var host = session.ToHostPath(SessionPath.Create(StorageTransaction.JournalFolder));
            if (!Directory.Exists(host))
            {
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(host, "*.json"))
            {
                var journal = JsonSerializer.Deserialize<StorageJournal>(File.ReadAllText(file), Options);
                if (journal is null || journal.State != StorageJournalState.Committed)
                {
                    continue;
                }

                if (Discard(session, journal.TransactionId))
                {
                    removed++;
                }
            }
        }
        catch (Exception)
        {
            // 清理失败不影响主流程。
        }

        return removed;
    }
}
