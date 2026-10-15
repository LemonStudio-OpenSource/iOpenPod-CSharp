using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IOpenPod.App.Devices;
using IOpenPod.App.Library;
using IOpenPod.App.Logging;
using IOpenPod.App.Media;
using IOpenPod.App.Settings;
using IOpenPod.App.Sync;
using IOpenPod.IPodDb;
using IOpenPod.IPodDb.Artwork;
using IOpenPod.Storage;

namespace IOpenPod.App.Sync;

/// <summary>同步辅助记录：把宿主文件与设备曲目对应起来，用于识别“已同步且未变化”。</summary>
public sealed record SyncHelperEntry(string HostPath, long Size, DateTimeOffset ModifiedUtc, uint TrackId, string DevicePath);

/// <summary>
/// 同步引擎。
///
/// 流程与产品约定一致：扫描 → 计划 → 用户确认 → 执行 → 校验 → 提交。
/// 所有设备写入都通过可恢复的存储事务完成。
/// </summary>
public sealed class SyncEngine
{
    private const string HelperPath = "iPod_Control/iOpenPod/library-sync-helper.json";
    private const long MaxScanFileBytes = 4L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly ActiveDevice _device;
    private readonly AppSettings _settings;

    public SyncEngine(ActiveDevice device, AppSettings settings)
    {
        _device = device;
        _settings = settings;
    }

    // ---- 扫描 ----

    /// <summary>扫描宿主文件夹，返回全部支持的媒体文件。</summary>
    public async Task<IReadOnlyList<HostMediaItem>> ScanHostAsync(
        IEnumerable<string> folders,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = new List<string>();
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                AppLog.Warning("扫描", $"跳过不存在的文件夹：{folder}");
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (MediaTagReader.IsSupportedExtension(file))
                    {
                        files.Add(file);
                    }
                }
            }
            catch (Exception error)
            {
                AppLog.Warning("扫描", $"枚举文件夹失败：{folder}（{error.Message}）");
            }
        }

        AppLog.Info("扫描", $"在 {folders.Count()} 个文件夹中发现 {files.Count} 个媒体文件，开始读取标签…");

        var results = new List<HostMediaItem>(files.Count);
        var index = 0;
        var gate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));
        var bag = new System.Collections.Concurrent.ConcurrentBag<HostMediaItem>();

        var tasks = files.Select(async file =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxScanFileBytes)
                {
                    return;
                }

                var tags = await Task.Run(() => MediaTagReader.Read(file), cancellationToken)
                    .ConfigureAwait(false);
                bag.Add(new HostMediaItem(file, tags with { SizeBytes = info.Length }));

                var done = Interlocked.Increment(ref index);
                if (done % 25 == 0 || done == files.Count)
                {
                    progress?.Report(new SyncProgress("正在读取媒体信息", done, files.Count, Path.GetFileName(file)));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                AppLog.Warning("扫描", $"读取标签失败：{file}（{error.Message}）");
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        gate.Dispose();

        var ordered = bag.OrderBy(item => item.Tags.Artist, StringComparer.CurrentCulture)
            .ThenBy(item => item.Tags.Album, StringComparer.CurrentCulture)
            .ThenBy(item => item.Tags.TrackNumber)
            .ThenBy(item => item.Tags.Title, StringComparer.CurrentCulture)
            .ToArray();

        AppLog.Info("扫描", $"扫描完成：{ordered.Length} 个媒体文件可用。");
        return ordered;
    }

    // ---- 计划 ----

    /// <summary>建立同步计划。</summary>
    public SyncPlan BuildPlan(IReadOnlyList<HostMediaItem> hostItems)
    {
        var tracks = _device.Database.Tracks;
        var helper = LoadHelper();

        var deviceByKey = new Dictionary<string, TrackRecord>(StringComparer.Ordinal);
        var deviceByHostPath = new Dictionary<string, TrackRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            deviceByKey.TryAdd(HostMediaItem.KeyForTrack(track), track);
        }

        foreach (var entry in helper.Values)
        {
            var track = _device.Database.TrackById(entry.TrackId);
            if (track is not null)
            {
                deviceByHostPath.TryAdd(entry.HostPath, track);
            }
        }

        var changes = new List<SyncChange>();
        var matchedTrackIds = new HashSet<uint>();
        var unchanged = 0;

        foreach (var item in hostItems)
        {
            var key = item.MatchKey;

            // 1) 通过上次同步的宿主路径精确匹配
            TrackRecord? track = null;
            HostMediaItem? hostForTrack = item;
            if (deviceByHostPath.TryGetValue(item.HostPath, out var byPath))
            {
                track = byPath;
            }
            else if (deviceByKey.TryGetValue(key, out var byKey))
            {
                track = byKey;
            }

            if (track is null)
            {
                changes.Add(new SyncChange(
                    SyncChangeKind.Add,
                    item,
                    null,
                    item.Tags.EffectiveTitle(item.HostPath),
                    $"{item.Tags.Artist} · {item.Tags.Album}"));
                continue;
            }

            matchedTrackIds.Add(track.TrackId);

            var helperEntry = helper.GetValueOrDefault(item.HostPath);
            var fileChanged = helperEntry is null ||
                              helperEntry.Size != item.Tags.SizeBytes ||
                              Math.Abs((helperEntry.ModifiedUtc - File.GetLastWriteTimeUtc(item.HostPath)).TotalSeconds) > 2;

            var metadataChanged =
                track.Title != item.Tags.EffectiveTitle(item.HostPath) ||
                track.Artist != item.Tags.Artist ||
                track.Album != item.Tags.Album ||
                track.Genre != item.Tags.Genre ||
                track.TrackNumber != item.Tags.TrackNumber ||
                track.Year != (item.Tags.Year == 0 ? track.Year : item.Tags.Year);

            if (fileChanged && helperEntry is null)
            {
                // 首次纳管：只更新元数据，不替换已存在的媒体。
                changes.Add(new SyncChange(
                    SyncChangeKind.UpdateMetadata, hostForTrack, track, track.Title, "补齐同步记录与标签"));
            }
            else if (fileChanged)
            {
                changes.Add(new SyncChange(
                    SyncChangeKind.Replace, hostForTrack, track, track.Title, "宿主文件已变化"));
            }
            else if (metadataChanged)
            {
                changes.Add(new SyncChange(
                    SyncChangeKind.UpdateMetadata, hostForTrack, track, track.Title, "标签与设备不一致"));
            }
            else
            {
                unchanged++;
            }
        }

        // 设备上存在但宿主已消失的曲目：列为移除候选（默认不勾选）。
        foreach (var track in tracks)
        {
            if (matchedTrackIds.Contains(track.TrackId))
            {
                continue;
            }

            var helperTracked = helper.Values.Any(entry => entry.TrackId == track.TrackId);
            if (!helperTracked && hostItems.Count == 0)
            {
                continue;
            }

            if (helperTracked)
            {
                changes.Add(new SyncChange(
                    SyncChangeKind.Remove, null, track, track.Title, "宿主文件已不存在"));
            }
        }

        var plan = new SyncPlan(changes, unchanged, hostItems.Count, tracks.Count);
        AppLog.Info("同步", $"同步计划：{plan.Summary}");
        return plan;
    }

    // ---- 执行 ----

    /// <summary>执行同步计划。</summary>
    public async Task<SyncOutcome> ExecuteAsync(
        LibrarySession library,
        SyncPlan plan,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_device.CanWrite)
        {
            return new SyncOutcome(false, _device.RestrictionReason, 0, 0, 0, 0, 0, []);
        }

        var changes = plan.Changes.ToArray();
        if (changes.Length == 0)
        {
            return new SyncOutcome(true, "没有需要同步的变更。", 0, 0, 0, 0, 0, []);
        }

        var failures = new List<string>();
        var helper = LoadHelper();
        var added = 0;
        var replaced = 0;
        var metadataUpdated = 0;
        var removed = 0;
        long bytesCopied = 0;

        var transaction = _device.Session.BeginTransaction();
        var artworkWriter = new ArtworkWriter(_device, library);
        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                var index = 0;
                foreach (var change in changes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    index++;
                    progress?.Report(new SyncProgress("正在准备同步内容", index, changes.Length, change.Title));

                    switch (change.Kind)
                    {
                        case SyncChangeKind.Add:
                        {
                            var item = change.Host!;
                            var target = AllocateDevicePath(item);
                            await transaction.StageFromFileAsync(target, item.HostPath, cancellationToken)
                                .ConfigureAwait(false);

                            var track = library.Database.AddTrack();
                            ApplyTags(track, item);
                            track.Location = target.Relative.Replace('/', ':');
                            track.SizeBytes = (uint)Math.Min(item.Tags.SizeBytes, uint.MaxValue);
                            track.LastModified = UnixSeconds(File.GetLastWriteTimeUtc(item.HostPath));
                            track.DateAdded = IpodNow();
                            track.DbTrackId = library.Database.PersistentIdFor(track.TrackId);

                            var artworkId = artworkWriter.AttachCover(transaction, track, item.Tags.CoverImage);
                            if (artworkId is not null)
                            {
                                track.ArtworkIdRef = artworkId.Value;
                                track.ArtworkCount = 1;
                            }

                            helper[item.HostPath] = new SyncHelperEntry(
                                item.HostPath, item.Tags.SizeBytes, File.GetLastWriteTimeUtc(item.HostPath),
                                track.TrackId, target.Relative);

                            added++;
                            bytesCopied += item.Tags.SizeBytes;
                            break;
                        }

                        case SyncChangeKind.Replace:
                        {
                            var item = change.Host!;
                            var track = change.Track!;
                            var relative = track.RelativePath ?? AllocateDevicePath(item).Relative;
                            if (!SessionPath.TryCreate(relative, out var target))
                            {
                                failures.Add($"{change.Title}：设备路径无效");
                                break;
                            }

                            await transaction.StageFromFileAsync(target, item.HostPath, cancellationToken)
                                .ConfigureAwait(false);
                            ApplyTags(track, item);
                            track.SizeBytes = (uint)Math.Min(item.Tags.SizeBytes, uint.MaxValue);
                            track.LastModified = UnixSeconds(File.GetLastWriteTimeUtc(item.HostPath));

                            var artworkId = artworkWriter.AttachCover(transaction, track, item.Tags.CoverImage);
                            if (artworkId is not null)
                            {
                                track.ArtworkIdRef = artworkId.Value;
                                track.ArtworkCount = 1;
                            }

                            helper[item.HostPath] = new SyncHelperEntry(
                                item.HostPath, item.Tags.SizeBytes, File.GetLastWriteTimeUtc(item.HostPath),
                                track.TrackId, relative);

                            replaced++;
                            bytesCopied += item.Tags.SizeBytes;
                            break;
                        }

                        case SyncChangeKind.UpdateMetadata:
                        {
                            var item = change.Host!;
                            var track = change.Track!;
                            ApplyTags(track, item);

                            var artworkId = artworkWriter.AttachCover(transaction, track, item.Tags.CoverImage);
                            if (artworkId is not null)
                            {
                                track.ArtworkIdRef = artworkId.Value;
                                track.ArtworkCount = 1;
                            }

                            helper[item.HostPath] = new SyncHelperEntry(
                                item.HostPath, item.Tags.SizeBytes, File.GetLastWriteTimeUtc(item.HostPath),
                                track.TrackId, track.RelativePath ?? string.Empty);

                            metadataUpdated++;
                            break;
                        }

                        case SyncChangeKind.Remove:
                        {
                            var track = change.Track!;
                            library.RemoveTracks([track]);
                            foreach (var stale in helper
                                         .Where(pair => pair.Value.TrackId == track.TrackId)
                                         .Select(pair => pair.Key)
                                         .ToArray())
                            {
                                helper.Remove(stale);
                            }

                            removed++;
                            break;
                        }
                    }
                }

                progress?.Report(new SyncProgress("正在写入设备", changes.Length, changes.Length, string.Empty));

                // 同步辅助记录
                var helperBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(helper, Options));
                transaction.Stage(SessionPath.Create(HelperPath), helperBytes);

                // 数据库本体
                await StageDatabaseAsync(library, transaction, cancellationToken).ConfigureAwait(false);

                // 插图文件
                artworkWriter.Flush(transaction);

                var result = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                AppLog.Info("同步", $"同步提交完成：写入 {result.Written} 个文件。");

                var message =
                    $"同步完成：新增 {added} 项，替换 {replaced} 项，更新信息 {metadataUpdated} 项，移除 {removed} 项。";
                progress?.Report(new SyncProgress("同步完成", changes.Length, changes.Length, string.Empty));
                return new SyncOutcome(true, message, added, replaced, metadataUpdated, removed, bytesCopied, failures);
            }
            catch (Exception error)
            {
                AppLog.Error("同步", "同步失败，设备内容已回滚", error);
                return new SyncOutcome(
                    false, $"同步失败：{error.Message}", added, replaced, metadataUpdated, removed, bytesCopied, failures);
            }
        }
    }

    private async Task StageDatabaseAsync(
        LibrarySession library,
        StorageTransaction transaction,
        CancellationToken cancellationToken)
    {
        var logical = library.Database.Serialize();
        var needsCompression = _device.IsCompressed ||
                               (_device.Profile?.Capabilities.Database.SupportsCompressedDatabase ?? false);
        var physical = needsCompression ? Databases.ITunesCdbCodec.Compress(logical, _device.CdbFraming) : logical;
        physical = SignPhysical(physical);

        var targetPath = needsCompression ? DevicePaths.ITunesCdb : DevicePaths.ITunesDb;
        transaction.Stage(SessionPath.Create(targetPath), physical);
        _device.PhysicalDatabaseBytes = physical;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private byte[] SignPhysical(byte[] physical)
    {
        var checksum = _device.Profile?.Capabilities.Database.Checksum ?? DeviceRegistry.DatabaseChecksum.None;
        return checksum switch
        {
            DeviceRegistry.DatabaseChecksum.None => physical,
            DeviceRegistry.DatabaseChecksum.Hash58 =>
                IPodDb.Signing.DatabaseSignature.SignHash58(physical, _device.Guid),
            DeviceRegistry.DatabaseChecksum.HashAb =>
                IPodDb.Signing.DatabaseSignature.SignHashAb(physical, _device.Guid),
            DeviceRegistry.DatabaseChecksum.Hash72 => SignHash72(physical),
            _ => physical,
        };
    }

    private byte[] SignHash72(byte[] physical)
    {
        var (iv, randomPart) = ResolveHash72Material();
        var scheme = _device.Database.HashingScheme is 1 ? (ushort)1 : (ushort)2;
        return IPodDb.Signing.DatabaseSignature.SignHash72(physical, iv, randomPart, scheme);
    }

    private (byte[] Iv, byte[] RandomPart) ResolveHash72Material()
    {
        try
        {
            var path = SessionPath.Create(DevicePaths.HashInfo);
            if (_device.Session.FileExists(path))
            {
                var material = IPodDb.Signing.DatabaseSignature.ParseHashInfo(
                    _device.Session.ReadAllBytes(path, 4096));
                return (material.Iv, material.RandomPart);
            }
        }
        catch (Exception error)
        {
            AppLog.Warning("签名", $"HashInfo 不可用：{error.Message}");
        }

        return IPodDb.Signing.DatabaseSignature.ExtractHash72Material(_device.PhysicalDatabaseBytes);
    }

    // ---- 工具 ----

    private static void ApplyTags(TrackRecord track, HostMediaItem item)
    {
        var tags = item.Tags;
        track.Title = tags.EffectiveTitle(item.HostPath);
        track.Artist = tags.Artist;
        track.AlbumArtist = tags.AlbumArtist;
        track.Album = tags.Album;
        track.Genre = tags.Genre;
        track.Composer = tags.Composer;
        track.Comment = tags.Comment;
        track.Grouping = tags.Grouping;
        track.Lyrics = tags.Lyrics;

        if (tags.TrackNumber > 0)
        {
            track.TrackNumber = tags.TrackNumber;
        }

        if (tags.TotalTracks > 0)
        {
            track.TotalTracks = tags.TotalTracks;
        }

        if (tags.DiscNumber > 0)
        {
            track.DiscNumber = tags.DiscNumber;
        }

        if (tags.TotalDiscs > 0)
        {
            track.TotalDiscs = tags.TotalDiscs;
        }

        if (tags.Year > 0)
        {
            track.Year = tags.Year;
        }

        if (tags.Bpm > 0)
        {
            track.Bpm = (ushort)Math.Min(tags.Bpm, ushort.MaxValue);
        }

        track.IsCompilation = tags.IsCompilation;
        track.LengthMs = (uint)Math.Max(0, tags.Duration.TotalMilliseconds);
        track.Bitrate = tags.BitrateKbps;

        if (tags.SampleRate > 0)
        {
            track.EnsureHeaderField(IPodDb.MhitFields.SampleRate1, 4);
            track.Node.TryWriteU32(IPodDb.MhitFields.SampleRate1, (uint)tags.SampleRate);
            track.Node.TryWriteF32(IPodDb.MhitFields.SampleRate2, tags.SampleRate);
        }

        var extension = Path.GetExtension(item.HostPath).TrimStart('.').ToLowerInvariant();
        if (ITunesDbConstants.FileExtensionCodes.TryGetValue(extension, out var code))
        {
            track.FileType = code;
        }

        track.MediaType = tags.IsVideo ? 2u : 1u;
        if (ITunesDbConstants.AudioFormatFlags.TryGetValue(extension, out var flag))
        {
            track.Node.TryWriteU16(IPodDb.MhitFields.AudioFormatFlag, flag);
        }

        if (tags.IsVideo)
        {
            track.SkipWhenShuffling = true;
            track.RememberPosition = true;
        }
    }

    private SessionPath AllocateDevicePath(HostMediaItem item)
    {
        var directories = _device.Profile?.Capabilities.Database.MusicDirectoryCount ?? 20;
        var extension = Path.GetExtension(item.HostPath).TrimStart('.').ToLowerInvariant();
        if (extension.Length == 0)
        {
            extension = "mp3";
        }

        var used = _device.Database.Tracks
            .Select(track => track.RelativePath)
            .Where(path => path is not null)
            .Select(path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < 64; attempt++)
        {
            var seed = Encoding.UTF8.GetBytes(item.HostPath + attempt);
            var hash = SHA256.HashData(seed);
            var directoryIndex = (hash[0] << 8 | hash[1]) % Math.Max(1, directories);
            var name = new string([.. hash.Skip(2).Take(4).Select(b => "0123456789ABCDEF"[b % 16])]);
            var relative = $"iPod_Control/Music/F{directoryIndex:D2}/{name}.{extension}";
            if (!used.Contains(relative))
            {
                return SessionPath.Create(relative);
            }
        }

        throw new StorageException("无法为新媒体文件分配唯一的设备路径。");
    }

    private Dictionary<string, SyncHelperEntry> LoadHelper()
    {
        try
        {
            var path = SessionPath.Create(HelperPath);
            if (!_device.Session.FileExists(path))
            {
                return new Dictionary<string, SyncHelperEntry>(StringComparer.OrdinalIgnoreCase);
            }

            var json = _device.Session.ReadAllText(path, 64 << 20);
            return JsonSerializer.Deserialize<Dictionary<string, SyncHelperEntry>>(json, Options)
                   ?? new Dictionary<string, SyncHelperEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error)
        {
            AppLog.Warning("同步", $"同步辅助记录无法读取，将重新建立：{error.Message}");
            return new Dictionary<string, SyncHelperEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static uint UnixSeconds(DateTime utc) =>
        (uint)Math.Max(0, new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds());

    private static uint IpodNow() =>
        (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2082844800);

    /// <summary>0..255 的评分等级到 0..5 星的换算。</summary>
    public static int StarsFromRating(byte rating) => (rating + 10) / 20;
}
