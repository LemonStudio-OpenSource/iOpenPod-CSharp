using IOpenPod.IPodDb.Chunks;

namespace IOpenPod.IPodDb;

/// <summary>iTunesDB 数据集类型。</summary>
public enum ITunesDatasetType
{
    TrackList = 1,
    PlaylistList = 2,
    PodcastPlaylistList = 3,
    AlbumList = 4,
    SmartPlaylistList = 5,
    UnknownTrackList6 = 6,
    Reserved7 = 7,
    ArtistList = 8,
    GeniusData = 9,
    UnknownTrackList10 = 10,
}

/// <summary>
/// 一个完整的 iTunesDB。
///
/// 保持无损：未修改的字节在序列化时逐字节重现。所有编辑都在数据块树上原地进行。
/// </summary>
public sealed class ITunesDatabase
{
    private readonly List<TrackRecord> _tracks = [];
    private readonly Dictionary<uint, TrackRecord> _tracksById = [];
    private readonly List<PlaylistRecord> _playlists = [];
    private readonly Dictionary<ulong, PlaylistRecord> _playlistsById = [];

    private ITunesDatabase(ChunkNode root, byte[] sourceSuffix)
    {
        Root = root;
        SourceSuffix = sourceSuffix;
        Reload();
    }

    /// <summary>底层根数据块（mhbd）。</summary>
    public ChunkNode Root { get; }

    /// <summary>数据库之后保留的原始字节。</summary>
    public byte[] SourceSuffix { get; }

    public DatabaseDefinition Definition => DatabaseDefinitions.ITunesDb;

    /// <summary>解析一份 iTunesDB 字节。</summary>
    public static ITunesDatabase Parse(byte[] data)
    {
        if (data.Length < 12)
        {
            throw new TruncatedChunkException("iTunesDB 内容不足 12 字节。");
        }

        var (node, next) = ChunkReader.Read(data, 0, DatabaseDefinitions.ITunesDb);
        if (node.Marker != "mhbd")
        {
            throw new InvalidChunkDataException($"iTunesDB 根块必须是 mhbd，实际为 {node.Marker}。");
        }

        var suffix = next < data.Length ? data[next..] : [];
        return new ITunesDatabase(node, suffix);
    }

    /// <summary>序列化回完整字节。</summary>
    public byte[] Serialize() => ChunkWriter.WriteDatabase(Root, DatabaseDefinitions.ITunesDb, SourceSuffix);

    // ---- 头部 ----

    public uint Version
    {
        get => Root.ReadU32Or(MhbdFields.Version, 0xFF);
        set => Root.TryWriteU32(MhbdFields.Version, value);
    }

    public ulong DatabaseId
    {
        get => Root.HasField(MhbdFields.DbId, 8) ? Root.ReadU64(MhbdFields.DbId) : 0;
        set
        {
            Root.TryWriteU64(MhbdFields.DbId, value);
            Root.TryWriteU64(MhbdFields.DbId2, value);
        }
    }

    public ushort HashingScheme
    {
        get => Root.HasField(MhbdFields.HashingScheme, 2) ? Root.ReadU16(MhbdFields.HashingScheme) : (ushort)0;
        set => Root.TryWriteU16(MhbdFields.HashingScheme, value);
    }

    public int TimezoneOffset
    {
        get => Root.HasField(MhbdFields.TimezoneOffset, 4) ? Root.ReadI32(MhbdFields.TimezoneOffset) : 0;
        set => Root.TryWriteI32(MhbdFields.TimezoneOffset, value);
    }

    public ushort Platform
    {
        get => Root.HasField(MhbdFields.Platform, 2) ? Root.ReadU16(MhbdFields.Platform) : (ushort)2;
        set => Root.TryWriteU16(MhbdFields.Platform, value);
    }

    /// <summary>是否为压缩（CDB）数据库。</summary>
    public bool IsCompressed
    {
        get => Root.ReadU32Or(MhbdFields.Compressed, 1) != 0;
        set => Root.TryWriteU32(MhbdFields.Compressed, value ? 1u : 0u);
    }

    // ---- 数据集 ----

    /// <summary>全部数据集包装块。</summary>
    public IReadOnlyList<ChunkNode> Datasets =>
        Root.Children.Where(child => child.Marker == "mhsd").ToArray();

    /// <summary>按数据集类型查找包装块。</summary>
    public ChunkNode? Dataset(int datasetType) =>
        Datasets.FirstOrDefault(node =>
            node.HasField(0x0C, 4) && (int)node.ReadU32(0x0C) == datasetType);

    /// <summary>取得或创建指定类型的曲目列表数据集。</summary>
    private ChunkNode? TrackListContainer() => Dataset(1)?.Child("mhlt");

    private ChunkNode? PlaylistListContainer() => Dataset(2)?.Child("mhlp");

    // ---- 曲目 ----

    public IReadOnlyList<TrackRecord> Tracks => _tracks;

    public int TrackCount => _tracks.Count;

    public TrackRecord? TrackById(uint trackId) => _tracksById.GetValueOrDefault(trackId);

    /// <summary>全部曲目的持久标识。</summary>
    public IReadOnlyDictionary<uint, ulong> PersistentIds =>
        _tracks.ToDictionary(track => track.TrackId, track => track.DbTrackId);

    /// <summary>重新从数据块树读取曲目与播放列表。</summary>
    public void Reload()
    {
        _tracks.Clear();
        _tracksById.Clear();
        _playlists.Clear();
        _playlistsById.Clear();

        var container = TrackListContainer();
        if (container is not null)
        {
            foreach (var child in container.Children)
            {
                if (child.Marker != "mhit")
                {
                    continue;
                }

                var track = new TrackRecord(this, child);
                _tracks.Add(track);
                if (!_tracksById.ContainsKey(track.TrackId))
                {
                    _tracksById[track.TrackId] = track;
                }
            }
        }

        var playlistContainer = PlaylistListContainer();
        if (playlistContainer is not null)
        {
            foreach (var child in playlistContainer.Children)
            {
                if (child.Marker != "mhyp")
                {
                    continue;
                }

                var playlist = new PlaylistRecord(this, child);
                _playlists.Add(playlist);
                if (!_playlistsById.ContainsKey(playlist.PlaylistId))
                {
                    _playlistsById[playlist.PlaylistId] = playlist;
                }
            }
        }
    }

    /// <summary>主播放列表；不存在时返回 <c>null</c>。</summary>
    public PlaylistRecord? MasterPlaylist =>
        _playlists.FirstOrDefault(playlist => playlist.IsMaster) ?? _playlists.FirstOrDefault();

    /// <summary>用户可见的播放列表（不含主播放列表与固件播客列表）。</summary>
    public IReadOnlyList<PlaylistRecord> UserPlaylists =>
        _playlists.Where(playlist => !playlist.IsMaster && !string.Equals(playlist.Name, "Podcasts", StringComparison.Ordinal)).ToArray();

    public PlaylistRecord? PlaylistById(ulong id) => _playlistsById.GetValueOrDefault(id);

    /// <summary>下一个可用的曲目标识。</summary>
    public uint NextTrackId()
    {
        var allocator = new IdentityAllocator(_tracks.Select(track => (ulong)track.TrackId), 32);
        return (uint)allocator.Take();
    }

    /// <summary>为新曲目计算持久标识。</summary>
    public ulong PersistentIdFor(uint trackId) =>
        (DatabaseId & 0xFFFFFFFFFF000000UL) | (trackId & 0x00FFFFFFFFFFFFFFUL);

    /// <summary>下一个可用的播放列表标识。</summary>
    public ulong NextPlaylistId()
    {
        var allocator = new IdentityAllocator(_playlists.Select(playlist => playlist.PlaylistId), 64);
        return allocator.Take();
    }

    /// <summary>下一个可用的播放列表条目持久标识。</summary>
    public ulong NextPlaylistItemPersistentId()
    {
        var existing = _playlists
            .SelectMany(playlist => playlist.Node.Children)
            .Where(child => child.Marker == "mhip")
            .Select(child => child.HasField(0x3C, 8) ? child.ReadU64(0x3C) : 0UL);
        return new IdentityAllocator(existing, 64).Take();
    }

    /// <summary>按设备策略扩展曲目头部；无法容纳时返回 <c>false</c>。</summary>
    public bool ExpandTrackHeader(ChunkNode node, int requiredEnd)
    {
        var current = node.Header.Length;
        if (current >= requiredEnd)
        {
            return true;
        }

        // 不得超出设备自身的创作布局。
        var deviceLayout = MhitFields.HeaderSizeForVersion(Version);
        var candidates = new[] { 0x9C, 0x148, 0x1F8, 0x270 };
        var target = candidates
            .Where(size => size >= requiredEnd && size <= deviceLayout)
            .DefaultIfEmpty(-1)
            .Min();

        if (target < 0)
        {
            return false;
        }

        var header = new byte[target];
        Array.Copy(node.Header, header, Math.Min(current, target));
        node.Header = header;

        SeedExpandedFields(node, current, target);
        return true;
    }

    /// <summary>为新增暴露的字段填入合理默认值，绝不覆盖原有数据。</summary>
    private static void SeedExpandedFields(ChunkNode node, int oldLength, int newLength)
    {
        void SeedIfNew(int offset, int size, Action write)
        {
            if (offset >= oldLength && offset + size <= newLength)
            {
                write();
            }
        }

        var dbTrackId = node.HasField(MhitFields.DbTrackId, 8) ? node.ReadU64(MhitFields.DbTrackId) : 0UL;
        var artworkCount = node.HasField(MhitFields.ArtworkCount, 2) ? node.ReadU16(MhitFields.ArtworkCount) : (ushort)0;
        var videoFlag = node.HasField(MhitFields.VideoFlag, 1) ? node.ReadU8(MhitFields.VideoFlag) : (byte)0;
        var purchasedAac = node.HasField(MhitFields.PurchasedAacFlag, 1) ? node.ReadU8(MhitFields.PurchasedAacFlag) : (byte)0;

        SeedIfNew(MhitFields.DbTrackId2, 8, () => node.WriteU64(MhitFields.DbTrackId2, dbTrackId));
        SeedIfNew(MhitFields.NotPlayedFlag, 1, () => node.WriteU8(MhitFields.NotPlayedFlag, 2));
        SeedIfNew(MhitFields.HasArtwork, 1, () => node.WriteU8(MhitFields.HasArtwork, (byte)(artworkCount > 0 ? 1 : 0)));
        SeedIfNew(MhitFields.VideoFlag2, 1, () => node.WriteU8(MhitFields.VideoFlag2, videoFlag));
        SeedIfNew(MhitFields.PurchasedAacFlag2, 1, () => node.WriteU8(MhitFields.PurchasedAacFlag2, purchasedAac));
        SeedIfNew(MhitFields.Volume, 4, () => node.WriteI32(MhitFields.Volume, 0));
        SeedIfNew(MhitFields.MediaType, 4, () => node.WriteU32(MhitFields.MediaType, 1));
    }

    // ---- 结构编辑 ----

    /// <summary>创建一个新的曲目条目并加入曲目列表。</summary>
    public TrackRecord AddTrack()
    {
        var container = TrackListContainer()
            ?? throw new IPodDbWriteException("iTunesDB 缺少曲目列表数据集，无法添加曲目。");

        var spec = Definition.Chunk("mhit")!;
        var node = ChunkNode.Create(spec, MhitFields.HeaderSizeForVersion(Version));

        var trackId = NextTrackId();
        node.WriteU32(MhitFields.ChildCount, 0);
        node.WriteU32(MhitFields.TrackId, trackId);
        node.WriteU32(MhitFields.UnknownClassFlag, 1);
        node.TryWriteU64(MhitFields.DbTrackId, PersistentIdFor(trackId));
        node.TryWriteU16(MhitFields.ArtworkCount, 0);
        node.TryWriteU16(MhitFields.AudioFormatFlag, ITunesDbConstants.DefaultAudioFormatFlag);
        node.TryWriteU32(MhitFields.MediaType, 1);
        node.TryWriteU8(MhitFields.NotPlayedFlag, 2);
        node.TryWriteU64(MhitFields.DbTrackId2, PersistentIdFor(trackId));
        node.TryWriteU32(MhitFields.SampleRate1, 44100);
        node.TryWriteF32(MhitFields.SampleRate2, 44100f);
        node.TryWriteU32(MhitFields.DateAdded, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2082844800u);
        node.TryWriteU32(MhitFields.DateAddedToItunes, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2082844800u);

        container.AddChild(node);
        var track = new TrackRecord(this, node);
        _tracks.Add(track);
        _tracksById[trackId] = track;
        return track;
    }

    /// <summary>从数据库移除一个曲目，并清理所有播放列表引用。</summary>
    public bool RemoveTrack(TrackRecord track)
    {
        var container = TrackListContainer();
        if (container is null || !container.RemoveChild(track.Node))
        {
            return false;
        }

        _tracks.Remove(track);
        _tracksById.Remove(track.TrackId);

        foreach (var playlist in _playlists)
        {
            playlist.RemoveAllOccurrencesOf(track.TrackId);
        }

        return true;
    }

    /// <summary>创建一个新的播放列表。</summary>
    public PlaylistRecord AddPlaylist(string name, ulong? parentFolderId = null)
    {
        var container = PlaylistListContainer()
            ?? throw new IPodDbWriteException("iTunesDB 缺少播放列表数据集，无法创建播放列表。");

        var spec = Definition.Chunk("mhyp")!;
        var node = ChunkNode.Create(spec);
        var playlistId = NextPlaylistId();
        node.WriteU64(MhypFields.PlaylistId, playlistId);
        node.TryWriteU64(MhypFields.PlaylistId2, playlistId);
        node.TryWriteU64(MhypFields.DbId2, DatabaseId);
        node.TryWriteU32(MhypFields.MhodChildCount, 0);
        node.TryWriteU32(MhypFields.MhipChildCount, 0);
        node.TryWriteU32(MhypFields.Timestamp, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2082844800u);
        node.TryWriteU32(MhypFields.SortOrder, 1);
        if (parentFolderId is { } parent)
        {
            node.TryWriteU64(MhypFields.ParentFolderPlaylistId, parent);
        }

        container.AddChild(node);
        var playlist = new PlaylistRecord(this, node) { Name = name };
        _playlists.Add(playlist);
        _playlistsById[playlistId] = playlist;
        return playlist;
    }

    /// <summary>移除一个播放列表。</summary>
    public bool RemovePlaylist(PlaylistRecord playlist)
    {
        if (playlist.IsMaster)
        {
            return false;
        }

        var container = PlaylistListContainer();
        if (container is null || !container.RemoveChild(playlist.Node))
        {
            return false;
        }

        _playlists.Remove(playlist);
        _playlistsById.Remove(playlist.PlaylistId);
        return true;
    }

    /// <summary>把曲目按顺序发布到主播放列表。</summary>
    public void RebuildMasterPlaylist()
    {
        var master = MasterPlaylist;
        if (master is null)
        {
            return;
        }

        foreach (var child in master.Node.Children.Where(child => child.Marker == "mhip").ToArray())
        {
            master.Node.RemoveChild(child);
        }

        foreach (var track in _tracks.OrderBy(track => track.TrackId))
        {
            master.AddTrack(track, NextPlaylistItemPersistentId());
        }
    }
}
