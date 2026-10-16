using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IOpenPod.App.Devices;
using IOpenPod.App.Library;
using IOpenPod.App.Logging;
using IOpenPod.App.Media;
using IOpenPod.App.Rockbox;
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
    private readonly MediaTranscodeOptions _transcodeOptions;
    private readonly MediaTranscoder _transcoder;

    public SyncEngine(ActiveDevice device, AppSettings settings)
    {
        _device = device;
        _settings = settings;

        _transcodeOptions = new MediaTranscodeOptions
        {
            FfmpegDirectory = settings.FfmpegDirectory,
            AudioFormat = settings.TranscodeFormat,
            AudioBitrateKbps = settings.TranscodeBitrateKbps,
            AllowAudioConversion = settings.EnableAudioConversion,
            AllowVideoConversion = settings.EnableVideoConversion,
            VideoBitrateKbps = settings.VideoBitrateKbps,
            VideoMaxWidth = settings.VideoMaxWidth,
            VideoMaxHeight = settings.VideoMaxHeight,
            VideoMaxFps = device.Profile?.Capabilities.Video.MaxFps is > 0 and var fps ? fps : 30,
            WorkDirectory = Path.Combine(settings.EffectiveWorkDirectory, "transcode"),
        };

        _transcoder = new MediaTranscoder(
            new FfmpegTools(FfmpegTools.Locate(settings.FfmpegDirectory)),
            _transcodeOptions.WorkDirectory!);
    }

    /// <summary>本次同步可用的媒体工具说明。</summary>
    public string MediaToolStatus => _transcoder.Availability;

    /// <summary>设备视频能力说明。</summary>
    public string VideoCapability => DeviceMediaPolicy.DescribeVideoCapability(_device.Profile);

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
                var item = new HostMediaItem(file, tags with { SizeBytes = info.Length });

                // 只对「可能是视频」或「容器不是设备原生格式」的文件做精确探测，
                // 避免对成千上万个普通 MP3 逐个调用 ffprobe。
                if (_transcoder.IsAvailable && DeviceMediaPolicy.ShouldProbe(file, item.Tags))
                {
                    var probe = await _transcoder.ProbeAsync(file, cancellationToken).ConfigureAwait(false);
                    item = item with
                    {
                        Probe = probe,
                        Conversion = DeviceMediaPolicy.Decide(file, item.Tags, probe),
                    };
                }

                bag.Add(item);

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

    /// <summary>生成宿主条目的界面说明，必要时附带转码提示。</summary>
    private static string DescribeHost(HostMediaItem item)
    {
        var basic = $"{item.Tags.Artist} · {item.Tags.Album}";
        if (item.Probe is { Success: true } probe && probe.Video is { } video)
        {
            basic = $"{basic} · {video.Width}×{video.Height} {video.CodecName}" +
                    (probe.Duration.TotalSeconds > 0 ? $" · {probe.Duration:hh\\:mm\\:ss}" : string.Empty);
        }

        return item.Conversion is { RequiresWork: true } decision
            ? $"{basic} ｜ 需要转码：{decision.Reason}"
            : basic;
    }

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
                    DescribeHost(item)));
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

        // 可选：在同步前把 Rockbox 系统文件装到设备上（只装系统文件，可逆）。
        var rockboxNote = await TryAutoInstallRockboxAsync(progress, cancellationToken).ConfigureAwait(false);
        if (rockboxNote is not null)
        {
            failures.Add(rockboxNote);
        }

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

                            var prepared = await PrepareMediaAsync(item, progress, cancellationToken)
                                .ConfigureAwait(false);
                            if (prepared is null)
                            {
                                failures.Add($"{change.Title}：{change.ConversionNote}");
                                break;
                            }

                            try
                            {
                                await transaction.StageFromFileAsync(target, prepared, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            finally
                            {
                                CleanUpPrepared(prepared, item.HostPath);
                            }

                            var track = library.Database.AddTrack();
                            ApplyTags(track, item);
                            track.Location = target.Relative.Replace('/', ':');
                            track.SizeBytes = (uint)Math.Min(new FileInfo(item.HostPath).Length, uint.MaxValue);
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
                                item.HostPath, new FileInfo(item.HostPath).Length,
                                File.GetLastWriteTimeUtc(item.HostPath),
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

                            var prepared = await PrepareMediaAsync(item, progress, cancellationToken)
                                .ConfigureAwait(false);
                            if (prepared is null)
                            {
                                failures.Add($"{change.Title}：{change.ConversionNote}");
                                break;
                            }

                            try
                            {
                                await transaction.StageFromFileAsync(target, prepared, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            finally
                            {
                                CleanUpPrepared(prepared, item.HostPath);
                            }

                            ApplyTags(track, item);
                            track.SizeBytes = (uint)Math.Min(new FileInfo(item.HostPath).Length, uint.MaxValue);
                            track.LastModified = UnixSeconds(File.GetLastWriteTimeUtc(item.HostPath));

                            var artworkId = artworkWriter.AttachCover(transaction, track, item.Tags.CoverImage);
                            if (artworkId is not null)
                            {
                                track.ArtworkIdRef = artworkId.Value;
                                track.ArtworkCount = 1;
                            }

                            helper[item.HostPath] = new SyncHelperEntry(
                                item.HostPath, new FileInfo(item.HostPath).Length,
                                File.GetLastWriteTimeUtc(item.HostPath),
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

                // 提交成功后，按用户设置把审核过的元数据写回宿主源文件。
                var tagNotes = await WriteBackTagsAsync(changes, cancellationToken).ConfigureAwait(false);
                failures.AddRange(tagNotes);

                var message =
                    $"同步完成：新增 {added} 项，替换 {replaced} 项，更新信息 {metadataUpdated} 项，移除 {removed} 项。";
                if (tagNotes.Count > 0 && _settings.WriteTagsToSourceFiles)
                {
                    message += $"（标签写回有 {tagNotes.Count} 条提示）";
                }

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

    /// <summary>
    /// 同步开始时按设置自动安装 Rockbox 的系统文件（可逆，不触碰固件）。
    /// 只在设备上还没有 Rockbox、且机型受支持时执行；返回给用户的一行说明。
    /// </summary>
    private async Task<string?> TryAutoInstallRockboxAsync(
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_settings.AutoInstallRockboxOnSync)
        {
            return null;
        }

        var target = RockboxTargets.Resolve(_device.Profile);
        if (target is null || !target.Supported)
        {
            return null;
        }

        try
        {
            var installer = new RockboxInstaller(
                Path.Combine(_settings.EffectiveWorkDirectory, "rockbox"));

            var status = await installer.InspectAsync(_device.MountPoint, cancellationToken).ConfigureAwait(false);
            if (status.SystemInstalled)
            {
                return null;
            }

            progress?.Report(new SyncProgress("正在安装 Rockbox 系统文件", 0, 0, target.Name));
            AppLog.Info("Rockbox", $"按设置自动安装 Rockbox 系统文件（目标 {target.Name}）。");

            var options = new RockboxOptions
            {
                MirrorBaseUrl = _settings.RockboxMirrorUrl,
                Channel = _settings.RockboxChannel,
                ReleaseVersion = _settings.RockboxReleaseVersion,
                Mode = RockboxInstallMode.SystemOnly,
                BackupExistingInstallation = true,
                BackupFirmwarePartition = false,
                BackupAppleFirmware = false,
                KeepAppleFirmwareDualBoot = _settings.RockboxKeepAppleFirmware,
            };

            var result = await installer.RunAsync(_device.MountPoint, target, options, null, cancellationToken)
                .ConfigureAwait(false);

            return result.Success
                ? $"已自动在设备上安装 Rockbox 系统文件（目标 {target.Name}）。"
                : $"自动安装 Rockbox 失败：{result.Message}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            AppLog.Error("Rockbox", "自动安装失败", error);
            return $"自动安装 Rockbox 时出错：{error.Message}";
        }
    }

    /// <summary>
    /// 把设备上刚刚提交的元数据写回宿主源文件。
    ///
    /// 这是可选功能（设置中的「标签写回源文件」）。写入按源文件格式支持多少字段就写多少，
    /// 无法表达的字段由 <see cref="MediaTagWriter"/> 在结果里列出。任何失败都只记入提示，
    /// 绝不影响已经提交的同步结果。
    /// </summary>
    private async Task<List<string>> WriteBackTagsAsync(
        IReadOnlyList<SyncChange> changes,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();

        if (!_settings.WriteTagsToSourceFiles)
        {
            return notes;
        }

        var targets = changes
            .Where(change => change.Host is not null && change.Track is not null &&
                             change.Kind is SyncChangeKind.Add or SyncChangeKind.Replace or SyncChangeKind.UpdateMetadata)
            .ToArray();

        if (targets.Length == 0)
        {
            return notes;
        }

        AppLog.Info("标签写回", $"开始把 {targets.Length} 个文件的元数据写回源文件。");

        foreach (var change in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = change.Host!;
            var track = change.Track!;

            if (!File.Exists(item.HostPath))
            {
                notes.Add($"{Path.GetFileName(item.HostPath)}：源文件已不存在，跳过写回。");
                continue;
            }

            try
            {
                var request = BuildTagWriteRequest(track, item);
                var outcome = await MediaTagWriter.WriteAsync(
                    item.HostPath, request, _settings.TagWriteCreateBackup, cancellationToken)
                    .ConfigureAwait(false);

                if (!outcome.Success)
                {
                    notes.Add($"{Path.GetFileName(item.HostPath)}：写回失败 —— {outcome.Message}");
                    continue;
                }

                if (!outcome.Changed)
                {
                    continue;
                }

                AppLog.Info("标签写回",
                    $"{Path.GetFileName(item.HostPath)}：写入 {outcome.WrittenFields.Count} 个字段" +
                    (outcome.UnsupportedFields.Count > 0
                        ? $"，{outcome.UnsupportedFields.Count} 个字段该格式不支持"
                        : string.Empty) + "。");

                if (outcome.UnsupportedFields.Count > 0)
                {
                    notes.Add($"{Path.GetFileName(item.HostPath)}：该格式不支持 " +
                              string.Join('、', outcome.UnsupportedFields) + "。");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                AppLog.Error("标签写回", $"写回失败：{item.HostPath}", error);
                notes.Add($"{Path.GetFileName(item.HostPath)}：写回异常 —— {error.Message}");
            }
        }

        return notes;
    }

    /// <summary>把设备曲目的最终元数据转换成本地文件标签写入请求。</summary>
    private static TagWriteRequest BuildTagWriteRequest(TrackRecord track, HostMediaItem item) => new()
    {
        Title = track.Title,
        Artist = track.Artist,
        AlbumArtist = track.AlbumArtist,
        Album = track.Album,
        Genre = track.Genre,
        Composer = track.Composer,
        Comment = track.Comment,
        Grouping = track.Grouping,
        Lyrics = track.Lyrics.Length > 0 ? track.Lyrics : null,
        TrackNumber = track.TrackNumber > 0 ? track.TrackNumber : null,
        TotalTracks = track.TotalTracks > 0 ? track.TotalTracks : null,
        DiscNumber = track.DiscNumber > 0 ? track.DiscNumber : null,
        TotalDiscs = track.TotalDiscs > 0 ? track.TotalDiscs : null,
        Year = track.Year > 0 ? track.Year : null,
        Bpm = track.Bpm > 0 ? track.Bpm : null,
        IsCompilation = track.IsCompilation,
        CoverImage = item.Tags.CoverImage,
        CoverMimeType = item.Tags.CoverMimeType,
    };

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

    /// <summary>
    /// 准备要写入设备的媒体文件：需要转码时先转码，返回临时文件路径；
    /// 不需要转码时直接返回原路径；失败时返回 <c>null</c>。
    /// </summary>
    private async Task<string?> PrepareMediaAsync(
        HostMediaItem item,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!item.NeedsConversion || item.Conversion is null)
        {
            return item.HostPath;
        }

        var decision = item.Conversion;
        var report = new Progress<TranscodeProgress>(value =>
            progress?.Report(new SyncProgress("正在转码", 0, 0, value.Display)));

        try
        {
            var result = await _transcoder.ConvertAsync(
                item.HostPath, decision, _transcodeOptions, _device.Profile, report, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success || result.OutputPath is null)
            {
                AppLog.Warning("同步", $"转码失败，已跳过：{item.HostPath}（{result.Message}）");
                return null;
            }

            return result.OutputPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            AppLog.Error("同步", $"转码异常，已跳过：{item.HostPath}", error);
            return null;
        }
    }

    private static void CleanUpPrepared(string prepared, string original)
    {
        if (string.Equals(prepared, original, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (File.Exists(prepared))
            {
                File.Delete(prepared);
            }
        }
        catch (Exception)
        {
            // 临时文件清理失败不影响同步结果。
        }
    }

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
