using IOpenPod.IPodDb.Chunks;

namespace IOpenPod.IPodDb;

/// <summary>
/// 一条曲目的类型化视图。
///
/// 所有读写在底层数据块上原地进行；未被显式修改的字节保持原样，
/// 因此“解析后立即写回”可以逐字节重现原始数据库。
/// </summary>
public sealed class TrackRecord
{
    private readonly ITunesDatabase _database;

    internal TrackRecord(ITunesDatabase database, ChunkNode node)
    {
        _database = database;
        Node = node;
    }

    /// <summary>底层 MHIT 数据块。</summary>
    public ChunkNode Node { get; }

    // ---- 头部整数字段 ----

    public uint TrackId
    {
        get => Node.ReadU32Or(MhitFields.TrackId);
        set => Node.TryWriteU32(MhitFields.TrackId, value);
    }

    public ulong DbTrackId
    {
        get => Node.HasField(MhitFields.DbTrackId, 8) ? Node.ReadU64(MhitFields.DbTrackId) : 0;
        set => Node.TryWriteU64(MhitFields.DbTrackId, value);
    }

    public uint FileType
    {
        get => Node.ReadU32Or(MhitFields.FileType);
        set => Node.TryWriteU32(MhitFields.FileType, value);
    }

    public string FileExtension => ITunesDbConstants.FileTypeExtension(FileType);

    public byte Rating
    {
        get => Node.HasField(MhitFields.Rating, 1) ? Node.ReadU8(MhitFields.Rating) : (byte)0;
        set => Node.TryWriteU8(MhitFields.Rating, value > 100 ? (byte)100 : value);
    }

    /// <summary>
    /// 应用层评分字节（MHIT 偏移 0x79）。
    ///
    /// 这是设备上独立的原生数值，iTunes 与 libgpod 会各自维护它；
    /// 因此修改 <see cref="Rating"/> 时不会隐式改写它，以保证无损。
    /// </summary>
    public byte AppRating
    {
        get => Node.HasField(MhitFields.AppRating, 1) ? Node.ReadU8(MhitFields.AppRating) : (byte)0;
        set => Node.TryWriteU8(MhitFields.AppRating, value);
    }

    /// <summary>0–5 星评分。</summary>
    public int StarRating
    {
        get => (Rating + 10) / 20;
        set => Rating = (byte)(Math.Clamp(value, 0, 5) * 20);
    }

    public bool IsCompilation
    {
        get => Node.HasField(MhitFields.CompilationFlag, 1) && Node.ReadU8(MhitFields.CompilationFlag) != 0;
        set => Node.TryWriteU8(MhitFields.CompilationFlag, value ? (byte)1 : (byte)0);
    }

    public bool IsVbr
    {
        get => Node.HasField(MhitFields.VbrFlag, 1) && Node.ReadU8(MhitFields.VbrFlag) != 0;
        set => Node.TryWriteU8(MhitFields.VbrFlag, value ? (byte)1 : (byte)0);
    }

    public uint SizeBytes
    {
        get => Node.ReadU32Or(MhitFields.Size);
        set => Node.TryWriteU32(MhitFields.Size, value);
    }

    public uint LengthMs
    {
        get => Node.ReadU32Or(MhitFields.Length);
        set => Node.TryWriteU32(MhitFields.Length, value);
    }

    public TimeSpan Duration => TimeSpan.FromMilliseconds(LengthMs);

    public int TrackNumber
    {
        get => (int)Node.ReadU32Or(MhitFields.TrackNumber);
        set => Node.TryWriteU32(MhitFields.TrackNumber, (uint)Math.Max(0, value));
    }

    public int TotalTracks
    {
        get => (int)Node.ReadU32Or(MhitFields.TotalTracks);
        set => Node.TryWriteU32(MhitFields.TotalTracks, (uint)Math.Max(0, value));
    }

    public int DiscNumber
    {
        get => (int)Node.ReadU32Or(MhitFields.DiscNumber);
        set => Node.TryWriteU32(MhitFields.DiscNumber, (uint)Math.Max(0, value));
    }

    public int TotalDiscs
    {
        get => (int)Node.ReadU32Or(MhitFields.TotalDiscs);
        set => Node.TryWriteU32(MhitFields.TotalDiscs, (uint)Math.Max(0, value));
    }

    public int Year
    {
        get => (int)Node.ReadU32Or(MhitFields.Year);
        set => Node.TryWriteU32(MhitFields.Year, (uint)Math.Max(0, value));
    }

    public int Bitrate
    {
        get => (int)Node.ReadU32Or(MhitFields.Bitrate);
        set => Node.TryWriteU32(MhitFields.Bitrate, (uint)Math.Max(0, value));
    }

    public int SampleRate
    {
        get
        {
            var primary = (int)Node.ReadU32Or(MhitFields.SampleRate1);
            if (primary > 0)
            {
                return primary;
            }

            return Node.HasField(MhitFields.SampleRate2, 4) ? (int)Node.ReadF32(MhitFields.SampleRate2) : 0;
        }
    }

    public int Volume
    {
        get => Node.HasField(MhitFields.Volume, 4) ? Node.ReadI32(MhitFields.Volume) : 0;
        set => Node.TryWriteI32(MhitFields.Volume, value);
    }

    public uint PlayCount => Math.Max(Node.ReadU32Or(MhitFields.PlayCount1), Node.ReadU32Or(MhitFields.PlayCount2));

    public uint SkipCount => Node.HasField(MhitFields.SkipCount, 4) ? Node.ReadU32Or(MhitFields.SkipCount) : 0;

    public uint LastPlayed => Node.ReadU32Or(MhitFields.LastPlayed);

    public uint DateAdded
    {
        get => Node.ReadU32Or(MhitFields.DateAdded);
        set => Node.TryWriteU32(MhitFields.DateAdded, value);
    }

    /// <summary>文件修改时间（Mac 纪元秒）。</summary>
    public uint LastModified
    {
        get => Node.ReadU32Or(MhitFields.LastModified);
        set => Node.TryWriteU32(MhitFields.LastModified, value);
    }

    public ushort Bpm
    {
        get => Node.HasField(MhitFields.Bpm, 2) ? Node.ReadU16(MhitFields.Bpm) : (ushort)0;
        set => Node.TryWriteU16(MhitFields.Bpm, value);
    }

    public uint MediaType
    {
        get => Node.HasField(MhitFields.MediaType, 4) ? Node.ReadU32Or(MhitFields.MediaType, 1) : 1;
        set => Node.TryWriteU32(MhitFields.MediaType, value);
    }

    public string MediaTypeName => ITunesDbConstants.MediaTypeName(MediaType);

    public ushort ArtworkCount
    {
        get => Node.HasField(MhitFields.ArtworkCount, 2) ? Node.ReadU16(MhitFields.ArtworkCount) : (ushort)0;
        set => Node.TryWriteU16(MhitFields.ArtworkCount, value);
    }

    public uint ArtworkIdRef
    {
        get => Node.HasField(MhitFields.ArtworkIdRef, 4) ? Node.ReadU32Or(MhitFields.ArtworkIdRef) : 0;
        set => Node.TryWriteU32(MhitFields.ArtworkIdRef, value);
    }

    public bool HasArtwork
    {
        get => ArtworkCount > 0 || ArtworkIdRef != 0;
    }

    public bool SkipWhenShuffling
    {
        get => Node.HasField(MhitFields.SkipWhenShuffling, 1) && Node.ReadU8(MhitFields.SkipWhenShuffling) != 0;
        set => Node.TryWriteU8(MhitFields.SkipWhenShuffling, value ? (byte)1 : (byte)0);
    }

    public bool RememberPosition
    {
        get => Node.HasField(MhitFields.RememberPosition, 1) && Node.ReadU8(MhitFields.RememberPosition) != 0;
        set => Node.TryWriteU8(MhitFields.RememberPosition, value ? (byte)1 : (byte)0);
    }

    public bool IsVideo => (MediaType & 0x0000003E) != 0;

    public bool IsPodcast => (MediaType & 0x00000004) != 0;

    public bool LyricsFlag
    {
        get => Node.HasField(MhitFields.LyricsFlag, 1) && Node.ReadU8(MhitFields.LyricsFlag) != 0;
        set => Node.TryWriteU8(MhitFields.LyricsFlag, value ? (byte)1 : (byte)0);
    }

    public byte ExplicitFlag
    {
        get => Node.HasField(MhitFields.ExplicitFlag, 1) ? Node.ReadU8(MhitFields.ExplicitFlag) : (byte)0;
        set => Node.TryWriteU8(MhitFields.ExplicitFlag, value);
    }

    public int SeasonNumber
    {
        get => Node.HasField(MhitFields.SeasonNumber, 4) ? (int)Node.ReadU32Or(MhitFields.SeasonNumber) : 0;
        set => Node.TryWriteU32(MhitFields.SeasonNumber, (uint)Math.Max(0, value));
    }

    public int EpisodeNumber
    {
        get => Node.HasField(MhitFields.EpisodeNumber, 4) ? (int)Node.ReadU32Or(MhitFields.EpisodeNumber) : 0;
        set => Node.TryWriteU32(MhitFields.EpisodeNumber, (uint)Math.Max(0, value));
    }

    // ---- 字符串型数据对象 ----

    public string Title
    {
        get => GetString(MhodType.Title);
        set => SetString(MhodType.Title, value);
    }

    public string Artist
    {
        get => GetString(MhodType.Artist);
        set => SetString(MhodType.Artist, value);
    }

    public string AlbumArtist
    {
        get => GetString(MhodType.AlbumArtist);
        set => SetString(MhodType.AlbumArtist, value);
    }

    public string Album
    {
        get => GetString(MhodType.Album);
        set => SetString(MhodType.Album, value);
    }

    public string Genre
    {
        get => GetString(MhodType.Genre);
        set => SetString(MhodType.Genre, value);
    }

    public string Composer
    {
        get => GetString(MhodType.Composer);
        set => SetString(MhodType.Composer, value);
    }

    public string Comment
    {
        get => GetString(MhodType.Comment);
        set => SetString(MhodType.Comment, value);
    }

    public string Grouping
    {
        get => GetString(MhodType.Grouping);
        set => SetString(MhodType.Grouping, value);
    }

    public string Category
    {
        get => GetString(MhodType.Category);
        set => SetString(MhodType.Category, value);
    }

    public string Lyrics
    {
        get => GetString(MhodType.Lyrics);
        set
        {
            var text = value ?? string.Empty;
            SetString(MhodType.Lyrics, text);

            // 与 iTunes 保持一致：只有正文非空时才是“含歌词”。
            LyricsFlag = text.Length > 0;
        }
    }

    public string Show
    {
        get => GetString(MhodType.Show);
        set => SetString(MhodType.Show, value);
    }

    public string Episode
    {
        get => GetString(MhodType.Episode);
        set => SetString(MhodType.Episode, value);
    }

    public string TvNetwork
    {
        get => GetString(MhodType.TvNetwork);
        set => SetString(MhodType.TvNetwork, value);
    }

    public string Description
    {
        get => GetString(MhodType.Description);
        set => SetString(MhodType.Description, value);
    }

    public string SortTitle
    {
        get => GetString(MhodType.SortTitle);
        set => SetString(MhodType.SortTitle, value);
    }

    public string SortArtist
    {
        get => GetString(MhodType.SortArtist);
        set => SetString(MhodType.SortArtist, value);
    }

    public string SortAlbum
    {
        get => GetString(MhodType.SortAlbum);
        set => SetString(MhodType.SortAlbum, value);
    }

    /// <summary>设备上的文件位置，形如 <c>iPod_Control:Music:F00:ABCD.mp3</c>。</summary>
    public string Location
    {
        get => GetString(MhodType.Location);
        set => SetString(MhodType.Location, value);
    }

    /// <summary>把位置转换为设备相对路径（使用正斜杠）。</summary>
    public string? RelativePath
    {
        get
        {
            var location = Location;
            if (location.Length == 0)
            {
                return null;
            }

            var normalized = location.Replace(':', '/').TrimStart('/');
            return normalized.Length == 0 ? null : normalized;
        }
    }

    /// <summary>设备上的文件名。</summary>
    public string DeviceFileName
    {
        get
        {
            var path = RelativePath;
            if (path is null)
            {
                return string.Empty;
            }

            var index = path.LastIndexOf('/');
            return index < 0 ? path : path[(index + 1)..];
        }
    }

    /// <summary>显示用的文件名：优先使用“艺术家 - 标题”，否则回退到设备文件名。</summary>
    public string DisplayFileName
    {
        get
        {
            if (Title.Length > 0)
            {
                return Artist.Length > 0 ? $"{Artist} - {Title}" : Title;
            }

            return DeviceFileName;
        }
    }

    /// <summary>是否为支持在应用内播放的音频文件。</summary>
    public bool IsPlayableAudio =>
        !IsVideo && MediaType is not (uint)MediaTypeFlags.EpubBook and not (uint)MediaTypeFlags.PdfBook;

    // ---- 数据对象访问 ----

    /// <summary>取得指定类型的第一个字符串数据对象。</summary>
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

    /// <summary>写入字符串型数据对象；内容为空时移除该对象。</summary>
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
        InsertMhodOrdered(node, (int)type);
    }

    /// <summary>按类型升序插入数据对象，保持固件期望的顺序。</summary>
    private void InsertMhodOrdered(ChunkNode node, int type)
    {
        var index = Node.Children.Count;
        for (var i = 0; i < Node.Children.Count; i++)
        {
            var child = Node.Children[i];
            if (!child.IsMhod || !child.HasField(0x0C, 4))
            {
                continue;
            }

            if (child.ReadU32(0x0C) > (uint)type)
            {
                index = i;
                break;
            }
        }

        Node.Children.Insert(index, node);
    }

    /// <summary>确保曲目头部至少能容纳指定字段；返回是否成功。</summary>
    public bool EnsureHeaderField(int offset, int size)
    {
        if (Node.HasField(offset, size))
        {
            return true;
        }

        return _database.ExpandTrackHeader(Node, offset + size);
    }

    public override string ToString() =>
        $"{Title} — {Artist}（ID {TrackId}）";
}
