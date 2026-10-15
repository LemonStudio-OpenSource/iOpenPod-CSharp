using IOpenPod.IPodDb.Chunks;

namespace IOpenPod.IPodDb;

/// <summary>播放列表中的一个出现位置（MHIP）。</summary>
public sealed class PlaylistItem
{
    internal PlaylistItem(ChunkNode node)
    {
        Node = node;
    }

    public ChunkNode Node { get; }

    /// <summary>被引用的曲目标识。</summary>
    public uint TrackId
    {
        get => Node.ReadU32Or(MhipFields.TrackId);
        set => Node.TryWriteU32(MhipFields.TrackId, value);
    }

    public uint GroupId
    {
        get => Node.ReadU32Or(MhipFields.GroupId);
        set => Node.TryWriteU32(MhipFields.GroupId, value);
    }

    public ulong TrackPersistentId
    {
        get => Node.HasField(MhipFields.TrackPersistentId, 8) ? Node.ReadU64(MhipFields.TrackPersistentId) : 0;
        set => Node.TryWriteU64(MhipFields.TrackPersistentId, value);
    }

    public uint Timestamp
    {
        get => Node.ReadU32Or(MhipFields.Timestamp);
        set => Node.TryWriteU32(MhipFields.Timestamp, value);
    }

    public override string ToString() => $"播放列表条目 → 曲目 {TrackId}";
}

/// <summary>一个播放列表（MHYP）。</summary>
public sealed class PlaylistRecord
{
    private readonly ITunesDatabase _database;

    internal PlaylistRecord(ITunesDatabase database, ChunkNode node)
    {
        _database = database;
        Node = node;
    }

    public ChunkNode Node { get; }

    public ulong PlaylistId
    {
        get => Node.HasField(MhypFields.PlaylistId, 8) ? Node.ReadU64(MhypFields.PlaylistId) : 0;
        set => Node.TryWriteU64(MhypFields.PlaylistId, value);
    }

    /// <summary>是否为主播放列表（Master Playlist）。</summary>
    public bool IsMaster
    {
        get => Node.HasField(MhypFields.MasterFlag, 1) && Node.ReadU8(MhypFields.MasterFlag) != 0;
        set
        {
            Node.TryWriteU8(MhypFields.MasterFlag, value ? (byte)1 : (byte)0);
            if (value)
            {
                Node.TryWriteU8(MhypFields.Flag1, 0);
            }
        }
    }

    /// <summary>是否为智能播放列表。</summary>
    public bool IsSmartPlaylist
    {
        get
        {
            var flags = Node.HasField(MhypFields.PlaylistKindFlags, 2) ? Node.ReadU16(MhypFields.PlaylistKindFlags) : 0;
            return (flags & 0x0004) != 0 || GetMhod(MhodType.SmartPlaylistRules) is not null;
        }
    }

    /// <summary>是否为播放列表文件夹。</summary>
    public bool IsFolder => GetMhod(MhodType.PlaylistPropertyPlist)?.ReadU32Or(0x0C) == 0 && IsFolderFlag;

    private bool IsFolderFlag =>
        Node.HasField(MhypFields.PlaylistKindFlags, 2) && (Node.ReadU16(MhypFields.PlaylistKindFlags) & 0x0100) != 0;

    public int SortOrder
    {
        get => Node.HasField(MhypFields.SortOrder, 4) ? (int)Node.ReadU32Or(MhypFields.SortOrder) : 0;
        set => Node.TryWriteU32(MhypFields.SortOrder, (uint)Math.Max(0, value));
    }

    public string SortOrderName =>
        ITunesDbConstants.PlaylistSortOrders.TryGetValue(SortOrder, out var name) ? name : $"未知排序（{SortOrder}）";

    public ulong ParentFolderId
    {
        get => Node.HasField(MhypFields.ParentFolderPlaylistId, 8) ? Node.ReadU64(MhypFields.ParentFolderPlaylistId) : 0;
        set => Node.TryWriteU64(MhypFields.ParentFolderPlaylistId, value);
    }

    /// <summary>播放列表名称（标题数据对象）。</summary>
    public string Name
    {
        get => GetString(MhodType.Title);
        set => SetString(MhodType.Title, value);
    }

    /// <summary>播放列表中的全部出现位置。</summary>
    public IReadOnlyList<PlaylistItem> Items =>
        Node.Children
            .Where(child => child.Marker == "mhip")
            .Select(child => new PlaylistItem(child))
            .ToArray();

    public int TrackCount => Node.Children.Count(child => child.Marker == "mhip");

    /// <summary>按顺序取出引用到的曲目。</summary>
    public IEnumerable<TrackRecord> Tracks()
    {
        foreach (var child in Node.Children)
        {
            if (child.Marker != "mhip")
            {
                continue;
            }

            var track = _database.TrackById(child.ReadU32Or(MhipFields.TrackId));
            if (track is not null)
            {
                yield return track;
            }
        }
    }

    /// <summary>把一个曲目出现位置追加到播放列表末尾。</summary>
    public PlaylistItem AddTrack(TrackRecord track, ulong persistentId)
    {
        var spec = _database.Definition.Chunk("mhip")!;
        var node = ChunkNode.Create(spec);
        node.WriteU32(MhipFields.TrackId, track.TrackId);
        node.WriteU32(MhipFields.GroupId, 0);
        node.WriteU32(MhipFields.Timestamp, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        node.TryWriteU64(MhipFields.TrackPersistentId, track.DbTrackId);
        node.TryWriteU64(MhipFields.MhipPersistentId, persistentId);
        node.TryWriteU64(MhipFields.GroupPersistentId, 0);
        Node.AddChild(node);
        return new PlaylistItem(node);
    }

    /// <summary>移除一个曲目出现位置。</summary>
    public bool RemoveItem(PlaylistItem item) => Node.RemoveChild(item.Node);

    /// <summary>移除某个曲目的全部出现位置；返回移除数量。</summary>
    public int RemoveAllOccurrencesOf(uint trackId)
    {
        var removed = 0;
        for (var i = Node.Children.Count - 1; i >= 0; i--)
        {
            var child = Node.Children[i];
            if (child.Marker == "mhip" && child.ReadU32Or(MhipFields.TrackId) == trackId)
            {
                Node.Children.RemoveAt(i);
                removed++;
            }
        }

        return removed;
    }

    public ChunkNode? GetMhod(MhodType type)
    {
        var value = (uint)type;
        foreach (var child in Node.Children)
        {
            if (child.IsMhod && child.HasField(0x0C, 4) && child.ReadU32(0x0C) == value)
            {
                return child;
            }
        }

        return null;
    }

    public string GetString(MhodType type)
    {
        var mhod = GetMhod(type);
        return mhod is null ? string.Empty : MhodString.Read(mhod);
    }

    public void SetString(MhodType type, string? value)
    {
        var text = value ?? string.Empty;
        var existing = GetMhod(type);
        if (text.Length == 0)
        {
            if (existing is not null)
            {
                Node.RemoveChild(existing);
            }

            return;
        }

        if (existing is not null)
        {
            MhodString.Write(existing, text);
            return;
        }

        var spec = _database.Definition.Chunk("mhod")!;
        var node = MhodString.Create(spec, (int)type, text);

        // 字符串型数据对象必须排在条目型数据对象之前。
        var index = Node.Children.FindIndex(child => child.Marker == "mhip");
        if (index < 0)
        {
            Node.AddChild(node);
        }
        else
        {
            Node.Children.Insert(index, node);
        }
    }

    public override string ToString() =>
        IsMaster ? $"主播放列表「{Name}」（{TrackCount} 首）" : $"播放列表「{Name}」（{TrackCount} 首）";
}
