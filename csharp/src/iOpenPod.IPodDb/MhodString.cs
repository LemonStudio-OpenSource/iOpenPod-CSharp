using System.Text;
using IOpenPod.IPodDb.Chunks;

namespace IOpenPod.IPodDb;

/// <summary>
/// 字符串型 MHOD 的读写。
///
/// 布局：块头 24 字节（标记、长度、类型…），随后是
/// 编码指示（0x18，u32）、字节长度（0x1C，u32）、常量 1（0x20）、保留（0x24），
/// 字符串正文从 0x28 开始。编码指示 1 表示 UTF-16LE，2 表示 UTF-8。
/// </summary>
public static class MhodString
{
    public const int PrefixEnd = 0x28;
    public const int EncodingIndicatorOffset = 0x18;
    public const int ByteLengthOffset = 0x1C;
    public const int ConstantOneOffset = 0x20;

    /// <summary>读取字符串型 MHOD 的正文。</summary>
    public static string Read(ChunkNode node)
    {
        if (!node.IsMhod)
        {
            throw new InvalidChunkDataException($"{node.Marker} 不是数据对象，无法读取字符串。");
        }

        var logical = node.ToLogical();
        if (logical.Length < PrefixEnd)
        {
            return string.Empty;
        }

        var encoding = (int)node.ReadU32(EncodingIndicatorOffset);
        var byteLength = (int)node.ReadU32(ByteLengthOffset);
        if (byteLength < 0 || PrefixEnd + byteLength > logical.Length)
        {
            byteLength = Math.Max(0, logical.Length - PrefixEnd);
        }

        return encoding == 2
            ? Encoding.UTF8.GetString(logical, PrefixEnd, byteLength)
            : Encoding.Unicode.GetString(logical, PrefixEnd, byteLength);
    }

    /// <summary>
    /// 写入字符串型 MHOD 的正文，保留块头其余字节与正文之后的尾部数据。
    /// </summary>
    public static void Write(ChunkNode node, string value, int encodingIndicator = 1)
    {
        if (!node.IsMhod)
        {
            throw new IPodDbWriteException($"{node.Marker} 不是数据对象，无法写入字符串。");
        }

        var logical = node.ToLogical();
        var originalEncoding = logical.Length >= PrefixEnd ? (int)node.ReadU32(EncodingIndicatorOffset) : 1;
        var originalByteLength = logical.Length >= PrefixEnd ? (int)node.ReadU32(ByteLengthOffset) : 0;
        if (encodingIndicator < 0)
        {
            encodingIndicator = originalEncoding is 1 or 2 ? originalEncoding : 1;
        }

        var codec = encodingIndicator == 2 ? Encoding.UTF8 : Encoding.Unicode;
        var encoded = codec.GetBytes(value ?? string.Empty);
        if (encodingIndicator != 2 && encoded.Length % 2 != 0)
        {
            encoded = [.. encoded, 0];
        }

        // 保留正文之后的原始尾部数据。
        var trailingStart = PrefixEnd + originalByteLength;
        var trailing = trailingStart < logical.Length
            ? logical[trailingStart..]
            : [];

        var prefixLength = Math.Max(PrefixEnd, node.Header.Length);
        var rebuilt = new byte[prefixLength + encoded.Length + trailing.Length];
        Array.Copy(logical, 0, rebuilt, 0, Math.Min(prefixLength, logical.Length));
        Array.Copy(encoded, 0, rebuilt, prefixLength, encoded.Length);
        Array.Copy(trailing, 0, rebuilt, prefixLength + encoded.Length, trailing.Length);

        node.SetLogical(rebuilt);
        node.WriteU32(EncodingIndicatorOffset, (uint)encodingIndicator);
        node.WriteU32(ByteLengthOffset, (uint)encoded.Length);
    }

    /// <summary>构造一个新的字符串型 MHOD。</summary>
    public static ChunkNode Create(
        ChunkSpec spec,
        int mhodType,
        string value,
        int encodingIndicator = 1,
        int headerSize = 24)
    {
        var node = ChunkNode.Create(spec, headerSize);
        node.WriteU32(0x0C, (uint)mhodType);
        Write(node, value, encodingIndicator);

        // 新对象的前缀常量与保留字段使用写入器默认值。
        var logical = node.ToLogical();
        if (logical.Length >= ConstantOneOffset + 4)
        {
            node.WriteU32(ConstantOneOffset, 1);
        }

        return node;
    }
}
