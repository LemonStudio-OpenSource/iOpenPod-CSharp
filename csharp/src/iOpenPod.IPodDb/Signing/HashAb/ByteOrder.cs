// SPDX-License-Identifier: Unlicense
// 由 dstaley/hashab（The Unlicense）的 src/byteorder.h 移植。

namespace IOpenPod.IPodDb.Signing.HashAb;

/// <summary>小端序 32 位读写辅助方法，对应 C 版本的 byteorder.h。</summary>
internal static class ByteOrder
{
    /// <summary>按小端序读取 4 个字节组成一个 32 位字。</summary>
    internal static uint Load32Le(ReadOnlySpan<byte> source)
    {
        return (uint)source[0] | ((uint)source[1] << 8) | ((uint)source[2] << 16) | ((uint)source[3] << 24);
    }

    /// <summary>把一个 32 位字按小端序写入 4 个字节。</summary>
    internal static void Store32Le(Span<byte> destination, uint value)
    {
        destination[0] = unchecked((byte)value);
        destination[1] = unchecked((byte)(value >> 8));
        destination[2] = unchecked((byte)(value >> 16));
        destination[3] = unchecked((byte)(value >> 24));
    }
}
