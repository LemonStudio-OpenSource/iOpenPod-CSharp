namespace IOpenPod.IPodDb.Chunks;

/// <summary>数据块的“长度”字段含义。</summary>
public enum ChunkExtent
{
    /// <summary>偏移 0x08 处是整块总长度。</summary>
    Length,

    /// <summary>偏移 0x08 处是子块数量，整块长度由子块推出。</summary>
    ChildCount,
}

/// <summary>数据块的数据区形态。</summary>
public enum ChunkBody
{
    /// <summary>由同构子块组成。</summary>
    Children,

    /// <summary>MHSD 数据集：恰好一个结构化子块，或一段不透明数据。</summary>
    Dataset,

    /// <summary>MHOD 类型化数据对象。</summary>
    Mhod,

    /// <summary>不透明字节。</summary>
    Opaque,
}

/// <summary>头部的子块计数字段。</summary>
public readonly record struct ChildCountField(int Offset, int Size);

/// <summary>MHSD 数据集类型字段在头部中的位置与宽度。</summary>
public readonly record struct DatasetTypeField(int Offset, int Size);

/// <summary>
/// 一种数据块格式事实。解析器与写入器共享同一份定义，
/// 因此格式知识在工程中只存在一处。
/// </summary>
public sealed record ChunkSpec(
    string Marker,
    string Purpose,
    IReadOnlyList<int> HeaderSizes,
    int MinimumHeaderSize,
    ChunkExtent Extent,
    ChunkBody Body,
    IReadOnlyList<IReadOnlyList<string>> ChildGroups)
{
    /// <summary>新建数据块时使用的头部长度。</summary>
    public int DefaultHeaderSize => HeaderSizes[^1];

    /// <summary>允许出现的所有子块标记。</summary>
    public IReadOnlySet<string> KnownChildMarkers { get; } =
        ChildGroups.SelectMany(group => group).ToHashSet(StringComparer.Ordinal);

    public bool KnowsChild(string marker) => KnownChildMarkers.Contains(marker);
}

/// <summary>MHSD 数据集：一种数据集类型对应的已知子块结构。</summary>
public sealed record DatasetSpec(int DatasetType, string Purpose, string? ChildMarker);

/// <summary>一个数据库格式的完整结构定义。</summary>
public sealed class DatabaseDefinition
{
    private readonly Dictionary<string, ChunkSpec> _chunks;
    private readonly Dictionary<int, DatasetSpec> _datasets;

    public DatabaseDefinition(
        ChunkSpec root,
        IReadOnlyList<ChunkSpec> chunks,
        IReadOnlyList<DatasetSpec> datasets,
        DatasetTypeField? datasetTypeField = null)
    {
        Root = root;
        DatasetTypeField = datasetTypeField ?? new DatasetTypeField(0x0C, 4);

        _chunks = new Dictionary<string, ChunkSpec>(StringComparer.Ordinal);
        foreach (var chunk in chunks)
        {
            if (!_chunks.TryAdd(chunk.Marker, chunk))
            {
                throw new ArgumentException($"数据块定义重复：{chunk.Marker}");
            }
        }

        if (!_chunks.TryGetValue(root.Marker, out var registered) || !ReferenceEquals(registered, root))
        {
            throw new ArgumentException("数据库根必须注册在数据块定义表中。");
        }

        foreach (var chunk in chunks)
        {
            foreach (var child in chunk.KnownChildMarkers)
            {
                if (!_chunks.ContainsKey(child))
                {
                    throw new ArgumentException($"{chunk.Marker} 引用了未注册的子块 {child}");
                }
            }
        }

        _datasets = new Dictionary<int, DatasetSpec>();
        foreach (var dataset in datasets)
        {
            if (!_datasets.TryAdd(dataset.DatasetType, dataset))
            {
                throw new ArgumentException($"数据集定义重复：{dataset.DatasetType}");
            }

            if (dataset.ChildMarker is not null && !_chunks.ContainsKey(dataset.ChildMarker))
            {
                throw new ArgumentException(
                    $"数据集 {dataset.DatasetType} 引用了未注册的子块 {dataset.ChildMarker}");
            }
        }
    }

    public ChunkSpec Root { get; }

    /// <summary>数据集类型字段的位置与宽度。</summary>
    public DatasetTypeField DatasetTypeField { get; }

    public ChunkSpec? Chunk(string marker) => _chunks.GetValueOrDefault(marker);

    public DatasetSpec? Dataset(int datasetType) => _datasets.GetValueOrDefault(datasetType);

    /// <summary>返回某个数据块头部中需要写入的子块计数字段。</summary>
    public static IReadOnlyList<ChildCountField> ChildCountFieldsFor(ChunkSpec spec) => spec.Marker switch
    {
        // 头部中的子块计数与子块分组一一对应。
        "mhbd" => [new ChildCountField(0x14, 4)],
        "mhit" => [new ChildCountField(0x0C, 4)],
        "mhip" => [new ChildCountField(0x0C, 4)],
        "mhyp" => [new ChildCountField(0x0C, 4), new ChildCountField(0x10, 4)],
        "mhii" => [new ChildCountField(0x0C, 4)],
        "mhni" => [new ChildCountField(0x0C, 4)],
        "mhif" => [new ChildCountField(0x0C, 4)],
        "mhba" => [new ChildCountField(0x0C, 4), new ChildCountField(0x10, 4)],
        "mhia" => [new ChildCountField(0x0C, 4)],
        "mhfd" => [new ChildCountField(0x14, 4)],
        _ => [],
    };
}
