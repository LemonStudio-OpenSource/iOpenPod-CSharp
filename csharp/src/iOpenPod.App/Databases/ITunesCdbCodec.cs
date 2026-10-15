using System.Buffers.Binary;
using System.IO.Compression;
using IOpenPod.IPodDb;

namespace IOpenPod.App.Databases;

/// <summary>已解包的 iTunesCDB 及其精确保留的原始字节。</summary>
public sealed record ITunesCdb(
    byte[] LogicalBytes,
    byte[] SourceBytes,
    byte[] StreamTrailingData,
    byte[] FileTrailingData)
{
    public static readonly ITunesCdb Empty = new([], [], [], []);
}

/// <summary>
/// iTunesCDB 的 zlib 封装。
///
/// 根 mhbd 头部保持未压缩，头部之后的全部字节是一个 zlib 流。
/// 签名必须作用在压缩后的物理字节上，因为固件校验的就是它们。
/// </summary>
public static class ITunesCdbCodec
{
    private const int GenericHeaderSize = 16;
    private const int CdbFlagOffset = 0xA8;
    private const int CdbFlagValue = 1;
    private const uint CompressedCapability = 2;
    private const long MaxLogicalBytes = 1024L * 1024 * 1024;

    /// <summary>判断数据是否带有 iTunesCDB 封装。</summary>
    public static bool IsITunesCdb(ReadOnlySpan<byte> data)
    {
        if (data.Length < GenericHeaderSize || !data[..4].SequenceEqual("mhbd"u8))
        {
            return false;
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(data[4..8]);
        if (headerSize < GenericHeaderSize || headerSize >= data.Length)
        {
            return false;
        }

        var flagged = headerSize >= CdbFlagOffset + 2 &&
                      BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(CdbFlagOffset, 2)) == CdbFlagValue;
        var capability = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x0C, 4));
        var streamStart = data[headerSize];

        // zlib 流的首字节以 0x78 开头（默认压缩级别）。
        return (flagged || capability == CompressedCapability) && streamStart == 0x78;
    }

    /// <summary>解开一层 iTunesCDB 封装。</summary>
    public static ITunesCdb Decompress(byte[] data)
    {
        if (!IsITunesCdb(data))
        {
            throw new IPodDbException("目标文件不是带封装的 iTunesCDB。");
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4));
        var declaredSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8, 4));
        if (headerSize >= declaredSize || declaredSize > data.Length)
        {
            throw new IPodDbException("iTunesCDB 的根长度字段被截断或小于其数据区。");
        }

        byte[] payload;
        try
        {
            using var input = new MemoryStream(data, headerSize, declaredSize - headerSize);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output, 1 << 16);
            payload = output.ToArray();
        }
        catch (InvalidDataException error)
        {
            throw new IPodDbException("iTunesCDB 的 zlib 数据区已损坏。", error);
        }

        if (payload.Length > MaxLogicalBytes)
        {
            throw new IPodDbException("iTunesCDB 解压后超过允许的逻辑大小上限。");
        }

        var logical = new byte[headerSize + payload.Length];
        Array.Copy(data, logical, headerSize);
        Array.Copy(payload, 0, logical, headerSize, payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(logical.AsSpan(8, 4), logical.Length);

        if (headerSize >= CdbFlagOffset + 2)
        {
            logical[CdbFlagOffset] = 0;
            logical[CdbFlagOffset + 1] = 0;
        }

        return new ITunesCdb(logical, data, [], data[declaredSize..]);
    }

    /// <summary>把逻辑 iTunesDB 字节重新封装为 iTunesCDB。</summary>
    public static byte[] Compress(byte[] logical, ITunesCdb? framing = null)
    {
        if (framing is not null && framing.LogicalBytes.Length > 0 &&
            logical.AsSpan().SequenceEqual(framing.LogicalBytes))
        {
            // 逻辑内容未变化时，逐字节还原原始物理字节。
            return framing.SourceBytes;
        }

        if (logical.Length < GenericHeaderSize || !logical.AsSpan(0, 4).SequenceEqual("mhbd"u8))
        {
            throw new IPodDbException("iTunesCDB 压缩要求数据以 iTunesDB 根块开头。");
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(logical.AsSpan(4, 4));
        if (headerSize < GenericHeaderSize || headerSize > logical.Length)
        {
            throw new IPodDbException("iTunesDB 根头部被截断。");
        }

        if (headerSize < CdbFlagOffset + 2)
        {
            throw new IPodDbException("该根头部无法表示 iTunesCDB 压缩标志。");
        }

        using var output = new MemoryStream();
        output.Write(logical, 0, headerSize);
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(logical, headerSize, logical.Length - headerSize);
        }

        if (framing is not null)
        {
            output.Write(framing.StreamTrailingData);
        }

        var result = output.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8, 4), result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x0C, 4), CompressedCapability);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(CdbFlagOffset, 2), CdbFlagValue);

        if (framing is not null)
        {
            result = [.. result, .. framing.FileTrailingData];
        }

        return result;
    }
}
