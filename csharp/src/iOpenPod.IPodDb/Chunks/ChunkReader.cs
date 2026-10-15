using System.Buffers.Binary;
using System.Text;

namespace IOpenPod.IPodDb.Chunks;

/// <summary>
/// 定义驱动的无损数据块读取器。
///
/// 未知标记的数据块会被完整保留为不透明节点，因此未知格式永远不会被丢弃。
/// </summary>
public static class ChunkReader
{
    private const int GenericHeaderSize = 12;

    /// <summary>解析完整的数据库字节。</summary>
    public static ChunkNode ReadDatabase(byte[] data, DatabaseDefinition definition)
    {
        var (node, next) = Read(data, 0, definition);
        if (node.Marker != definition.Root.Marker)
        {
            throw new InvalidChunkDataException(
                $"数据库根必须是 {definition.Root.Marker}，实际为 {node.Marker}。");
        }

        return node;
    }

    /// <summary>从指定偏移解析一个数据块，返回节点与下一个偏移。</summary>
    public static (ChunkNode Node, int NextOffset) Read(byte[] data, int offset, DatabaseDefinition definition)
    {
        if (offset < 0 || offset + GenericHeaderSize > data.Length)
        {
            throw new TruncatedChunkException($"偏移 {offset} 处的数据块头部不完整。");
        }

        var marker = Encoding.ASCII.GetString(data, offset, 4);
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
        var lengthOrChildCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8));

        if (headerLength < GenericHeaderSize)
        {
            throw new InvalidChunkLengthException(
                $"{marker} 在偏移 {offset} 声明的头部长度 {headerLength} 小于基本头部长度 {GenericHeaderSize}。");
        }

        var spec = definition.Chunk(marker);
        var minimumHeaderSize = spec?.MinimumHeaderSize ?? GenericHeaderSize;
        if (headerLength < minimumHeaderSize)
        {
            throw new InvalidChunkLengthException(
                $"{marker} 在偏移 {offset} 声明的头部长度 {headerLength} 小于其最小长度 {minimumHeaderSize}。");
        }

        var headerEnd = offset + headerLength;
        if (headerEnd > data.Length)
        {
            throw new TruncatedChunkException($"{marker} 的头部超出数据末尾（偏移 {offset}）。");
        }

        if (spec is null)
        {
            // 未知数据块：完整保留为不透明节点。
            var unknownSpec = new ChunkSpec(
                marker, "未知数据块", [headerLength], GenericHeaderSize, ChunkExtent.Length, ChunkBody.Opaque, []);
            var chunkEndUnknown = offset + lengthOrChildCount;
            if (chunkEndUnknown > data.Length || chunkEndUnknown < headerEnd)
            {
                throw new TruncatedChunkException($"{marker} 的长度字段超出数据末尾（偏移 {offset}）。");
            }

            var unknown = new ChunkNode(
                unknownSpec,
                data[offset..headerEnd],
                data[headerEnd..chunkEndUnknown])
            {
                SourceOffset = offset,
            };

            return (unknown, chunkEndUnknown);
        }

        var isListChunk = spec.Extent == ChunkExtent.ChildCount;
        int chunkEnd;
        int[] childCounts;
        string? datasetChildMarker = null;

        if (isListChunk)
        {
            chunkEnd = -1;
            childCounts = [lengthOrChildCount];
        }
        else
        {
            chunkEnd = offset + lengthOrChildCount;
            if (chunkEnd > data.Length)
            {
                throw new TruncatedChunkException(
                    $"{marker} 的长度 {lengthOrChildCount} 超出数据末尾（偏移 {offset}）。");
            }

            if (chunkEnd < headerEnd)
            {
                throw new InvalidChunkLengthException(
                    $"{marker} 的长度小于其头部（偏移 {offset}）。");
            }

            if (spec.Body == ChunkBody.Children)
            {
                childCounts = DatabaseDefinition.ChildCountFieldsFor(spec)
                    .Select(field => (int)BinaryPrimitives.ReadUInt32LittleEndian(
                        data.AsSpan(offset + field.Offset, field.Size)))
                    .ToArray();
            }
            else if (spec.Body == ChunkBody.Dataset)
            {
                (childCounts, datasetChildMarker) = ReadDatasetChildCount(data, offset, headerLength, definition);
            }
            else
            {
                childCounts = [];
            }
        }

        var node = new ChunkNode(spec, data[offset..headerEnd]) { SourceOffset = offset };
        var childOffset = headerEnd;

        if (spec.Body == ChunkBody.Mhod || spec.Body == ChunkBody.Opaque)
        {
            node.Body = data[headerEnd..chunkEnd];
        }
        else if (spec.Body == ChunkBody.Dataset && childCounts.Length == 0)
        {
            // 不透明数据集：数据区原样保留。
            node.Body = data[headerEnd..chunkEnd];
        }

        for (var groupIndex = 0; groupIndex < childCounts.Length; groupIndex++)
        {
            var childCount = childCounts[groupIndex];
            if (childCount < 0)
            {
                throw new InvalidChunkDataException($"{marker} 的子块数量为负数。");
            }

            for (var i = 0; i < childCount; i++)
            {
                if (chunkEnd > 0 && childOffset >= chunkEnd)
                {
                    throw new InvalidChunkDataException(
                        $"{marker} 声明了更多子块，但其长度已经用尽。");
                }

                var (child, nextOffset) = Read(data, childOffset, definition);
                if (nextOffset <= childOffset)
                {
                    throw new InvalidChunkDataException($"偏移 {childOffset} 处的子块没有推进解析位置。");
                }

                if (chunkEnd > 0 && nextOffset > chunkEnd)
                {
                    throw new InvalidChunkDataException(
                        $"偏移 {childOffset} 处的子块超出了父块 {marker} 的范围。");
                }

                ValidateChildPlacement(spec, groupIndex, child, definition, datasetChildMarker);
                node.AddChild(child);
                childOffset = nextOffset;
            }
        }

        if (chunkEnd < 0)
        {
            chunkEnd = childOffset;
        }
        else if (childOffset < chunkEnd)
        {
            // 子块之后的保留字节必须原样写回。
            node.Trailing = data[childOffset..chunkEnd];
        }

        return (node, chunkEnd);
    }

    private static void ValidateChildPlacement(
        ChunkSpec spec,
        int groupIndex,
        ChunkNode child,
        DatabaseDefinition definition,
        string? datasetChildMarker)
    {
        if (spec.Body == ChunkBody.Dataset)
        {
            if (datasetChildMarker is null || definition.Chunk(child.Marker) is null)
            {
                return;
            }

            if (child.Marker != datasetChildMarker)
            {
                throw new InvalidChunkDataException(
                    $"数据集要求 {datasetChildMarker} 子块，实际为 {child.Marker}。");
            }

            return;
        }

        if (spec.Body != ChunkBody.Children || groupIndex >= spec.ChildGroups.Count)
        {
            return;
        }

        var allowed = spec.ChildGroups[groupIndex];
        if (allowed.Count == 0 || allowed.Contains(child.Marker))
        {
            return;
        }

        // 未知数据块可以出现在任何分组中，以保留前向兼容性。
        if (definition.Chunk(child.Marker) is null)
        {
            return;
        }

        throw new InvalidChunkDataException(
            $"{spec.Marker} 的第 {groupIndex + 1} 组子块应为 {string.Join('/', allowed)}，实际为 {child.Marker}。");
    }

    private static (int[] ChildCounts, string? ChildMarker) ReadDatasetChildCount(
        byte[] data,
        int offset,
        int headerLength,
        DatabaseDefinition definition)
    {
        var field = definition.DatasetTypeField;
        if (headerLength < field.Offset + field.Size)
        {
            return ([], null);
        }

        var span = data.AsSpan(offset + field.Offset, field.Size);
        var datasetType = field.Size == 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(span)
            : (int)BinaryPrimitives.ReadUInt32LittleEndian(span);

        var dataset = definition.Dataset(datasetType);
        return dataset?.ChildMarker is null ? ([], null) : ([1], dataset.ChildMarker);
    }
}
