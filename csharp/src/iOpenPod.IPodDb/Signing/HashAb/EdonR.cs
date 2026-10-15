// SPDX-License-Identifier: Unlicense
// 由 dstaley/hashab（The Unlicense）的 src/edonr.c 与 src/edonr.h 移植。

namespace IOpenPod.IPodDb.Signing.HashAb;

/// <summary>EDON-R' 256 压缩函数（HashAB 内部哈希的底层置换）。</summary>
internal static class EdonR
{
    /// <summary>初始状态字个数。</summary>
    private const int StateWords = 16;

    /// <summary>EDON-R256 的固定初始状态。</summary>
    private static readonly uint[] InitialHash =
    {
        0x40414243, 0x44454647, 0x48494a4b, 0x4c4d4e4f,
        0x50515253, 0x54555657, 0x58595a5b, 0x5c5d5e5f,
        0x60616263, 0x64656667, 0x68696a6b, 0x6c6d6e6f,
        0x70717273, 0x74757677, 0x78797a7b, 0x7c7d7e7f,
    };

    /// <summary>把 16 个字的初始状态写入 <paramref name="hash"/>。</summary>
    /// <param name="hash">长度至少 16 的状态缓冲区。</param>
    internal static void Init(Span<uint> hash)
    {
        InitialHash.CopyTo(hash);
    }

    /// <summary>用 16 字的输入块压缩状态（对应 C 的 EdonR256Compress）。</summary>
    /// <param name="hash">16 字状态，原地更新。</param>
    /// <param name="block">16 字输入块。</param>
    internal static void Compress(Span<uint> hash, ReadOnlySpan<uint> block)
    {
        Span<uint> p = stackalloc uint[StateWords];
        Span<uint> reversed = stackalloc uint[8];

        // Q256(block[15..8], block[0..7]) -> p16..p23
        Reverse8(block, 15, reversed);
        Q256(reversed, block.Slice(0, 8), p.Slice(0, 8));

        // Q256(p16..p23, block[8..15]) -> p24..p31
        Q256(p.Slice(0, 8), block.Slice(8, 8), p.Slice(8, 8));

        // Q256(hash[8..15], p16..p23) -> p16..p23
        Q256(hash.Slice(8, 8), p.Slice(0, 8), p.Slice(0, 8));

        // Q256(p16..p23, p24..p31) -> p24..p31
        Q256(p.Slice(0, 8), p.Slice(8, 8), p.Slice(8, 8));

        // Q256(p16..p23, hash[0..7]) -> p16..p23
        Q256(p.Slice(0, 8), hash.Slice(0, 8), p.Slice(0, 8));

        // Q256(p24..p31, p16..p23) -> p24..p31
        Q256(p.Slice(8, 8), p.Slice(0, 8), p.Slice(8, 8));

        // Q256(block[7..0], p16..p23) -> p16..p23
        Reverse8(block, 7, reversed);
        Q256(reversed, p.Slice(0, 8), p.Slice(0, 8));

        // Q256(p16..p23, p24..p31) -> p24..p31
        Q256(p.Slice(0, 8), p.Slice(8, 8), p.Slice(8, 8));

        // 后馈：high 与 low 两个半区分别与输入块交叉异或
        for (int i = 0; i < 8; i++)
        {
            hash[i] ^= block[8 + i] ^ p[i];
            hash[8 + i] ^= block[i] ^ p[8 + i];
        }
    }

    /// <summary>把 <paramref name="source"/> 中下标 0..7 的字倒序写入 8 字缓冲区，即 destination[i] = source[high - i]。</summary>
    /// <param name="source">源缓冲区。</param>
    /// <param name="high">倒序区间的最高下标（7 或 15）。</param>
    /// <param name="destination">8 字目标缓冲区。</param>
    private static void Reverse8(ReadOnlySpan<uint> source, int high, Span<uint> destination)
    {
        for (int i = 0; i < 8; i++)
        {
            destination[i] = source[high - i];
        }
    }

    /// <summary>32 位循环左移（位移量为常量 4..29）。</summary>
    /// <param name="value">待移位值。</param>
    /// <param name="shift">位移量。</param>
    private static uint Rotl32(uint value, int shift)
    {
        unchecked
        {
            return (value << shift) | (value >> (32 - shift));
        }
    }

    /// <summary>
    /// EDON-R 的 quasigroup 操作 Q256（对应 C 中的 Q256 宏）：把两组各 8 个 32 位字混合成 8 个 32 位字。
    /// </summary>
    /// <param name="x">第一组 8 个字。</param>
    /// <param name="y">第二组 8 个字。</param>
    /// <param name="z">8 个字的结果；允许与 <paramref name="x"/> 或 <paramref name="y"/> 是同一块内存。</param>
    /// <remarks>实现与 C 宏一致：所有输入先读入局部变量，最后才写入 <paramref name="z"/>，因此允许原地别名。</remarks>
    private static void Q256(ReadOnlySpan<uint> x, ReadOnlySpan<uint> y, Span<uint> z)
    {
        unchecked
        {
            uint t0, t1, t2, t3, t4, t5, t6, t7;
            uint t8, t9, t10, t11, t12, t13, t14, t15;
            uint t16, t17, t18, t19, t20, t21, t22, t23;

            t8 = x[0] + x[4];
            t9 = x[1] + x[7];
            t12 = t8 + t9;
            t10 = x[2] + x[3];
            t11 = x[5] + x[6];
            t13 = t10 + t11;
            t0 = 0xaaaaaaaau + t12 + x[2];
            t1 = Rotl32(t12 + x[3], 4);
            t2 = Rotl32(t12 + x[6], 8);
            t3 = Rotl32(t13 + x[7], 13);
            t4 = Rotl32(x[1] + t13, 17);
            t5 = Rotl32(t8 + t10 + x[5], 22);
            t6 = Rotl32(x[0] + t9 + t11, 24);
            t7 = Rotl32(t13 + x[4], 29);
            t16 = t0 ^ t4;
            t17 = t1 ^ t7;
            t18 = t2 ^ t3;
            t19 = t5 ^ t6;
            t8 = t3 ^ t19;
            t9 = t2 ^ t19;
            t10 = t18 ^ t5;
            t11 = t16 ^ t1;
            t12 = t16 ^ t7;
            t13 = t17 ^ t6;
            t14 = t18 ^ t4;
            t15 = t0 ^ t17;
            t16 = y[0] + y[1];
            t17 = y[2] + y[5];
            t20 = t16 + t17;
            t18 = y[3] + y[4];
            t22 = t16 + t18;
            t19 = y[6] + y[7];
            t21 = t18 + t19;
            t23 = t17 + t19;
            t0 = 0x55555555u + t20 + y[7];
            t1 = Rotl32(t22 + y[6], 5);
            t2 = Rotl32(t20 + y[3], 9);
            t3 = Rotl32(y[2] + t21, 11);
            t4 = Rotl32(t22 + y[5], 15);
            t5 = Rotl32(t23 + y[4], 20);
            t6 = Rotl32(y[1] + t23, 25);
            t7 = Rotl32(y[0] + t21, 27);
            t16 = t0 ^ t1;
            t17 = t2 ^ t5;
            t18 = t3 ^ t4;
            t19 = t6 ^ t7;
            z[5] = t8 + (t18 ^ t6);
            z[6] = t9 + (t17 ^ t7);
            z[7] = t10 + (t4 ^ t19);
            z[0] = t11 + (t16 ^ t5);
            z[1] = t12 + (t2 ^ t19);
            z[2] = t13 + (t16 ^ t3);
            z[3] = t14 + (t0 ^ t18);
            z[4] = t15 + (t1 ^ t17);
        }
    }
}
