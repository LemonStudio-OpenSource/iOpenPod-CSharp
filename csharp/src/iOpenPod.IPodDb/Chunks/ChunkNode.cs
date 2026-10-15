using System.Buffers.Binary;

namespace IOpenPod.IPodDb.Chunks;

/// <summary>
/// 无损数据块节点。
///
/// 设计要点：头部字节与数据区字节都被原样保留，任何未被显式修改的字节
/// 在写回时都会逐字节重现（包括未知字段、填充与尾部数据）。
/// </summary>
public sealed class ChunkNode
{
    /// <summary>头部字节（长度等于写入时的 header_length）。</summary>
    public byte[] Header { get; set; }

    /// <summary>子块列表（仅对子块型数据块有意义）。</summary>
    public List<ChunkNode> Children { get; } = [];

    /// <summary>数据区字节；子块型数据块使用 <see cref="Trailing"/>。</summary>
    public byte[] Body { get; set; }

    /// <summary>最后一个子块之后的保留字节。</summary>
    public byte[] Trailing { get; set; }

    public ChunkNode(ChunkSpec spec, byte[] header, byte[]? body = null, byte[]? trailing = null)
    {
        Spec = spec;
        Header = header;
        Body = body ?? [];
        Trailing = trailing ?? [];
    }

    public ChunkSpec Spec { get; }

    public string Marker => Spec.Marker;

    /// <summary>父数据块；根节点与游离节点为 <c>null</c>。</summary>
    public ChunkNode? Parent { get; private set; }

    /// <summary>原始文件偏移；新建节点为 0。</summary>
    public int SourceOffset { get; internal set; }

    /// <summary>source_offset 处保存着原始头部长度。</summary>
    public bool HasRetainedHeader => Header.Length > 0;

    /// <summary>整块在文件中的长度（写入时重新计算）。</summary>
    public int TotalLength
    {
        get
        {
            if (Spec.Extent == ChunkExtent.ChildCount)
            {
                var total = Header.Length;
                foreach (var child in Children)
                {
                    total += child.TotalLength;
                }

                return total + Trailing.Length;
            }

            if (Spec.Body == ChunkBody.Children || Spec.Body == ChunkBody.Dataset)
            {
                var total = Header.Length;
                foreach (var child in Children)
                {
                    total += child.TotalLength;
                }

                return total + Body.Length + Trailing.Length;
            }

            return Header.Length + Body.Length;
        }
    }

    /// <summary>整块在内存中的线性长度，包含 Body 或子块。</summary>
    public bool IsMhod => Spec.Body == ChunkBody.Mhod;

    // ---- 头部字段读写（绝对偏移，跨头部与数据区） ----

    public uint ReadU32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));

    public int ReadI32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Slice(offset, 4));

    public ulong ReadU64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(Slice(offset, 8));

    public ushort ReadU16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(offset, 2));

    public short ReadI16(int offset) => BinaryPrimitives.ReadInt16LittleEndian(Slice(offset, 2));

    public byte ReadU8(int offset) => Slice(offset, 1)[0];

    public float ReadF32(int offset) => BinaryPrimitives.ReadSingleLittleEndian(Slice(offset, 4));

    public void WriteU32(int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(Slice(offset, 4), value);

    public void WriteI32(int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(Slice(offset, 4), value);

    public void WriteU64(int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(Slice(offset, 8), value);

    public void WriteU16(int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(Slice(offset, 2), value);

    public void WriteI16(int offset, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(Slice(offset, 2), value);

    public void WriteU8(int offset, byte value) => Slice(offset, 1)[0] = value;

    public void WriteF32(int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(Slice(offset, 4), value);

    /// <summary>判断某个绝对偏移是否落在头部内。</summary>
    public bool IsInHeader(int offset) => offset < Header.Length;

    /// <summary>判断某个字段是否完整存在于当前头部中。</summary>
    public bool HasField(int offset, int size) => offset + size <= Header.Length;

    /// <summary>安全读取 u32；字段不存在时返回默认值。</summary>
    public uint ReadU32Or(int offset, uint fallback = 0) =>
        HasField(offset, 4) ? BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(offset, 4)) : fallback;

    /// <summary>安全写入 u32；字段不存在时不执行任何操作。</summary>
    public bool TryWriteU32(int offset, uint value)
    {
        if (!HasField(offset, 4))
        {
            return false;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(offset, 4), value);
        return true;
    }

    /// <summary>安全写入 u8；字段不存在时不执行任何操作。</summary>
    public bool TryWriteU8(int offset, byte value)
    {
        if (!HasField(offset, 1))
        {
            return false;
        }

        Header[offset] = value;
        return true;
    }

    /// <summary>安全写入 u16；字段不存在时不执行任何操作。</summary>
    public bool TryWriteU16(int offset, ushort value)
    {
        if (!HasField(offset, 2))
        {
            return false;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(Header.AsSpan(offset, 2), value);
        return true;
    }

    /// <summary>安全写入 u64；字段不存在时不执行任何操作。</summary>
    public bool TryWriteU64(int offset, ulong value)
    {
        if (!HasField(offset, 8))
        {
            return false;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(Header.AsSpan(offset, 8), value);
        return true;
    }

    /// <summary>安全写入 i32；字段不存在时不执行任何操作。</summary>
    public bool TryWriteI32(int offset, int value)
    {
        if (!HasField(offset, 4))
        {
            return false;
        }

        BinaryPrimitives.WriteInt32LittleEndian(Header.AsSpan(offset, 4), value);
        return true;
    }

    /// <summary>安全写入 f32；字段不存在时不执行任何操作。</summary>
    public bool TryWriteF32(int offset, float value)
    {
        if (!HasField(offset, 4))
        {
            return false;
        }

        BinaryPrimitives.WriteSingleLittleEndian(Header.AsSpan(offset, 4), value);
        return true;
    }


    /// <summary>读取一段跨越头部与数据区的字节。</summary>
    public Span<byte> Slice(int offset, int length)
    {
        var headerLength = Header.Length;
        if (offset < headerLength && offset + length <= headerLength)
        {
            return Header.AsSpan(offset, length);
        }

        if (offset >= headerLength && offset + length <= headerLength + Body.Length)
        {
            return Body.AsSpan(offset - headerLength, length);
        }

        throw new InvalidChunkDataException(
            $"数据块 {Marker} 在偏移 {offset} 处请求 {length} 字节，超出头部（{headerLength}）与数据区（{Body.Length}）范围。");
    }

    /// <summary>读取一段尾部数据（不抛异常，越界时返回空）。</summary>
    public byte[] ReadBytesSafe(int offset, int length)
    {
        try
        {
            return Slice(offset, length).ToArray();
        }
        catch (InvalidChunkDataException)
        {
            return [];
        }
    }

    /// <summary>按标记查找第一个子块。</summary>
    public ChunkNode? Child(string marker) =>
        Children.FirstOrDefault(child => child.Marker == marker);

    /// <summary>按标记枚举子块。</summary>
    public IEnumerable<ChunkNode> ChildrenOf(string marker) =>
        Children.Where(child => child.Marker == marker);

    /// <summary>插入子块，保持同组子块相对顺序。</summary>
    public void AddChild(ChunkNode child)
    {
        child.Parent?.Children.Remove(child);
        child.Parent = this;
        Children.Add(child);
    }

    public bool RemoveChild(ChunkNode child)
    {
        if (Children.Remove(child))
        {
            child.Parent = null;
            return true;
        }

        return false;
    }

    /// <summary>创建具有默认头部长度的新数据块。</summary>
    public static ChunkNode Create(ChunkSpec spec, int? headerSize = null)
    {
        var size = headerSize ?? spec.DefaultHeaderSize;
        var header = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)size);
        System.Text.Encoding.ASCII.GetBytes(spec.Marker, header.AsSpan(0, 4));
        return new ChunkNode(spec, header);
    }

    /// <summary>把头部与数据区拼接为一个连续缓冲区。</summary>
    public byte[] ToLogical()
    {
        if (Body.Length == 0)
        {
            return (byte[])Header.Clone();
        }

        var logical = new byte[Header.Length + Body.Length];
        Array.Copy(Header, logical, Header.Length);
        Array.Copy(Body, 0, logical, Header.Length, Body.Length);
        return logical;
    }

    /// <summary>用连续缓冲区替换头部与数据区；头部长度保持不变。</summary>
    public void SetLogical(byte[] logical)
    {
        if (logical.Length < Header.Length)
        {
            throw new IPodDbWriteException(
                $"数据块 {Marker} 的新内容长度 {logical.Length} 小于头部长度 {Header.Length}。");
        }

        var header = new byte[Header.Length];
        Array.Copy(logical, header, Header.Length);
        Header = header;

        var body = new byte[logical.Length - Header.Length];
        Array.Copy(logical, Header.Length, body, 0, body.Length);
        Body = body;
    }

    /// <summary>深拷贝子树。</summary>
    public ChunkNode Clone()
    {
        var copy = new ChunkNode(Spec, (byte[])Header.Clone(), (byte[])Body.Clone(), (byte[])Trailing.Clone())
        {
            SourceOffset = SourceOffset,
        };

        foreach (var child in Children)
        {
            copy.AddChild(child.Clone());
        }

        return copy;
    }

    /// <summary>递归枚举整棵子树（含自身）。</summary>
    public IEnumerable<ChunkNode> Descendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.Descendants())
            {
                yield return node;
            }
        }
    }
}
