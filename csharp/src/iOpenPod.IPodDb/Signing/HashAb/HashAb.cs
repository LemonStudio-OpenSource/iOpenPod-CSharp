// SPDX-License-Identifier: Unlicense
// 由 dstaley/hashab（The Unlicense）的 src/calcHashAB.c 与 src/calcHashAB.h 移植。

namespace IOpenPod.IPodDb.Signing.HashAb;

/// <summary>Apple HASHAB 签名计算（由 dstaley/hashab 的公开领域 C 实现移植）。</summary>
/// <remarks>本类型无可变共享状态，可安全地被多个线程同时调用。</remarks>
public static class HashAbCalculator
{
    /// <summary>签名长度（字节）。</summary>
    private const int SignatureSize = 57;

    /// <summary>SHA-1 摘要长度（字节）。</summary>
    private const int Sha1Size = 20;

    /// <summary>GUID 长度（字节）。</summary>
    private const int GuidSize = 8;

    /// <summary>固定随机字节长度（字节）。</summary>
    private const int RandomSize = 23;

    /// <summary>输入字节的乘数。</summary>
    private const byte InputMultiplier = 0xed;

    /// <summary>stage2 输出字节的乘数。</summary>
    private const byte OutputMultiplier = 0x2d;

    /// <summary>stage1 输入尾部的填充字节。</summary>
    private const byte PaddingByte = 0xc1;

    /// <summary>expand 使用的固定选择子。</summary>
    private const uint ExpandSelector = 0xaeba51ecu;

    /// <summary>stage2 输出缓冲区中位于 32 字节有效数据之后的那一个字节的取值（见下方说明）。</summary>
    /// <remarks>
    /// C 版本中 permutation_56 的末元素为 0x37(55)，代入 stage2_output[source - 23] 会得到下标 32，
    /// 即越过 32 字节缓冲区读取一个字节（在 C 中属于未定义行为）。参考实现（libhashab32.so 与 wasm 构建）
    /// 在该处始终读到 0x13，官方 100 条测试向量也要求该值，因此这里把它显式建模为缓冲区末位的哨兵字节，
    /// 以避免移植版出现任何越界访问。
    /// </remarks>
    private const byte Stage2OutputOverreadByte = 0x13;

    /// <summary>stage2 输入的置换表。</summary>
    private static readonly byte[] Permutation32 =
    {
        0x06, 0x18, 0x11, 0x07, 0x13, 0x0d, 0x0e, 0x09, 0x15, 0x1d, 0x02, 0x1f, 0x01, 0x04, 0x1c, 0x1a,
        0x10, 0x14, 0x0b, 0x1e, 0x03, 0x0a, 0x1b, 0x19, 0x05, 0x0f, 0x16, 0x00, 0x12, 0x17, 0x08, 0x0c,
    };

    /// <summary>57 字节签名所用的置换表。</summary>
    private static readonly byte[] Permutation56 =
    {
        0x15, 0x1c, 0x06, 0x0c, 0x07, 0x1a, 0x05, 0x13, 0x08, 0x19, 0x03, 0x01, 0x2d, 0x1e, 0x10, 0x31,
        0x1d, 0x14, 0x28, 0x27, 0x35, 0x00, 0x2f, 0x1b, 0x26, 0x0b, 0x0e, 0x02, 0x23, 0x17, 0x24, 0x22,
        0x12, 0x1f, 0x20, 0x04, 0x29, 0x25, 0x21, 0x09, 0x18, 0x0d, 0x32, 0x0f, 0x11, 0x2e, 0x33, 0x2b,
        0x30, 0x2a, 0x36, 0x0a, 0x2c, 0x34, 0x16, 0x37,
    };

    /// <summary>固定的 23 字节随机常量，即 ASCII 字符串 "ABCDEFGHIJKLMNOPQRSTUVW"。</summary>
    private static readonly byte[] RandomBytes =
    {
        0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4b, 0x4c, 0x4d,
        0x4e, 0x4f, 0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57,
    };

    /// <summary>计算 57 字节 HASHAB 签名。</summary>
    /// <param name="sha1">20 字节 SHA-1 摘要。</param>
    /// <param name="guid">8 字节设备 FireWire GUID。</param>
    /// <returns>新的 57 字节签名数组。</returns>
    /// <exception cref="ArgumentException"><paramref name="sha1"/> 不是 20 字节，或 <paramref name="guid"/> 不是 8 字节。</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "参数名与项目既有的 HASHAB 调用约定保持一致。")]
    public static byte[] Compute(ReadOnlySpan<byte> sha1, ReadOnlySpan<byte> guid)
    {
        if (sha1.Length != Sha1Size)
        {
            throw new ArgumentException($"SHA-1 摘要必须正好是 {Sha1Size} 字节，实际为 {sha1.Length} 字节。", nameof(sha1));
        }

        if (guid.Length != GuidSize)
        {
            throw new ArgumentException($"设备 GUID 必须正好是 {GuidSize} 字节，实际为 {guid.Length} 字节。", nameof(guid));
        }

        byte[] target = new byte[SignatureSize];
        ComputeCore(target, sha1, guid);
        return target;
    }

    /// <summary>HASHAB 主流程（对应 C 的 calcHashAB）。</summary>
    /// <param name="target">57 字节输出。</param>
    /// <param name="sha1">20 字节 SHA-1 摘要。</param>
    /// <param name="guid">8 字节 GUID。</param>
    private static void ComputeCore(Span<byte> target, ReadOnlySpan<byte> sha1, ReadOnlySpan<byte> guid)
    {
        unchecked
        {
            Span<byte> stage1Input = stackalloc byte[80];
            Span<byte> stage1Output = stackalloc byte[80];
            Span<byte> expanded = stackalloc byte[Expand.OutputSize];
            Span<byte> compression = stackalloc byte[Reduce.OutputSize];
            Span<byte> stage2Input = stackalloc byte[32];

            // 多申请一个字节：末位用于承载参考实现在 stage2_output[32] 处越界读到的固定字节。
            Span<byte> stage2Output = stackalloc byte[33];

            // 组装 stage1 输入：GUID + SHA-1 + 随机常量，各自乘 0xed，尾部用 0xc1 填充
            for (int i = 0; i < GuidSize; i++)
            {
                stage1Input[i] = (byte)(guid[i] * InputMultiplier);
            }

            for (int i = 0; i < Sha1Size; i++)
            {
                stage1Input[8 + i] = (byte)(sha1[i] * InputMultiplier);
            }

            for (int i = 0; i < RandomSize; i++)
            {
                stage1Input[28 + i] = (byte)(RandomBytes[i] * InputMultiplier);
            }

            stage1Input.Slice(51).Fill(PaddingByte);

            // stage1 CBC 加密（80 字节）
            Cipher.Stage1Cipher(stage1Output, stage1Input);

            // 扩展成 190 字节，再压缩成 16 字节 IV
            if (!Expand.Run(expanded, stage1Output.Slice(20, Expand.SeedSize), ExpandSelector, stage1Output.Slice(0, Sha1Size)))
            {
                target.Clear();
                return;
            }

            Reduce.Run(expanded, compression);

            // 按置换表从 stage1 输出的 44..75 字节取出 stage2 输入
            for (int i = 0; i < stage2Input.Length; i++)
            {
                stage2Input[i] = stage1Output[44 + Permutation32[i]];
            }

            stage2Output[32] = Stage2OutputOverreadByte;

            // stage2 CBC 加密（32 字节）
            Cipher.Stage2Cipher(stage2Output.Slice(0, 32), stage2Input, compression);

            // 组装签名：固定 0x03 0x00 前缀 + 55 字节置换结果
            target[0] = 3;
            target[1] = 0;

            for (int i = 0; i < 55; i++)
            {
                int source = Permutation56[i];
                target[i + 2] = source < RandomSize
                    ? RandomBytes[source]
                    : (byte)(stage2Output[source - RandomSize] * OutputMultiplier);
            }
        }
    }
}
