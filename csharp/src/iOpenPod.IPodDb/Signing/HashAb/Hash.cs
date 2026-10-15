// SPDX-License-Identifier: Unlicense
// 由 dstaley/hashab（The Unlicense）的 src/hash.c 与 src/hash.h 移植。

namespace IOpenPod.IPodDb.Signing.HashAb;

/// <summary>HashAB 内部哈希：先做自定义字节替换预处理，再用 EDON-R' 256 压缩 4 轮。</summary>
internal static class Hash
{
    /// <summary>工作区长度（字节）。</summary>
    internal const int WorkSize = 64;

    /// <summary>摘要长度（字节）。</summary>
    internal const int DigestSize = 32;

    /// <summary>
    /// 对 64 字节工作区求 32 字节摘要。
    /// </summary>
    /// <param name="work">64 字节工作区；与 C 版本一样会被就地改写（写回最后一轮的块）。</param>
    /// <param name="output">32 字节输出摘要。</param>
    internal static void Compute(Span<byte> work, Span<byte> output)
    {
        Span<uint> block = stackalloc uint[16];
        Span<uint> state = stackalloc uint[16];

        for (int i = 0; i < 16; i++)
        {
            block[i] = PreprocessWord(ByteOrder.Load32Le(work.Slice(i * 4)));
        }

        EdonR.Init(state);

        for (int round = 0; round < 4; round++)
        {
            EdonR.Compress(state, block);
            if (round != 3)
            {
                for (int i = 0; i < 16; i++)
                {
                    block[i] = StateToWord(state[i]);
                }
            }
        }

        for (int i = 0; i < 16; i++)
        {
            ByteOrder.Store32Le(work.Slice(i * 4), block[i]);
        }

        Finalize(state, output);
    }

    /// <summary>把 EDON-R 状态的高 8 个字乘以常数后按小端序输出（对应 C 的 hash_final）。</summary>
    /// <param name="state">16 字 EDON-R 状态。</param>
    /// <param name="output">32 字节输出。</param>
    private static void Finalize(ReadOnlySpan<uint> state, Span<byte> output)
    {
        for (int i = 0; i < 8; i++)
        {
            ByteOrder.Store32Le(output.Slice(i * 4), unchecked(state[8 + i] * 0xf46ef4ffu));
        }
    }

    /// <summary>取一个 32 位字的最低位字节再乘 0xbf，得到 0..255 的表下标（对应 C 的 hash_byte_index）。</summary>
    /// <param name="value">输入字。</param>
    private static byte ByteIndex(uint value)
    {
        return unchecked((byte)(value * 0xbfu));
    }

    /// <summary>32 位循环左移（对应 C 的 encoded_rol：先乘 0x8fa491df 解码，再循环左移）。</summary>
    /// <param name="value">输入字。</param>
    /// <param name="count">位移量。</param>
    private static uint EncodedRol(uint value, int count)
    {
        unchecked
        {
            uint decoded = value * 0x8fa491dfu;
            int shift = count & 31;

            return (decoded << shift) | (decoded >> ((32 - shift) & 31));
        }
    }

    /// <summary>字节替换预处理（对应 C 的 hash_preprocess_word）：4 个字节各查表后按混淆常数混合成一个字。</summary>
    /// <param name="input">输入字（小端序读入）。</param>
    private static uint PreprocessWord(uint input)
    {
        unchecked
        {
            const uint InputMultiplier = 0xa272be3fu;
            const uint CombineMultiplier = 0x7f8631bfu;
            const uint ByteMultiplier = 0xc83bebc7u;
            const uint WordBias = 0x44e57c7eu;

            uint byte0 = HashByteTables.Table0[input & 0xff];
            uint byte1 = HashByteTables.Table1[(input >> 8) & 0xff];
            uint byte2 = HashByteTables.Table2[(input >> 16) & 0xff];
            uint byte3 = HashByteTables.Table3[input >> 24];

            uint scaled = (((byte0 * 0xc9u * 0x79u) & 0xffu) << 8) * InputMultiplier;

            byte1 = byte1 * 0xc9u & 0xffu;
            uint highMask = ~(byte1 * 0x79u) | 0xffu;
            uint partial = byte1 * ByteMultiplier + WordBias + ~highMask * InputMultiplier + highMask * WordBias + scaled;
            scaled = partial * CombineMultiplier * 0x100u * InputMultiplier;

            highMask = ~(byte2 * 0x79u) | 0xffu;
            partial = byte2 * ByteMultiplier + WordBias + ~highMask * InputMultiplier + highMask * WordBias + partial + scaled;

            highMask = ~(byte3 * 0x79u) | 0xffu;
            partial = highMask * WordBias + byte3 * ByteMultiplier + WordBias + ~highMask * InputMultiplier + partial * CombineMultiplier * 0x100u * InputMultiplier + partial;

            partial = partial * CombineMultiplier * 0x100u * InputMultiplier + partial;
            return partial * CombineMultiplier;
        }
    }

    /// <summary>把 EDON-R 状态字编码成下一轮的输入块字（对应 C 的 hash_state_to_word）。</summary>
    /// <param name="canonicalStateWord">EDON-R 状态中的一个字。</param>
    private static uint StateToWord(uint canonicalStateWord)
    {
        unchecked
        {
            const uint StateEncodeMultiplier = 0xc2ed397fu;
            const uint InputMultiplier = 0xa272be3fu;
            const uint IndexDecode = 0x7f8631bfu;
            const uint IndexMultiplier = 0x5d8d41c1u;
            const uint StateMultiplier = 0xbcb95b41u;
            const uint ByteMultiplier = 0xc83bebc7u;
            const uint WordBias = 0x44e57c7eu;
            const uint RotateInput = 0x1e051c21u;
            const uint EncodedOrMultiplier = 0x00f39c82u;

            uint stateWord = canonicalStateWord * StateEncodeMultiplier;

            uint mapped = HashByteTables.Table0[ByteIndex(stateWord * StateMultiplier)];
            mapped = mapped * 0xc9u & 0xffu;
            uint highMask = ~(mapped * 0x79u) | 0xffu;
            uint combined = stateWord * StateMultiplier + highMask * WordBias + mapped * ByteMultiplier + WordBias + ~highMask * InputMultiplier;
            uint rotated = EncodedRol(combined * RotateInput, 11);
            rotated *= InputMultiplier;

            mapped = HashByteTables.Table1[ByteIndex(rotated)];
            mapped = mapped * 0xc9u & 0xffu;
            highMask = ~(mapped * 0x79u) | 0xffu;
            combined = ~highMask * InputMultiplier + mapped * ByteMultiplier + WordBias + highMask * WordBias;
            rotated = EncodedRol(
                (((combined * EncodedOrMultiplier - 1u) | (rotated * EncodedOrMultiplier - 1u)) * InputMultiplier + rotated + combined - IndexMultiplier) * RotateInput,
                10);

            mapped = HashByteTables.Table2[ByteIndex(rotated * InputMultiplier)];
            highMask = ~(mapped * 0x79u) | 0xffu;
            combined = mapped * ByteMultiplier + WordBias + ~highMask * InputMultiplier + highMask * WordBias;
            rotated = EncodedRol((rotated * InputMultiplier - combined) * RotateInput, 7);

            return rotated * InputMultiplier * IndexDecode;
        }
    }
}
