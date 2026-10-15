// SPDX-License-Identifier: Unlicense
// 由 dstaley/hashab（The Unlicense）的 src/reduce.c 与 src/reduce.h 移植。

namespace IOpenPod.IPodDb.Signing.HashAb;

/// <summary>把 190 字节中间数据压缩成 16 字节（对应 C 的 reduce）。</summary>
internal static class Reduce
{
    /// <summary>输入长度（字节）。</summary>
    internal const int InputSize = 190;

    /// <summary>输出长度（字节）。</summary>
    internal const int OutputSize = 16;

    /// <summary>初始密钥种子。</summary>
    private static readonly uint[] KeySeed =
    {
        0x5099fdb1, 0x9bbf4349, 0x5cd7686c, 0x92aebb6d, 0x2df36af2, 0x552f4079, 0x219dfeae, 0x343eb35a,
        0x5dac0284, 0x91bae1f7, 0x64ec9d83, 0xa2ff04aa, 0x46df36ef, 0xd0d7ebdc, 0xf334c68d, 0x99a81ecf,
        0x8a85f4e4, 0xb96ef2ae, 0x2e132f59, 0x91d1d6ee, 0x73628eaa, 0x7bfcd2c8, 0xcbc8db84, 0x2c358ebb,
        0xcdce075f, 0x096dddf4, 0xf86bae65, 0xb408b83c, 0x3aa5ae3b, 0xef04fe54, 0xcbae433f, 0xbafb26c5,
        0x546e42be, 0x694466f8, 0xc0141741, 0x51fb98d5, 0x6a53c93d, 0x784b88bc, 0xe689e637, 0x720cffaf,
        0x3fb860f4, 0x1d78ec71, 0xa8a5ae0b, 0x6eeaa5b8, 0x4fd82294, 0xa61ea409, 0x3919851c, 0x577402c5,
        0xe8b09ca2, 0x11676e33, 0xfa7fa4ec, 0x707bcd8d, 0x52c20d40, 0x734b0bfb, 0x744241ff, 0x089fb6e0,
        0xe1cea850, 0xa91e1a9a, 0x9e9db0f3, 0x6fedd35b, 0xee066a04, 0x178a973a, 0x00aa4e6f, 0xf2994295,
    };

    /// <summary>初始状态种子。</summary>
    private static readonly uint[] StateSeed =
    {
        0xf825e4f8, 0x202997a3, 0x65055cfe, 0xd588806b, 0x9a396a74, 0x210d95d7, 0x760fa7a5, 0x5b85496a,
        0x65ad1cb7, 0xd93ce632, 0xb44bfe86, 0x97e5e021, 0x774af9ea, 0xfed3b7c5, 0x0d36b3e5, 0x7d7242ac,
        0x648df3be, 0xfda267a7, 0x0abf3e91, 0xea9d68c3, 0xb9de0b6e, 0xd4b020aa, 0x2af3296f, 0xb5bd052c,
        0xb0b3bf8e, 0x4b3d7bfe, 0xfd689550, 0xc3c8d629, 0xf1683297, 0x70e73403, 0x7b681b65, 0x4c566e07,
        0x9d39df6e, 0x7ced44ca, 0xd85121e1, 0x8f89dab8, 0x49d5dcd4, 0x630a3cdd, 0xa43cd850, 0xe05011fa,
        0xc79a3eb2, 0xe5912da3, 0x4683718c, 0xd0a154f1, 0x9c6aec15, 0x0e0d0468, 0x27de8990, 0x412adb3d,
        0xf8775990, 0xe5e21243, 0xc753f819, 0x0663c6ef, 0x93c9d773, 0xab28ffff, 0x5204a510, 0x11a9d2c5,
        0x6ec18106, 0x20ea572e, 0x6fe51b3a, 0x6e812c3d, 0x014b907b, 0x5c86830b, 0x9934a3d8, 0x3c57d8a8,
    };

    /// <summary>混淆用常数表。</summary>
    private static readonly uint[] RoundTable =
    {
        0x3dd3c06d, 0x02eda8fb, 0xb82d5a58, 0xe4d2adec, 0x5c66b0d9, 0x5a4f2459, 0x08da381e, 0x35ba3a4c,
        0xa86bb294, 0xfb7fc66a, 0xf53a25f2, 0x5dfe38f1, 0x6cb6b4af, 0xebeb4c63, 0x66209bd7, 0x29271b4e,
        0x7a91205b, 0x7993f4f5, 0x8fb73ed6, 0x974262cd, 0xf47cab3b, 0x757b65cc, 0x92e55b93, 0xfa401668,
        0xe8f42bcc, 0xa1796665, 0xc544210e, 0x4ff7d275, 0x1090b646, 0xad8b53cb, 0x7c871580, 0x67fb52d6,
        0x5ae5b9a1, 0x69becb2c, 0xd2b974af, 0xb709f90d, 0x95671e5b, 0x2479e36e, 0x305facbd, 0xd7524214,
        0xd7f29e79, 0x30cf42db, 0x86bad405, 0x370d4cb9, 0xaf348764, 0xc5ea190b, 0x06b2f0c7, 0x62ecaf33,
        0x59f1cee3, 0xf7f35186, 0xf89d98cf, 0x1c9e3527, 0x31e62b5c, 0x89851e84, 0x9d67e93a, 0xec33dc26,
        0x913bca92, 0x92f8982e, 0x5f160f82, 0x2691b03f, 0x433589fd, 0x18d97e1a, 0xba586b58, 0x8907cb75,
    };

    /// <summary>
    /// 把 190 字节输入压缩成 16 字节输出（对应 C 的 reduce）。
    /// </summary>
    /// <param name="input">190 字节输入。</param>
    /// <param name="output">16 字节输出。</param>
    internal static void Run(ReadOnlySpan<byte> input, Span<byte> output)
    {
        unchecked
        {
            Span<uint> key = stackalloc uint[64];
            Span<uint> state = stackalloc uint[64];
            Span<uint> work = stackalloc uint[128];

            KeySeed.CopyTo(key);
            StateSeed.CopyTo(state);

            // 把 190 字节输入循环展开成 128 个 32 位字（大端序拼装）
            for (int i = 0; i < 128; i++)
            {
                uint word = 0;
                for (int b = 0; b < 4; b++)
                {
                    word |= (uint)(byte)(input[(4 * i + b) % InputSize] * 0xbd) << (24 - 8 * b);
                }

                work[i] = word;
            }

            // 4 遍差分扩散
            for (int pass = 0; pass < 4; pass++)
            {
                for (int j = 0; j < 128; j++)
                {
                    work[j] = (work[j] ^ RotateLeft(work[(j - 13) & 127], 3))
                        + RotateLeft(work[(j + 71) & 127], 5)
                        - RotateLeft(work[(j + 101) & 127], 23);
                }
            }

            uint s7 = (work[22] / 5) * 0xc3456f96u;
            uint s8 = Maj(state[60], RoundTable[63], 0x908d4d25u) / 3 + 0xef9695ddu;
            key[42] += ((RoundTable[I64(work[33])] & 0x3cba9069u) + 0x43440114u) / 5;
            uint s9 = Choose(state[26], state[I64(work[87])], ~0x3cba9069u);
            uint rotation = RotateLeftOrOnes(0x6f72b2dau, (0u - state[I64(work[55])]) & 31);
            uint k = key[I64(s7)] / 5;
            uint selected = Choose(state[42], ~0x3047874du, ~0xb0dea603u);
            uint s10 = rotation ^ 0x908d4d25u;
            key[63] -= Maj(selected, k, s7);
            state[25] += key[I64(s7)];
            state[60] = Choose(key[21], s8, ~0x1fb7d8d3u) * work[122];
            work[5] += Choose(s7, ~0xd36d6fb6u, RoundTable[I64(work[120])]);

            key[35] += 0xb1a7e76fu;
            uint s18 = key[I64(s10)] ^ 0x908d4d25u;
            state[44] ^= 3u * Choose(0xc3456f96u, RoundTable[29], RoundTable[I64(work[18])]);
            work[107] += state[I64(work[44])] + 1u;
            uint invertedRotation = ~RotateRightOrOnes(0xb0dea603u, work[71] & 31);
            key[54] -= Maj(state[29], invertedRotation, ~0x6f72b2dau);
            work[62] ^= 0xdbd3d289u;
            key[19] += state[I64(s7)] / 5 - Maj(state[45], RoundTable[I64(work[45])], ~0x4cf50fa3u);
            key[49] = RoundTable[I64(state[I64(s18)] * state[I64(s18)])];
            key[21] ^= RoundTable[I64(work[27])];
            uint s19 = 0x908d4d24u - RotateLeftOrOnes(0x98b99bd8u, state[60] * state[41] & 31);
            state[10] = work[73];
            key[20] ^= RotateLeftOrOnes(~state[I64(work[98])], key[60] & 31);
            key[49] += Choose(state[56], key[41], RoundTable[I64(s19)]) >> 1;
            uint tableWord = RoundTable[I64(work[10])];
            state[24] ^= Choose(key[39], state[37], tableWord * tableWord * tableWord);
            uint s6 = RoundTable[I64(work[43])] & ~0x45059986u;
            s6 = (s6 | 0x41050082u) ^ 0x3cba9069u;
            uint rotated = RotateRightOrOnes(~s18, RoundTable[I64(work[31])] & 31);
            state[13] ^= (key[I64(work[50])] & ~0x147f3886u) | ~(rotated | 0xeb80c779u);

            key[28] -= Choose(key[35], Maj(key[58], RoundTable[I64(s9)], 0x908d4d25u), ~key[29]);
            state[23] += ~Maj(s8, state[I64(work[106])], 0x2725fe3bu);
            state[24] ^= work[118] * work[118];
            key[35] ^= Maj(state[54], 2u * work[108], state[41] * state[41] * state[41]);
            work[34] ^= Choose(s18, state[I64(work[39])] ^ state[37], RoundTable[29] + 0x02e5a7e3u);
            state[42] ^= Choose(state[40], state[I64(work[35])], work[78] * work[78]);
            work[49] += 3u * key[I64(work[39])] + 0x62116be6u;
            key[48] = s7;
            state[62] = key[I64(s19)] - state[I64(work[93])] - 0xef9695ddu;
            key[55] += key[60] / 3 - state[I64(work[112])] / 3;
            uint s21 = Maj(s6, RoundTable[60] + 0x4d485456u, 0x41d44d69u) + 0x3cba9069u;
            uint s25 = s9 ^ key[37];
            state[55] += 0x1d0f1b76u;
            work[77] -= RoundTable[(int)((state[60] & 127u) >> 1)];
            state[29] = key[I64(work[68])] / 15;
            state[46] ^= RoundTable[I64(RoundTable[I64(RoundTable[37])])];
            uint s22 = 0x4f2159fcu - Maj(state[59], RoundTable[29], RoundTable[60] / 5);
            uint s24 = Choose(0xbd06dd88u, Maj(0x59089be9u, RoundTable[I64(work[16])], state[60]), RoundTable[60]) + 0x4f2159fcu;
            work[10] = 0xde2849ddu;
            work[8] += Maj(Choose(state[52], 0x908d4d25u, 0x5987b420u), work[48] ^ s22, 0x51127d0fu);
            state[20] += RoundTable[I64(work[1])];
            work[32] += 1u + RotateRightOrOnes(0x0c7ad237u, s22 * s22 * s22 & 31);
            state[4] -= Choose(key[35], 0x4f2159fcu, 0xc0540978u) | ~RotateRightOrOnes(0x46ef99f4u, key[41] & 31);
            state[60] = ~RotateLeftOrOnes(~(work[62] / 3), (work[22] + key[I64(work[93])]) & 31);
            key[30] = 0x3cba9069u;
            key[10] = ~(RoundTable[I64(work[122])] + 0x972da190u);
            state[46] += Maj(state[8], RoundTable[I64(work[35])] | RoundTable[41], 0xf3d39067u);
            uint s26 = 0x3cba9069u - ((RoundTable[I64(work[67])] | RoundTable[I64(work[120])]) - RoundTable[I64(work[98])] - 1u);
            uint halvedState = state[I64(work[38])] >> 1;
            state[17] ^= halvedState * halvedState * halvedState;
            state[44] += RoundTable[62];
            key[26] -= RoundTable[I64(s6)];
            uint first = Choose(0x313ec15du, key[I64(s6)], state[60]);
            uint second = (RoundTable[I64(work[79])] & 0x7fbbd9fdu) | 0x0c201068u;
            key[35] -= Maj(key[34], first, second);
            key[16] ^= Maj(state[43], work[27] >> 1, state[41] | state[37]);
            work[33] = 0x1c3e9665u;
            key[58] -= work[118];
            state[21] += key[60];
            state[10] -= state[60];

            // 由前面若干中间值构造 16 字节初值
            Span<byte> initial = stackalloc byte[OutputSize];
            initial[0] = (byte)(0xba + s25);
            initial[1] = 0x97;
            initial[2] = 0x97;
            initial[3] = (byte)(0x9b + s24);
            initial[4] = (byte)(0xba + s8);
            initial[5] = 0x97;
            initial[6] = (byte)(0xd3 + s22);
            initial[7] = 0x97;
            initial[8] = (byte)(0xbe + s18 + s6);
            initial[9] = (byte)(0x27 + s7);
            initial[10] = 0x4c;
            initial[11] = (byte)(0x27 + s19);
            initial[12] = (byte)(0xbe + s10 + s21);
            initial[13] = (byte)(0xe3 + s26);
            initial[14] = 0x4c;
            initial[15] = (byte)(0x27 + work[92]);

            // 逐字节折叠 key/state/work 后乘 0xa5 输出
            for (int i = 0; i < OutputSize; i++)
            {
                byte reduced = initial[i];

                for (int j = i; j < 64; j += OutputSize)
                {
                    reduced = (byte)(reduced ^ (byte)key[j] ^ (byte)state[j]);
                }

                for (int j = i; j < 128; j += OutputSize)
                {
                    reduced = (byte)(reduced ^ FoldWord(work[j]));
                }

                output[i] = (byte)(0xa5u * reduced);
            }
        }
    }

    /// <summary>`(mask &amp; whenSet) | (~mask &amp; whenClear)`（对应 C 的 choose）。</summary>
    private static uint Choose(uint mask, uint whenSet, uint whenClear)
    {
        unchecked
        {
            return (mask & whenSet) | (~mask & whenClear);
        }
    }

    /// <summary>三选二多数函数（对应 C 的 maj）。</summary>
    private static uint Maj(uint a, uint b, uint c)
    {
        unchecked
        {
            return (a & b) | (a & c) | (b & c);
        }
    }

    /// <summary>32 位循环左移。</summary>
    private static uint RotateLeft(uint value, int count)
    {
        unchecked
        {
            return (value << count) | (value >> (32 - count));
        }
    }

    /// <summary>位移量为 0 时返回全 1 的循环左移（对应 C 的 rotate_left_or_ones）。</summary>
    private static uint RotateLeftOrOnes(uint value, uint count)
    {
        return count != 0 ? RotateLeft(value, (int)count) : uint.MaxValue;
    }

    /// <summary>位移量为 0 时返回全 1 的循环右移（对应 C 的 rotate_right_or_ones）。</summary>
    private static uint RotateRightOrOnes(uint value, uint count)
    {
        unchecked
        {
            return count != 0 ? (value >> (int)count) | (value << (32 - (int)count)) : uint.MaxValue;
        }
    }

    /// <summary>把一个 32 位字的 4 个字节相加/异或折叠成一个字节（对应 C 的 fold_word）。</summary>
    private static byte FoldWord(uint word)
    {
        unchecked
        {
            byte folded = (byte)word;
            folded = (byte)(folded + (byte)(word >> 8));
            folded = (byte)(folded ^ (byte)(word >> 16));
            folded = (byte)(folded + (byte)(word >> 24));
            return folded;
        }
    }

    /// <summary>把 32 位值取低 6 位作为 64 项表的整数下标。</summary>
    private static int I64(uint value)
    {
        return (int)(value & 63u);
    }
}
