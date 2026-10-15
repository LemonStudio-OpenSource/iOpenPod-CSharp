using System.Text;
using IOpenPod.IPodDb.Chunks;

namespace IOpenPod.IPodDb.Artwork;

/// <summary>一张插图的 iTHMB 定位信息。</summary>
public sealed record IthmbLocation(
    uint FormatId,
    string FileName,
    long Offset,
    long ByteLength,
    int Width,
    int Height,
    short HorizontalPadding,
    short VerticalPadding,
    int DisplayTimeMs)
{
    /// <summary>像素格式由设备档案解析得到，不在此处推断。</summary>
    public IthmbLayout ToLayout(IthmbPixelFormat pixelFormat, int rowBytes) =>
        new(Width, Height, rowBytes, pixelFormat, HorizontalPadding, VerticalPadding);
}

/// <summary>ArtworkDB 中的一张插图及其全部表示形式。</summary>
public sealed record ArtworkItem(
    uint ImageId,
    ulong DbTrackIdRef,
    uint SourceImageSize,
    IReadOnlyList<IthmbLocation> Locations)
{
    public IthmbLocation? LargestLocation => Locations
        .OrderByDescending(location => (long)location.Width * location.Height)
        .FirstOrDefault();
}

/// <summary>ArtworkDB 中的一种 iTHMB 文件。</summary>
public sealed record ArtworkFormatFile(uint FormatId, uint ImageSize, string FileName);

/// <summary>
/// ArtworkDB（iPod 插图数据库）。
///
/// 与 iTunesDB 一样保持无损；只有被显式修改的节点会被重建。
/// </summary>
public sealed class ArtworkDatabase
{
    private const int MhfdNextImageId = 0x1C;
    private const int MhiiChildCount = 0x0C;
    private const int MhiiImageId = 0x10;
    private const int MhiiDbTrackIdRef = 0x14;
    private const int MhiiSourceImageSize = 0x30;
    private const int MhniChildCount = 0x0C;
    private const int MhniFormatId = 0x10;
    private const int MhniOffset = 0x14;
    private const int MhniImageSize = 0x18;
    private const int MhniVerticalPadding = 0x1C;
    private const int MhniHorizontalPadding = 0x1E;
    private const int MhniHeight = 0x20;
    private const int MhniWidth = 0x22;
    private const int MhniDisplayTime = 0x24;
    private const int MhniImageSize2 = 0x28;
    private const int MhifChildCount = 0x0C;
    private const int MhifFormatId = 0x10;
    private const int MhifImageSize = 0x14;
    private const int MhodType = 0x0C;

    private readonly List<ArtworkItem> _items = [];
    private readonly List<ArtworkFormatFile> _formatFiles = [];

    private ArtworkDatabase(ChunkNode root, byte[] sourceSuffix)
    {
        Root = root;
        SourceSuffix = sourceSuffix;
        Reload();
    }

    public ChunkNode Root { get; }

    public byte[] SourceSuffix { get; }

    public DatabaseDefinition Definition => DatabaseDefinitions.ArtworkDb;

    public IReadOnlyList<ArtworkItem> Items => _items;

    public IReadOnlyList<ArtworkFormatFile> FormatFiles => _formatFiles;

    /// <summary>解析一份 ArtworkDB。</summary>
    public static ArtworkDatabase Parse(byte[] data)
    {
        if (data.Length < 12)
        {
            throw new TruncatedChunkException("ArtworkDB 内容不足 12 字节。");
        }

        var (node, next) = ChunkReader.Read(data, 0, DatabaseDefinitions.ArtworkDb);
        if (node.Marker != "mhfd")
        {
            throw new InvalidChunkDataException($"ArtworkDB 根块必须是 mhfd，实际为 {node.Marker}。");
        }

        return new ArtworkDatabase(node, next < data.Length ? data[next..] : []);
    }

    /// <summary>创建一个空的 ArtworkDB；<paramref name="rootValue"/> 来自设备档案的创建策略。</summary>
    public static ArtworkDatabase CreateEmpty(uint rootValue)
    {
        var root = ChunkNode.Create(DatabaseDefinitions.Mhfd);
        root.WriteU32(0x0C, rootValue);
        root.WriteU32(0x30, 2);
        root.WriteU32(MhfdNextImageId, 1);

        var database = new ArtworkDatabase(root, []);
        database.EnsureImageListDataset();
        database.EnsureFileListDataset();
        return database;
    }

    public byte[] Serialize() => ChunkWriter.WriteDatabase(Root, DatabaseDefinitions.ArtworkDb, SourceSuffix);

    /// <summary>重新读取插图条目与格式文件。</summary>
    public void Reload()
    {
        _items.Clear();
        _formatFiles.Clear();

        foreach (var mhii in Root.Descendants().Where(node => node.Marker == "mhii"))
        {
            _items.Add(ReadItem(mhii));
        }

        foreach (var mhif in Root.Descendants().Where(node => node.Marker == "mhif"))
        {
            _formatFiles.Add(new ArtworkFormatFile(
                mhif.HasField(MhifFormatId, 4) ? mhif.ReadU32Or(MhifFormatId) : 0,
                mhif.HasField(MhifImageSize, 4) ? mhif.ReadU32Or(MhifImageSize) : 0,
                ReadStringChild(mhif, (int)ArtworkMhodType.AlbumName)));
        }
    }

    public ArtworkItem? ItemForImageId(uint imageId) =>
        _items.FirstOrDefault(item => item.ImageId == imageId);

    public ArtworkItem? ItemForTrackPersistentId(ulong persistentId) =>
        persistentId == 0 ? null : _items.FirstOrDefault(item => item.DbTrackIdRef == persistentId);

    /// <summary>按曲目持久标识或 MHIT 中的插图引用查找插图。</summary>
    public ArtworkItem? FindForTrack(ulong persistentId, uint artworkIdRef)
    {
        var byTrack = ItemForTrackPersistentId(persistentId);
        if (byTrack is not null)
        {
            return byTrack;
        }

        return artworkIdRef == 0 ? null : ItemForImageId(artworkIdRef);
    }

    // ---- 读取 ----

    private static ArtworkItem ReadItem(ChunkNode mhii)
    {
        var locations = new List<IthmbLocation>();
        foreach (var child in mhii.Children)
        {
            if (!child.IsMhod)
            {
                continue;
            }

            var (mhni, _) = ReadContainer(child);
            if (mhni is null || mhni.Marker != "mhni")
            {
                continue;
            }

            var location = ReadLocation(mhni);
            if (location is not null)
            {
                locations.Add(location);
            }
        }

        return new ArtworkItem(
            mhii.HasField(MhiiImageId, 4) ? mhii.ReadU32Or(MhiiImageId) : 0,
            mhii.HasField(MhiiDbTrackIdRef, 8) ? mhii.ReadU64(MhiiDbTrackIdRef) : 0,
            mhii.HasField(MhiiSourceImageSize, 4) ? mhii.ReadU32Or(MhiiSourceImageSize) : 0,
            locations);
    }

    private static IthmbLocation? ReadLocation(ChunkNode mhni)
    {
        var formatId = mhni.ReadU32Or(MhniFormatId);
        if (formatId == 0)
        {
            return null;
        }

        var byteLength = (long)mhni.ReadU32Or(MhniImageSize);
        if (byteLength <= 0)
        {
            byteLength = mhni.HasField(MhniImageSize2, 4) ? mhni.ReadU32Or(MhniImageSize2) : 0;
        }

        if (byteLength <= 0)
        {
            return null;
        }

        return new IthmbLocation(
            formatId,
            ReadStringChild(mhni, (int)ArtworkMhodType.FileName),
            mhni.ReadU32Or(MhniOffset),
            byteLength,
            mhni.HasField(MhniWidth, 2) ? mhni.ReadU16(MhniWidth) : 0,
            mhni.HasField(MhniHeight, 2) ? mhni.ReadU16(MhniHeight) : 0,
            mhni.HasField(MhniHorizontalPadding, 2) ? mhni.ReadI16(MhniHorizontalPadding) : (short)0,
            mhni.HasField(MhniVerticalPadding, 2) ? mhni.ReadI16(MhniVerticalPadding) : (short)0,
            mhni.HasField(MhniDisplayTime, 4) ? (int)mhni.ReadU32Or(MhniDisplayTime) : 0);
    }

    /// <summary>
    /// ArtworkDB 的插图定位以“容器型 MHOD”保存，
    /// 其数据区本身是一个完整的 MHNI 数据块。
    /// </summary>
    public static (ChunkNode? Child, byte[] Trailing) ReadContainer(ChunkNode mhod)
    {
        var body = mhod.Body;
        if (body.Length < 12)
        {
            return (null, body);
        }

        try
        {
            var (child, next) = ChunkReader.Read(body, 0, DatabaseDefinitions.ArtworkDb);
            return (child, body[next..]);
        }
        catch (IPodDbException)
        {
            return (null, body);
        }
    }

    private static string ReadStringChild(ChunkNode parent, int mhodType)
    {
        foreach (var child in parent.Children)
        {
            if (child.IsMhod && child.HasField(MhodType, 4) && child.ReadU32(MhodType) == (uint)mhodType)
            {
                return MhodString.Read(child);
            }
        }

        return string.Empty;
    }

    // ---- 写入 ----

    /// <summary>把一张新的插图加入 ArtworkDB，并返回它的图像 ID。</summary>
    public uint AddImage(
        ulong dbTrackId,
        uint sourceImageSize,
        IReadOnlyList<(uint FormatId, string FileName, long Offset, int ByteLength, int Width, int Height)> locations)
    {
        var list = ImageListNode()
            ?? throw new IPodDbWriteException("ArtworkDB 缺少插图列表数据集。");

        var imageId = AllocateImageId();
        var spec = Definition.Chunk("mhii")!;
        var mhii = ChunkNode.Create(spec);
        mhii.WriteU32(MhiiImageId, imageId);
        mhii.WriteU64(MhiiDbTrackIdRef, dbTrackId);
        mhii.WriteU32(MhiiSourceImageSize, sourceImageSize);
        mhii.WriteU32(0x20, 0);
        mhii.WriteU32(0x24, 1);

        foreach (var location in locations)
        {
            var container = CreateLocationContainer(location);
            mhii.AddChild(container);
        }

        list.AddChild(mhii);
        var item = new ArtworkItem(imageId, dbTrackId, sourceImageSize, []);
        _items.Add(item);
        Reload();
        return imageId;
    }

    /// <summary>移除指定图像 ID 的插图；返回是否成功。</summary>
    public bool RemoveImage(uint imageId)
    {
        var node = Root.Descendants().FirstOrDefault(
            candidate => candidate.Marker == "mhii" &&
                         candidate.HasField(MhiiImageId, 4) &&
                         candidate.ReadU32(MhiiImageId) == imageId);
        if (node?.Parent is null)
        {
            return false;
        }

        node.Parent.Children.Remove(node);
        Reload();
        return true;
    }

    /// <summary>移除引用指定曲目持久标识的全部插图。</summary>
    public int RemoveImagesForTrack(ulong dbTrackId)
    {
        if (dbTrackId == 0)
        {
            return 0;
        }

        var removed = 0;
        foreach (var node in Root.Descendants()
                     .Where(candidate => candidate.Marker == "mhii" &&
                                         candidate.HasField(MhiiDbTrackIdRef, 8) &&
                                         candidate.ReadU64(MhiiDbTrackIdRef) == dbTrackId)
                     .ToArray())
        {
            if (node.Parent is not null)
            {
                node.Parent.Children.Remove(node);
                removed++;
            }
        }

        if (removed > 0)
        {
            Reload();
        }

        return removed;
    }

    private ChunkNode CreateLocationContainer(
        (uint FormatId, string FileName, long Offset, int ByteLength, int Width, int Height) location)
    {
        var mhodSpec = Definition.Chunk("mhod")!;
        var container = ChunkNode.Create(mhodSpec);
        container.WriteU32(MhodType, (uint)ArtworkMhodType.ThumbnailImage);

        // 数据区为一个完整的 MHNI 数据块。
        var mhniSpec = Definition.Chunk("mhni")!;
        var mhni = ChunkNode.Create(mhniSpec);
        mhni.WriteU32(MhniFormatId, location.FormatId);
        mhni.WriteU32(MhniOffset, (uint)location.Offset);
        mhni.WriteU32(MhniImageSize, (uint)location.ByteLength);
        mhni.WriteU16(MhniWidth, (ushort)location.Width);
        mhni.WriteU16(MhniHeight, (ushort)location.Height);
        mhni.WriteI16(MhniVerticalPadding, 0);
        mhni.WriteI16(MhniHorizontalPadding, 0);
        mhni.WriteU32(MhniDisplayTime, 0);
        mhni.WriteU32(MhniImageSize2, (uint)location.ByteLength);

        if (!string.IsNullOrEmpty(location.FileName))
        {
            var fileName = MhodString.Create(
                mhodSpec, (int)ArtworkMhodType.FileName, location.FileName, encodingIndicator: 2);
            mhni.AddChild(fileName);
        }

        using var buffer = new MemoryStream();
        buffer.Write(ChunkWriter.WriteChunkNode(mhni, Definition));
        container.Body = buffer.ToArray();
        return container;
    }

    private ChunkNode? ImageListNode()
    {
        var dataset = Dataset(1);
        return dataset is null ? EnsureImageListDataset() : dataset.Child("mhli");
    }

    private ChunkNode EnsureImageListDataset()
    {
        var dataset = Dataset(1);
        if (dataset is not null)
        {
            if (dataset.Child("mhli") is null)
            {
                dataset.AddChild(ChunkNode.Create(Definition.Chunk("mhli")!));
            }

            return dataset.Child("mhli")!;
        }

        var mhsd = ChunkNode.Create(Definition.Chunk("mhsd")!);
        mhsd.WriteU16(0x0C, 1);
        var list = ChunkNode.Create(Definition.Chunk("mhli")!);
        mhsd.AddChild(list);
        Root.AddChild(mhsd);
        return list;
    }

    private ChunkNode EnsureFileListDataset()
    {
        var dataset = Dataset(3);
        if (dataset is not null)
        {
            if (dataset.Child("mhlf") is null)
            {
                dataset.AddChild(ChunkNode.Create(Definition.Chunk("mhlf")!));
            }

            return dataset.Child("mhlf")!;
        }

        var mhsd = ChunkNode.Create(Definition.Chunk("mhsd")!);
        mhsd.WriteU16(0x0C, 3);
        var list = ChunkNode.Create(Definition.Chunk("mhlf")!);
        mhsd.AddChild(list);
        Root.AddChild(mhsd);
        return list;
    }

    private ChunkNode? Dataset(int datasetType) =>
        Root.Children.FirstOrDefault(node =>
            node.Marker == "mhsd" &&
            node.HasField(0x0C, 2) &&
            node.ReadU16(0x0C) == datasetType);

    private uint AllocateImageId()
    {
        var next = Root.HasField(MhfdNextImageId, 4) ? Root.ReadU32Or(MhfdNextImageId) : 1u;
        var used = _items.Select(item => item.ImageId).ToHashSet();
        while (used.Contains(next) || next == 0)
        {
            next++;
        }

        Root.WriteU32(MhfdNextImageId, next + 1);
        return next;
    }

    /// <summary>确保某个 iTHMB 文件在格式列表中存在。</summary>
    public void EnsureFormatFile(uint formatId, uint imageSize, string fileName)
    {
        var list = EnsureFileListDataset();
        foreach (var child in list.Children)
        {
            if (child.Marker == "mhif" &&
                child.HasField(MhifFormatId, 4) &&
                child.ReadU32(MhifFormatId) == formatId)
            {
                return;
            }
        }

        var mhif = ChunkNode.Create(Definition.Chunk("mhif")!);
        mhif.WriteU32(MhifFormatId, formatId);
        mhif.WriteU32(MhifImageSize, imageSize);
        mhif.WriteU32(MhifChildCount, 0);
        var nameSpec = Definition.Chunk("mhod")!;
        mhif.AddChild(MhodString.Create(nameSpec, (int)ArtworkMhodType.AlbumName, fileName, encodingIndicator: 2));
        list.AddChild(mhif);
        Reload();
    }

    /// <summary>计算下一个可用的 iTHMB 文件序号（用于生成形如 F1061_1.ithmb 的文件名）。</summary>
    public string NextIthmbFileName(uint formatId)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in Root.Descendants().Where(node => node.IsMhod))
        {
            if (child.HasField(MhodType, 4) &&
                (child.ReadU32(MhodType) is (uint)ArtworkMhodType.FileName or (uint)ArtworkMhodType.AlbumName))
            {
                used.Add(MhodString.Read(child));
            }
        }

        foreach (var file in _formatFiles)
        {
            used.Add(file.FileName);
        }

        for (var index = 1; index < 100000; index++)
        {
            var candidate = $"F{formatId}_{index}.ithmb";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"F{formatId}_{Guid.NewGuid():N}.ithmb";
    }

    /// <summary>把文件名字符串补齐到 4 字节对齐（iTHMB 分配所需）。</summary>
    public static int AlignTo4(int value) => (value + 3) & ~3;

    public override string ToString() =>
        $"ArtworkDB：{_items.Count} 张插图，{_formatFiles.Count} 种格式（编码 {Encoding.ASCII.GetString(Root.Header, 0, 4)}）";
}
