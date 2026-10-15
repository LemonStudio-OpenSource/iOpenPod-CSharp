using System.Buffers.Binary;
using System.Text;

namespace IOpenPod.IPodDb.Chunks;

/// <summary>
/// 定义驱动的无损数据块写入器。
///
/// 只重算长度与子块计数这两类结构字段，其余字节逐字节保留。
/// </summary>
public static class ChunkWriter
{
    /// <summary>序列化整棵数据库树。</summary>
    public static byte[] WriteDatabase(ChunkNode root, DatabaseDefinition definition, byte[]? sourceSuffix = null)
    {
        if (root.Marker != definition.Root.Marker)
        {
            throw new IPodDbWriteException(
                $"数据库根必须是 {definition.Root.Marker}，实际为 {root.Marker}。");
        }

        using var stream = new MemoryStream();
        WriteChunk(stream, root, definition);

        if (sourceSuffix is { Length: > 0 })
        {
            stream.Write(sourceSuffix);
        }

        return stream.ToArray();
    }

    /// <summary>序列化单个数据块（用于 ArtworkDB 中的嵌套容器）。</summary>
    public static byte[] WriteChunkNode(ChunkNode node, DatabaseDefinition definition)
    {
        using var stream = new MemoryStream();
        WriteChunk(stream, node, definition);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, ChunkNode node, DatabaseDefinition definition)
    {
        var header = (byte[])node.Header.Clone();
        if (header.Length < 12)
        {
            throw new IPodDbWriteException($"{node.Marker} 的头部长度不足 12 字节。");
        }

        byte[] body;
        if (node.Spec.Body is ChunkBody.Mhod or ChunkBody.Opaque)
        {
            body = node.Body;
        }
        else if (node.Spec.Body == ChunkBody.Dataset && node.Children.Count == 0)
        {
            body = node.Body;
        }
        else
        {
            using var bodyStream = new MemoryStream();
            foreach (var child in node.Children)
            {
                WriteChunk(bodyStream, child, definition);
            }

            if (node.Trailing.Length > 0)
            {
                bodyStream.Write(node.Trailing);
            }

            body = bodyStream.ToArray();
        }

        WriteChildCounts(header, node);

        var lengthOrChildCount = node.Spec.Extent == ChunkExtent.ChildCount
            ? node.Children.Count
            : header.Length + body.Length;

        Encoding.ASCII.GetBytes(node.Marker, header.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)header.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)lengthOrChildCount);

        stream.Write(header);
        stream.Write(body);
    }

    private static void WriteChildCounts(byte[] header, ChunkNode node)
    {
        var fields = DatabaseDefinition.ChildCountFieldsFor(node.Spec);
        if (fields.Count == 0)
        {
            return;
        }

        if (fields.Count == 1)
        {
            WriteCount(header, fields[0], node.Children.Count);
            return;
        }

        var groups = node.Spec.ChildGroups;
        var groupCounts = new int[groups.Count];
        for (var i = 0; i < groups.Count; i++)
        {
            if (i >= fields.Count)
            {
                break;
            }

            foreach (var child in node.Children)
            {
                if (groups[i].Contains(child.Marker))
                {
                    groupCounts[i]++;
                }
            }
        }

        if (groupCounts.Sum() == node.Children.Count)
        {
            for (var i = 0; i < fields.Count && i < groupCounts.Length; i++)
            {
                WriteCount(header, fields[i], groupCounts[i]);
            }

            return;
        }

        // 标记分组无法覆盖全部子块时，保留原有计数顺序，仅在总数一致时沿用。
        var declared = new int[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            declared[i] = ReadCount(header, fields[i]);
        }

        if (declared.Sum() == node.Children.Count)
        {
            return;
        }

        throw new IPodDbWriteException(
            $"{node.Marker} 在结构编辑后无法推断子块分组计数。");
    }

    private static void WriteCount(byte[] header, ChildCountField field, int value)
    {
        if (field.Offset + field.Size > header.Length)
        {
            return;
        }

        if (field.Size == 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(field.Offset, 4), (uint)value);
        }
        else if (field.Size == 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(field.Offset, 2), (ushort)value);
        }
    }

    private static int ReadCount(byte[] header, ChildCountField field)
    {
        if (field.Offset + field.Size > header.Length)
        {
            return 0;
        }

        return field.Size == 4
            ? (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(field.Offset, 4))
            : BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(field.Offset, 2));
    }
}
