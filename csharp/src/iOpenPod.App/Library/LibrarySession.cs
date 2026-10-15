using IOpenPod.App.Databases;
using IOpenPod.App.Devices;
using IOpenPod.App.Logging;
using IOpenPod.App.Settings;
using IOpenPod.DeviceRegistry;
using IOpenPod.IPodDb;
using IOpenPod.IPodDb.Artwork;
using IOpenPod.IPodDb.Signing;
using IOpenPod.Storage;

namespace IOpenPod.App.Library;

/// <summary>一次可提交的曲目元数据修改。为 <c>null</c> 的字段保持原值。</summary>
public sealed record TrackEdit
{
    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? AlbumArtist { get; init; }

    public string? Album { get; init; }

    public string? Genre { get; init; }

    public string? Composer { get; init; }

    public string? Comment { get; init; }

    public string? Grouping { get; init; }

    public string? Lyrics { get; init; }

    public string? SortTitle { get; init; }

    public string? SortArtist { get; init; }

    public string? SortAlbum { get; init; }

    public int? TrackNumber { get; init; }

    public int? TotalTracks { get; init; }

    public int? DiscNumber { get; init; }

    public int? TotalDiscs { get; init; }

    public int? Year { get; init; }

    public int? Bpm { get; init; }

    public int? StarRating { get; init; }

    public bool? IsCompilation { get; init; }

    public bool? SkipWhenShuffling { get; init; }

    public bool? RememberPosition { get; init; }

    public uint? MediaType { get; init; }
}

/// <summary>保存结果。</summary>
public sealed record SaveOutcome(
    bool Success,
    string Message,
    int ChangedTracks,
    long WrittenBytes,
    string? Warning = null);

/// <summary>
/// 一个 iPod 音乐库的编辑会话。
///
/// 所有编辑都直接作用在内存中的无损数据库上；何时写回设备由
/// <see cref="AppSettings.DraftAllChanges"/> 决定（默认自动保存）。
/// </summary>
public sealed class LibrarySession
{
    private readonly ActiveDevice _device;
    private readonly AppSettings _settings;
    private readonly HashSet<uint> _changedTrackIds = [];
    private bool _deviceNameChanged;
    private int _removedTracks;

    public LibrarySession(ActiveDevice device, AppSettings settings)
    {
        _device = device;
        _settings = settings;
        OriginalDeviceName = device.Name;
    }

    public ActiveDevice Device => _device;

    public ITunesDatabase Database => _device.Database;

    public ArtworkDatabase? Artwork => _device.Artwork;

    /// <summary>是否有未保存的修改。</summary>
    public bool IsDirty { get; private set; }

    /// <summary>设备名称在本会话中是否被修改过。</summary>
    public bool DeviceNameChanged => _deviceNameChanged;

    public string OriginalDeviceName { get; private set; }

    /// <summary>本会话中修改过的曲目数量。</summary>
    public int ChangedTrackCount => _changedTrackIds.Count;

    /// <summary>本会话中移除的曲目数量。</summary>
    public int RemovedTrackCount => _removedTracks;

    /// <summary>修改状态发生变化时触发。</summary>
    public event EventHandler? Changed;

    // ---- 编辑操作 ----

    /// <summary>应用一次曲目元数据修改。</summary>
    public void UpdateTrack(TrackRecord track, TrackEdit edit)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(edit);

        if (edit.Title is not null)
        {
            track.Title = edit.Title;
        }

        if (edit.Artist is not null)
        {
            track.Artist = edit.Artist;
        }

        if (edit.AlbumArtist is not null)
        {
            track.AlbumArtist = edit.AlbumArtist;
        }

        if (edit.Album is not null)
        {
            track.Album = edit.Album;
        }

        if (edit.Genre is not null)
        {
            track.Genre = edit.Genre;
        }

        if (edit.Composer is not null)
        {
            track.Composer = edit.Composer;
        }

        if (edit.Comment is not null)
        {
            track.Comment = edit.Comment;
        }

        if (edit.Grouping is not null)
        {
            track.Grouping = edit.Grouping;
        }

        if (edit.Lyrics is not null)
        {
            track.Lyrics = edit.Lyrics;
        }

        if (edit.SortTitle is not null)
        {
            track.SortTitle = edit.SortTitle;
        }

        if (edit.SortArtist is not null)
        {
            track.SortArtist = edit.SortArtist;
        }

        if (edit.SortAlbum is not null)
        {
            track.SortAlbum = edit.SortAlbum;
        }

        if (edit.TrackNumber is { } number)
        {
            track.TrackNumber = number;
        }

        if (edit.TotalTracks is { } totalTracks)
        {
            track.TotalTracks = totalTracks;
        }

        if (edit.DiscNumber is { } discNumber)
        {
            track.DiscNumber = discNumber;
        }

        if (edit.TotalDiscs is { } totalDiscs)
        {
            track.TotalDiscs = totalDiscs;
        }

        if (edit.Year is { } year)
        {
            track.Year = year;
        }

        if (edit.Bpm is { } bpm)
        {
            track.Bpm = (ushort)Math.Clamp(bpm, 0, ushort.MaxValue);
        }

        if (edit.StarRating is { } rating)
        {
            track.StarRating = rating;
        }

        if (edit.IsCompilation is { } compilation)
        {
            track.IsCompilation = compilation;
        }

        if (edit.SkipWhenShuffling is { } skip)
        {
            track.SkipWhenShuffling = skip;
        }

        if (edit.RememberPosition is { } remember)
        {
            track.RememberPosition = remember;
        }

        if (edit.MediaType is { } mediaType)
        {
            track.MediaType = mediaType;
        }

        MarkChanged(track);
    }

    /// <summary>修改设备名称（写入主播放列表名）。</summary>
    public void RenameDevice(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var master = Database.MasterPlaylist;
        if (master is null || master.Name == trimmed)
        {
            return;
        }

        master.Name = trimmed;
        _device.Name = trimmed;
        _deviceNameChanged = true;
        SetDirty();
    }

    /// <summary>创建一个播放列表。</summary>
    public PlaylistRecord CreatePlaylist(string name)
    {
        var playlist = Database.AddPlaylist(name);
        AppLog.Info("音乐库", $"已创建播放列表「{name}」。");
        SetDirty();
        return playlist;
    }

    /// <summary>删除一个播放列表。</summary>
    public bool DeletePlaylist(PlaylistRecord playlist)
    {
        if (playlist.IsMaster)
        {
            AppLog.Warning("音乐库", "主播放列表不能被删除。");
            return false;
        }

        var name = playlist.Name;
        if (!Database.RemovePlaylist(playlist))
        {
            return false;
        }

        AppLog.Info("音乐库", $"已删除播放列表「{name}」。");
        SetDirty();
        return true;
    }

    /// <summary>重命名播放列表。</summary>
    public void RenamePlaylist(PlaylistRecord playlist, string name)
    {
        playlist.Name = name;
        SetDirty();
    }

    /// <summary>把曲目添加到播放列表末尾。</summary>
    public int AddToPlaylist(PlaylistRecord playlist, IEnumerable<TrackRecord> tracks)
    {
        var added = 0;
        foreach (var track in tracks)
        {
            playlist.AddTrack(track, Database.NextPlaylistItemPersistentId());
            added++;
        }

        if (added > 0)
        {
            SetDirty();
        }

        return added;
    }

    /// <summary>把曲目出现位置从播放列表中移除。</summary>
    public int RemoveFromPlaylist(PlaylistRecord playlist, IEnumerable<TrackRecord> tracks)
    {
        var ids = tracks.Select(track => track.TrackId).ToHashSet();
        var removed = 0;
        for (var i = playlist.Node.Children.Count - 1; i >= 0; i--)
        {
            var child = playlist.Node.Children[i];
            if (child.Marker == "mhip" && ids.Contains(child.ReadU32Or(IPodDb.MhipFields.TrackId)))
            {
                playlist.Node.Children.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0)
        {
            SetDirty();
        }

        return removed;
    }

    /// <summary>
    /// 从设备上彻底移除曲目：数据库条目、播放列表引用、插图与媒体文件。
    /// 媒体文件的删除会与数据库写入放在同一个可恢复事务中。
    /// </summary>
    public int RemoveTracks(IEnumerable<TrackRecord> tracks, bool deleteMediaFiles = true)
    {
        var removed = 0;
        foreach (var track in tracks.ToArray())
        {
            var persistentId = track.DbTrackId;
            var relativePath = track.RelativePath;

            if (!Database.RemoveTrack(track))
            {
                continue;
            }

            if (deleteMediaFiles && relativePath is not null &&
                SessionPath.TryCreate(relativePath, out var mediaPath))
            {
                _pendingDeletions.Add(mediaPath);
            }

            _pendingArtworkRemovals.Add(persistentId);
            removed++;
        }

        if (removed > 0)
        {
            _removedTracks += removed;
            AppLog.Info("音乐库", $"已从库中移除 {removed} 首曲目。");
            SetDirty();
        }

        return removed;
    }

    private readonly List<SessionPath> _pendingDeletions = [];
    private readonly List<ulong> _pendingArtworkRemovals = [];

    // ---- 保存 ----

    /// <summary>把当前内存中的音乐库写回设备。</summary>
    public async Task<SaveOutcome> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!_device.CanWrite)
        {
            return new SaveOutcome(false, _device.RestrictionReason, 0, 0);
        }

        if (!IsDirty)
        {
            return new SaveOutcome(true, "没有需要保存的修改。", 0, 0);
        }

        try
        {
            AppLog.Info("保存", $"开始保存音乐库（{_changedTrackIds.Count} 首曲目被修改）…");

            var logical = Database.Serialize();
            var needsCompression = _device.IsCompressed ||
                                   (_device.Profile?.Capabilities.Database.SupportsCompressedDatabase ?? false);
            var physical = needsCompression
                ? ITunesCdbCodec.Compress(logical, _device.CdbFraming)
                : logical;

            physical = SignPhysical(physical, _device);

            // 重新解析校验：保证生成的字节仍然是结构合法的数据库。
            var verifyLogical = needsCompression
                ? ITunesCdbCodec.Decompress(physical).LogicalBytes
                : physical;
            _ = ITunesDatabase.Parse(verifyLogical);

            var targetPath = needsCompression ? DevicePaths.ITunesCdb : DevicePaths.ITunesDb;
            var changedCount = _changedTrackIds.Count;
            var transaction = _device.Session.BeginTransaction();
            await using (transaction.ConfigureAwait(false))
            {
                transaction.Stage(SessionPath.Create(targetPath), physical);

                foreach (var deletion in _pendingDeletions)
                {
                    if (_device.Session.FileExists(deletion))
                    {
                        transaction.MarkForDeletion(deletion);
                    }
                }

                var result = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                _device.PhysicalDatabaseBytes = physical;
                _changedTrackIds.Clear();
                _pendingDeletions.Clear();
                _removedTracks = 0;
                _deviceNameChanged = false;
                OriginalDeviceName = _device.Name;
                IsDirty = false;
                Changed?.Invoke(this, EventArgs.Empty);

                var warning = result.CleanupWarning;
                var message = $"已保存 {result.Written} 个文件（{result.BytesWritten / 1024.0 / 1024.0:F2} MB）" +
                              (result.Deleted > 0 ? $"，移除 {result.Deleted} 个媒体文件" : string.Empty) + "。";
                AppLog.Info("保存", message);
                if (warning is not null)
                {
                    AppLog.Warning("保存", warning);
                }

                return new SaveOutcome(true, message, changedCount, result.BytesWritten, warning);
            }
        }
        catch (Exception error)
        {
            AppLog.Error("保存", "音乐库保存失败", error);
            return new SaveOutcome(false, $"保存失败：{error.Message}", 0, 0);
        }
    }

    /// <summary>按设备档案给出的校验方案为物理数据库字节签名。</summary>
    private static byte[] SignPhysical(byte[] physical, ActiveDevice device)
    {
        var checksum = device.Profile?.Capabilities.Database.Checksum ?? DatabaseChecksum.None;
        return checksum switch
        {
            DatabaseChecksum.None => physical,
            DatabaseChecksum.Hash58 => DatabaseSignature.SignHash58(physical, device.Guid),
            DatabaseChecksum.HashAb => DatabaseSignature.SignHashAb(physical, device.Guid),
            DatabaseChecksum.Hash72 => SignHash72(physical, device),
            _ => physical,
        };
    }

    private static byte[] SignHash72(byte[] physical, ActiveDevice device)
    {
        var (iv, randomPart) = ResolveHash72Material(device);
        var scheme = device.Database.HashingScheme is 1 ? (ushort)1 : (ushort)2;
        return DatabaseSignature.SignHash72(physical, iv, randomPart, scheme);
    }

    private static (byte[] Iv, byte[] RandomPart) ResolveHash72Material(ActiveDevice device)
    {
        // 1) 设备上的 HashInfo 记录
        try
        {
            var path = SessionPath.Create(DevicePaths.HashInfo);
            if (device.Session.FileExists(path))
            {
                var material = DatabaseSignature.ParseHashInfo(device.Session.ReadAllBytes(path, 4096));
                if (material.Iv.Length == 16 && material.RandomPart.Length == 12)
                {
                    return (material.Iv, material.RandomPart);
                }
            }
        }
        catch (Exception error)
        {
            AppLog.Warning("签名", $"HashInfo 不可用（{error.Message}），尝试从现有数据库恢复。");
        }

        // 2) 从现有数据库中已签名的信封恢复
        if (device.PhysicalDatabaseBytes.Length > 0 &&
            DatabaseSignature.HasHash72Signature(device.PhysicalDatabaseBytes))
        {
            return DatabaseSignature.ExtractHash72Material(device.PhysicalDatabaseBytes);
        }

        throw new SignatureException(
            "既没有可用的 HashInfo 记录，现有数据库也没有可恢复的 HASH72 签名，无法安全写入该设备。");
    }

    // ---- 内部状态 ----

    private void MarkChanged(TrackRecord track)
    {
        _changedTrackIds.Add(track.TrackId);
        SetDirty();
    }

    private void SetDirty()
    {
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>清除脏标记（用于“已保存”提示）。</summary>
    internal void MarkClean()
    {
        IsDirty = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
