// SPDX-License-Identifier: Unlicense
// 由 dstaley/hashab（The Unlicense）的 src/expand.c 与 src/expand.h 移植。

namespace IOpenPod.IPodDb.Signing.HashAb;

/// <summary>把 24 字节密文种子扩展成 190 字节中间数据（对应 C 的 expand）。</summary>
internal static class Expand
{
    /// <summary>种子长度（字节）。</summary>
    internal const int SeedSize = 24;

    /// <summary>输出长度（字节）。</summary>
    internal const int OutputSize = 190;

    /// <summary>输出所用的固定选择子。</summary>
    internal const uint Selector = 0xaeba51ec;

    /// <summary>选择子的校验乘数：selector * 0xb26ed57f 必须等于 20。</summary>
    private const uint SelectorMultiplier = 0xb26ed57fu;

    /// <summary>种子字节的乘数。</summary>
    private const byte SeedMultiplier = 0x6f;

    /// <summary>
    /// 扩展 190 字节中间数据。
    /// </summary>
    /// <param name="output">190 字节输出。</param>
    /// <param name="ciphertextAt20">stage1 输出的第 20..43 字节（24 字节种子）。</param>
    /// <param name="selector">选择子，必须是 <see cref="Selector"/>。</param>
    /// <param name="ciphertextAt0">stage1 输出的开头部分，只使用前 20 字节。</param>
    /// <returns>选择子校验失败时返回 false（对应 C 的 -1）。</returns>
    internal static bool Run(Span<byte> output, ReadOnlySpan<byte> ciphertextAt20, uint selector, ReadOnlySpan<byte> ciphertextAt0)
    {
        Span<byte> work = stackalloc byte[Hash.WorkSize];
        Span<byte> digest = stackalloc byte[Hash.DigestSize];
        Span<byte> chunk = stackalloc byte[Hash.DigestSize];

        work.Clear();

        if (unchecked(selector * SelectorMultiplier) != 20u)
        {
            return false;
        }

        for (int i = 0; i < SeedSize; i++)
        {
            work[i] = unchecked((byte)(ciphertextAt20[i] * SeedMultiplier));
        }

        for (int i = 0; i < 20; i++)
        {
            work[24 + i] = unchecked((byte)(ciphertextAt0[i] * SeedMultiplier));
        }

        ByteOrder.Store32Le(work.Slice(48), 12u);

        for (int iteration = 0; iteration < 6; iteration++)
        {
            int offset = iteration * Hash.DigestSize;
            int remaining = OutputSize - offset;
            int copyLength = remaining < Hash.DigestSize ? remaining : Hash.DigestSize;

            ByteOrder.Store32Le(work.Slice(44), unchecked((uint)iteration));

            // 注意：Hash.Compute 会就地改写 work，后续迭代基于改写后的工作区，必须保持这一顺序依赖。
            Hash.Compute(work, digest);
            TransformHashChunk(digest, chunk);
            chunk.Slice(0, copyLength).CopyTo(output.Slice(offset));
        }

        return true;
    }

    /// <summary>把 32 字节摘要逐字乘常数并重排字节（对应 C 的 transform_hash_chunk）。</summary>
    /// <param name="hash">32 字节摘要。</param>
    /// <param name="output">32 字节输出块。</param>
    private static void TransformHashChunk(ReadOnlySpan<byte> hash, Span<byte> output)
    {
        unchecked
        {
            for (int i = 0; i < 8; i++)
            {
                uint word = ByteOrder.Load32Le(hash.Slice(i * 4));
                uint product = word * 0xc8180affu;
                uint transformed = (uint)(byte)(word * 0x6bu)
                    | ((uint)(byte)((product >> 8) * 0x95u) << 8)
                    | ((uint)(byte)((product >> 16) * 0x95u) << 16)
                    | ((uint)(byte)((product >> 24) * 0x95u) << 24);

                ByteOrder.Store32Le(output.Slice(i * 4), transformed);
            }
        }
    }
}
