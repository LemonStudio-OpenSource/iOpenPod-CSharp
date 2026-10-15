// SPDX-License-Identifier: Unlicense
// 由 dstaley/hashab（The Unlicense）的 src/cipher.c 与 src/cipher.h 移植。

namespace IOpenPod.IPodDb.Signing.HashAb;

/// <summary>一套 HASHAB 分组密码使用的替换表、仿射参数与轮密钥（对应 C 的 cipher_profile）。</summary>
internal sealed class CipherProfile
{
    /// <summary>构造一套密码参数表。</summary>
    internal CipherProfile(byte[] inputMap, byte[] finalMap, byte[] outputMap, byte[] roundBase, byte[] roundAffine, byte[] schedule)
    {
        InputMap = inputMap;
        FinalMap = finalMap;
        OutputMap = outputMap;
        RoundBase = roundBase;
        RoundAffine = roundAffine;
        Schedule = schedule;
    }

    /// <summary>轮输入替换表（256 字节）。</summary>
    internal byte[] InputMap { get; }

    /// <summary>最后一轮的替换表（256 字节）。</summary>
    internal byte[] FinalMap { get; }

    /// <summary>输出替换表（256 字节）。</summary>
    internal byte[] OutputMap { get; }

    /// <summary>表查找基准字节（256 字节）。</summary>
    internal byte[] RoundBase { get; }

    /// <summary>仿射参数，扁平存放（15 行 × 9 字节）。</summary>
    internal byte[] RoundAffine { get; }

    /// <summary>轮密钥表（11 轮 × 16 字节 = 176 字节）。</summary>
    internal byte[] Schedule { get; }
}

/// <summary>HASHAB 的分组密码部分：stage1 与 stage2 的 CBC 加密。</summary>
internal static class Cipher
{
    /// <summary>分组长度（字节）。</summary>
    private const int BlockSize = 16;

    /// <summary>AES-128 轮数。</summary>
    private const int Aes128Rounds = 10;

    /// <summary>每套 profile 的仿射参数行数。</summary>
    private const int RoundAffineParameters = 9;

    /// <summary>stage1 的密码参数。</summary>
    private static readonly CipherProfile Stage1Profile = new(
        CipherTables.Stage1InputMap,
        CipherTables.Stage1FinalMap,
        CipherTables.Stage1OutputMap,
        CipherTables.Stage1RoundBase,
        CipherTables.Stage1RoundAffine,
        CipherTables.Stage1Schedule);

    /// <summary>stage2 的密码参数。</summary>
    private static readonly CipherProfile Stage2Profile = new(
        CipherTables.Stage2InputMap,
        CipherTables.Stage2FinalMap,
        CipherTables.Stage2OutputMap,
        CipherTables.Stage2RoundBase,
        CipherTables.Stage2RoundAffine,
        CipherTables.Stage2Schedule);

    /// <summary>stage1 加密：使用 stage1 profile 与固定初始链值做 CBC。</summary>
    /// <param name="output">输出缓冲区，长度必须是 16 的倍数。</param>
    /// <param name="input">输入缓冲区，长度与 <paramref name="output"/> 相同。</param>
    internal static void Stage1Cipher(Span<byte> output, ReadOnlySpan<byte> input)
    {
        Cbc(Stage1Profile, output, input, CipherTables.Stage1InitialChain);
    }

    /// <summary>stage2 加密：先对 IV 做输出表的逆映射得到初始链值，再做 CBC。</summary>
    /// <param name="output">输出缓冲区，长度必须是 16 的倍数。</param>
    /// <param name="input">输入缓冲区，长度与 <paramref name="output"/> 相同。</param>
    /// <param name="iv">16 字节初始向量（stage1 的压缩结果）。</param>
    internal static void Stage2Cipher(Span<byte> output, ReadOnlySpan<byte> input, ReadOnlySpan<byte> iv)
    {
        Span<byte> initialChain = stackalloc byte[BlockSize];
        for (int position = 0; position < BlockSize; position++)
        {
            initialChain[position] = InverseMapByte(Stage2Profile.OutputMap, iv[position]);
        }

        Cbc(Stage2Profile, output, input, initialChain);
    }

    /// <summary>对单个字节做仿射变换：以第 0 个参数为初值，按 <paramref name="value"/> 的 8 个位依次异或后续参数。</summary>
    /// <param name="affine">扁平的仿射参数表。</param>
    /// <param name="lane">参数行号。</param>
    /// <param name="value">待变换的字节。</param>
    private static byte AffineByte(byte[] affine, int lane, byte value)
    {
        int offset = lane * RoundAffineParameters;
        byte result = affine[offset];

        for (int bit = 0; bit < 8; bit++)
        {
            if ((value & (1 << bit)) != 0)
            {
                result ^= affine[offset + bit + 1];
            }
        }

        return result;
    }

    /// <summary>计算某个表（0..3）对给定索引贡献的 32 位字，4 个字节分别经过不同的仿射变换。</summary>
    /// <param name="profile">密码参数。</param>
    /// <param name="tableNumber">表编号。</param>
    /// <param name="index">表索引。</param>
    private static uint ProfileWord(CipherProfile profile, int tableNumber, byte index)
    {
        byte baseValue = profile.RoundBase[index];
        uint word = 0;

        for (int position = 0; position < 4; position++)
        {
            int lane = tableNumber * 4 + position;
            byte value = lane == 0 ? baseValue : AffineByte(profile.RoundAffine, lane - 1, baseValue);

            word |= (uint)value << (position * 8);
        }

        return word;
    }

    /// <summary>执行一轮 T 表查表轮变换。</summary>
    /// <param name="profile">密码参数。</param>
    /// <param name="input">当前状态（16 字节）。</param>
    /// <param name="roundKey">本轮的 16 字节轮密钥。</param>
    /// <param name="output">轮输出（16 字节）。</param>
    private static void TableRound(CipherProfile profile, ReadOnlySpan<byte> input, ReadOnlySpan<byte> roundKey, Span<byte> output)
    {
        for (int word = 0; word < 4; word++)
        {
            uint value = ByteOrder.Load32Le(roundKey.Slice(word * 4));

            for (int table = 0; table < 4; table++)
            {
                value ^= ProfileWord(profile, table, input[(word * 4 + table * 5) & 15]);
            }

            ByteOrder.Store32Le(output.Slice(word * 4), value);
        }
    }

    /// <summary>执行最后一轮：按固定置换取字节，做乘加后与轮密钥异或。</summary>
    /// <param name="profile">密码参数。</param>
    /// <param name="input">当前状态（16 字节）。</param>
    /// <param name="roundKey">最后一轮的 16 字节轮密钥。</param>
    /// <param name="output">轮输出（16 字节）。</param>
    private static void FinalRound(CipherProfile profile, ReadOnlySpan<byte> input, ReadOnlySpan<byte> roundKey, Span<byte> output)
    {
        for (int position = 0; position < BlockSize; position++)
        {
            int inputPosition = ((position & 12) + (position & 3) * 5) & 15;
            byte tableValue = profile.FinalMap[input[inputPosition]];

            output[position] = unchecked((byte)(unchecked((uint)tableValue * 0x81u + 0x7au) ^ roundKey[position]));
        }
    }

    /// <summary>在替换表中反查某个输出字节对应的下标；找不到时返回 0（与 C 版本一致）。</summary>
    /// <param name="map">替换表。</param>
    /// <param name="value">输出字节。</param>
    private static byte InverseMapByte(byte[] map, byte value)
    {
        for (int index = 0; index < map.Length; index++)
        {
            if (map[index] == value)
            {
                return unchecked((byte)index);
            }
        }

        return 0;
    }

    /// <summary>通用的 CBC 加密循环（1..10 轮表变换 + 最后一轮）。</summary>
    /// <param name="profile">密码参数。</param>
    /// <param name="output">输出缓冲区，长度与 <paramref name="input"/> 相同且是 16 的倍数。</param>
    /// <param name="input">输入缓冲区。</param>
    /// <param name="initialChain">16 字节初始链值。</param>
    private static void Cbc(CipherProfile profile, Span<byte> output, ReadOnlySpan<byte> input, ReadOnlySpan<byte> initialChain)
    {
        if (input.Length == 0 || (input.Length & (BlockSize - 1)) != 0)
        {
            throw new ArgumentException("CBC 输入长度必须是非零的 16 字节倍数。", nameof(input));
        }

        if (output.Length != input.Length)
        {
            throw new ArgumentException("CBC 输出缓冲区长度必须与输入相同。", nameof(output));
        }

        Span<byte> chaining = stackalloc byte[BlockSize];
        Span<byte> state = stackalloc byte[BlockSize];
        Span<byte> next = stackalloc byte[BlockSize];

        initialChain.CopyTo(chaining);

        for (int offset = 0; offset < input.Length; offset += BlockSize)
        {
            for (int position = 0; position < BlockSize; position++)
            {
                state[position] = unchecked((byte)(profile.InputMap[input[offset + position]] ^ chaining[position] ^ profile.Schedule[position]));
            }

            for (int round = 1; round < Aes128Rounds; round++)
            {
                TableRound(profile, state, profile.Schedule.AsSpan(round * BlockSize, BlockSize), next);
                next.CopyTo(state);
            }

            FinalRound(profile, state, profile.Schedule.AsSpan(Aes128Rounds * BlockSize, BlockSize), next);

            for (int position = 0; position < BlockSize; position++)
            {
                chaining[position] = next[position];
                output[offset + position] = profile.OutputMap[next[position]];
            }
        }
    }
}
