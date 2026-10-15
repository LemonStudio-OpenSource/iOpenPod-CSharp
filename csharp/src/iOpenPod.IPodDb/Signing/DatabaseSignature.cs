using System.Security.Cryptography;
using System.Text;
using IOpenPod.IPodDb.Signing.HashAb;

namespace IOpenPod.IPodDb.Signing;

/// <summary>设备使用的数据库签名方案。</summary>
public enum SignatureScheme
{
    None = 0,
    Hash58 = 1,
    Hash72 = 2,
    HashAb = 3,
}

/// <summary>从设备 <c>HashInfo</c> 记录中解析出的 HASH72 材料。</summary>
public sealed record Hash72Material(byte[] Uuid, byte[] RandomPart, byte[] Iv);

/// <summary>签名过程中出现的可预期错误。</summary>
public sealed class SignatureException : IPodDbException
{
    public SignatureException(string message) : base(message) { }
}

/// <summary>
/// iTunesDB / iTunesCDB 的签名计算与校验。
///
/// 实现依据：libgpod 的 itdb_hash58.c、原 iOpenPod 的 HASH58 向量，以及
/// dstaley/hashab 的公开领域 HASHAB 实现。
/// </summary>
public static class DatabaseSignature
{
    private const int FieldDbId = 0x18;
    private const int FieldUnk0X32 = 0x32;
    private const int FieldHashingScheme = 0x30;
    private const int FieldHash58 = 0x58;
    private const int FieldHash72 = 0x72;
    private const int FieldHashAb = 0xAB;

    private static readonly byte[] Hash58Seed =
        Convert.FromHexString("6723FE304533F890992107C1D012B2A10781");

    private static readonly byte[] Hash72Key =
        Convert.FromHexString("618CA10DC7F57FD3B4723E08157463D7");

    private static readonly byte[] SBox = BuildSBox();
    private static readonly byte[] InverseSBox = BuildInverseSBox();

    // ---- HASH58 ----

    /// <summary>计算 HASH58 的 HMAC 密钥。</summary>
    public static byte[] Hash58Key(ReadOnlySpan<byte> guid)
    {
        if (guid.Length != 8)
        {
            throw new SignatureException("HASH58 需要恰好 8 字节的 FireWire GUID。");
        }

        var derived = new byte[16];
        var index = 0;
        for (var i = 0; i < 8; i += 2)
        {
            int left = guid[i];
            int right = guid[i + 1];
            var value = left != 0 && right != 0 ? Lcm(left, right) : 1;
            derived[index++] = SBox[(value >> 8) & 0xFF];
            derived[index++] = InverseSBox[(value >> 8) & 0xFF];
            derived[index++] = SBox[value & 0xFF];
            derived[index++] = InverseSBox[value & 0xFF];
        }

        var buffer = new byte[Hash58Seed.Length + derived.Length];
        Array.Copy(Hash58Seed, buffer, Hash58Seed.Length);
        Array.Copy(derived, 0, buffer, Hash58Seed.Length, derived.Length);
        return SHA1.HashData(buffer);
    }

    /// <summary>为整份数据库写入 HASH58 签名。</summary>
    public static byte[] SignHash58(byte[] data, ReadOnlySpan<byte> guid)
    {
        var headerSize = RootHeaderSize(data);
        var key = Hash58Key(guid);
        var output = (byte[])data.Clone();
        var normalized = (byte[])data.Clone();

        ZeroField(normalized, headerSize, FieldDbId, 8, "数据库标识");
        ZeroField(normalized, headerSize, FieldUnk0X32, 20, "保留区");
        ZeroField(normalized, headerSize, FieldHash58, 20, "HASH58");
        ZeroField(normalized, headerSize, FieldHashingScheme, 2, "签名方案");

        WriteU16(normalized, FieldHashingScheme, 1);
        WriteU16(output, FieldHashingScheme, 1);

        var signature = HMACSHA1.HashData(key, normalized);
        Array.Copy(signature, 0, output, FieldHash58, 20);
        return output;
    }

    /// <summary>校验 HASH58 签名。</summary>
    public static bool VerifyHash58(byte[] data, ReadOnlySpan<byte> guid) =>
        CryptographicOperations.FixedTimeEquals(data, SignHash58(data, guid));

    /// <summary>为任意载荷（例如 Locations 校验簿）计算 HASH58 HMAC。</summary>
    public static byte[] ComputeHash58(ReadOnlySpan<byte> guid, ReadOnlySpan<byte> payload) =>
        HMACSHA1.HashData(Hash58Key(guid), payload);

    // ---- HASH72 ----

    /// <summary>解析设备上 54 字节的 <c>HashInfo</c>（HASHv0）记录。</summary>
    public static Hash72Material ParseHashInfo(ReadOnlySpan<byte> data)
    {
        if (data.Length != 54 || !data[..6].SequenceEqual("HASHv0"u8))
        {
            throw new SignatureException("HashInfo 必须是单条 54 字节的 HASHv0 记录。");
        }

        return new Hash72Material(
            data.Slice(6, 20).ToArray(),
            data.Slice(26, 12).ToArray(),
            data.Slice(38, 16).ToArray());
    }

    /// <summary>用给定的 IV 与随机字节生成 46 字节 HASH72 信封。</summary>
    public static byte[] ComputeHash72Signature(
        ReadOnlySpan<byte> digest,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> randomPart)
    {
        if (digest.Length != 20)
        {
            throw new SignatureException("HASH72 需要 20 字节的 SHA-1 摘要。");
        }

        if (iv.Length != 16 || randomPart.Length != 12)
        {
            throw new SignatureException("HASH72 需要 16 字节 IV 与 12 字节随机数据。");
        }

        var plaintext = new byte[32];
        digest.CopyTo(plaintext);
        randomPart.CopyTo(plaintext.AsSpan(20));

        using var aes = Aes.Create();
        aes.Key = Hash72Key;
        aes.IV = iv.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        var encrypted = aes.EncryptCbc(plaintext, aes.IV, PaddingMode.None);

        var signature = new byte[46];
        signature[0] = 0x01;
        signature[1] = 0x00;
        randomPart.CopyTo(signature.AsSpan(2));
        encrypted.CopyTo(signature.AsSpan(14));
        return signature;
    }

    /// <summary>为整份数据库写入 HASH72 签名。</summary>
    public static byte[] SignHash72(
        byte[] data,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> randomPart,
        ushort hashingScheme = 2)
    {
        if (iv.Length != 16 || randomPart.Length != 12)
        {
            throw new SignatureException("HASH72 需要 16 字节 IV 与 12 字节随机数据。");
        }

        if (hashingScheme is not (1 or 2))
        {
            throw new SignatureException("HASH72 的签名方案只能是 1 或 2。");
        }

        var headerSize = RootHeaderSize(data);
        var output = (byte[])data.Clone();
        var normalized = (byte[])data.Clone();

        ZeroField(normalized, headerSize, FieldDbId, 8, "数据库标识");
        ZeroField(normalized, headerSize, FieldHash58, 20, "HASH58");
        ZeroField(normalized, headerSize, FieldHash72, 46, "HASH72");

        WriteU16(output, FieldHashingScheme, hashingScheme);
        WriteU16(normalized, FieldHashingScheme, hashingScheme);

        var digest = SHA1.HashData(normalized);
        var signature = ComputeHash72Signature(digest, iv, randomPart);
        Array.Copy(signature, 0, output, FieldHash72, 46);
        return output;
    }

    /// <summary>校验 HASH72 签名。</summary>
    public static bool VerifyHash72(
        byte[] data,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> randomPart,
        ushort hashingScheme = 2) =>
        CryptographicOperations.FixedTimeEquals(
            data, SignHash72(data, iv, randomPart, hashingScheme));

    /// <summary>判断数据库是否带有 HASH72 信封。</summary>
    public static bool HasHash72Signature(byte[] data) =>
        data.Length >= FieldHash72 + 46 && data[FieldHash72] == 0x01 && data[FieldHash72 + 1] == 0x00;

    /// <summary>从一份内部自洽的 HASH72 记录中恢复 IV 与随机字节。</summary>
    public static (byte[] Iv, byte[] RandomPart) ExtractHash72Material(byte[] data)
    {
        var headerSize = RootHeaderSize(data);
        var normalized = (byte[])data.Clone();
        ZeroField(normalized, headerSize, FieldDbId, 8, "数据库标识");
        ZeroField(normalized, headerSize, FieldHash58, 20, "HASH58");
        ZeroField(normalized, headerSize, FieldHash72, 46, "HASH72");

        var signature = data.AsSpan(FieldHash72, 46).ToArray();
        return RecoverHash72Signature(SHA1.HashData(normalized), signature);
    }

    /// <summary>校验并恢复单条 46 字节信封中的设备材料。</summary>
    public static (byte[] Iv, byte[] RandomPart) RecoverHash72Signature(
        ReadOnlySpan<byte> digest,
        ReadOnlySpan<byte> signature)
    {
        if (digest.Length != 20)
        {
            throw new SignatureException("HASH72 恢复需要 20 字节的 SHA-1 摘要。");
        }

        if (signature.Length != 46 || signature[0] != 0x01 || signature[1] != 0x00)
        {
            throw new SignatureException("目标记录中没有可恢复的 HASH72 签名。");
        }

        var randomPart = signature.Slice(2, 12).ToArray();

        using var aes = Aes.Create();
        aes.Key = Hash72Key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        var firstBlock = aes.DecryptEcb(signature.Slice(14, 16).ToArray(), PaddingMode.None);
        var iv = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            iv[i] = (byte)(firstBlock[i] ^ digest[i]);
        }

        var plaintext = aes.DecryptCbc(signature[14..].ToArray(), iv, PaddingMode.None);

        var expected = new byte[32];
        digest.CopyTo(expected);
        randomPart.CopyTo(expected.AsSpan(20));

        if (!CryptographicOperations.FixedTimeEquals(plaintext, expected))
        {
            throw new SignatureException("目标记录中的 HASH72 签名无效。");
        }

        return (iv, randomPart);
    }

    // ---- HASHAB ----

    /// <summary>用给定的 SHA-1 摘要与 GUID 计算 57 字节 HASHAB。</summary>
    public static byte[] ComputeHashAb(ReadOnlySpan<byte> digest, ReadOnlySpan<byte> guid)
    {
        if (digest.Length != 20)
        {
            throw new SignatureException("HASHAB 需要 20 字节的 SHA-1 摘要。");
        }

        if (guid.Length != 8)
        {
            throw new SignatureException("HASHAB 需要恰好 8 字节的 FireWire GUID。");
        }

        return HashAbCalculator.Compute(digest, guid);
    }

    /// <summary>为整份数据库写入 HASHAB 签名。</summary>
    public static byte[] SignHashAb(byte[] data, ReadOnlySpan<byte> guid)
    {
        var headerSize = RootHeaderSize(data);
        var output = (byte[])data.Clone();
        var normalized = (byte[])data.Clone();

        ZeroField(normalized, headerSize, FieldDbId, 8, "数据库标识");
        ZeroField(normalized, headerSize, FieldUnk0X32, 20, "保留区");
        ZeroField(normalized, headerSize, FieldHash58, 20, "HASH58");
        ZeroField(normalized, headerSize, FieldHash72, 46, "HASH72");
        ZeroField(normalized, headerSize, FieldHashAb, 57, "HASHAB");

        // HASHAB 的摘要使用 libgpod 的线路判别值 4，
        // 而 Nano 6/7 上由 iTunes 写出的头部保留 3。
        WriteU16(normalized, FieldHashingScheme, 4);

        var signature = ComputeHashAb(SHA1.HashData(normalized), guid);
        Array.Copy(signature, 0, output, FieldHashAb, 57);
        WriteU16(output, FieldHashingScheme, 3);
        return output;
    }

    /// <summary>校验 HASHAB 签名。</summary>
    public static bool VerifyHashAb(byte[] data, ReadOnlySpan<byte> guid) =>
        CryptographicOperations.FixedTimeEquals(data, SignHashAb(data, guid));

    // ---- 工具 ----

    /// <summary>读取数据库根头部声明的签名方案。</summary>
    public static ushort ReadScheme(byte[] data)
    {
        var headerSize = RootHeaderSize(data);
        if (headerSize < FieldHashingScheme + 2)
        {
            return 0;
        }

        return (ushort)(data[FieldHashingScheme] | (data[FieldHashingScheme + 1] << 8));
    }

    private static int RootHeaderSize(byte[] data)
    {
        if (data.Length < 12 || !data.AsSpan(0, 4).SequenceEqual("mhbd"u8))
        {
            throw new SignatureException("数据库签名要求数据以 iTunesDB 根块 mhbd 开头。");
        }

        var headerSize = BitConverter.ToInt32(data, 4);
        if (headerSize < 12 || headerSize > data.Length)
        {
            throw new SignatureException("iTunesDB 根头部被截断。");
        }

        return headerSize;
    }

    private static void ZeroField(byte[] data, int headerSize, int offset, int size, string name)
    {
        if (offset + size > headerSize)
        {
            throw new SignatureException($"保留的数据库头部无法容纳{name}字段。");
        }

        Array.Clear(data, offset, size);
    }

    private static void WriteU16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)(value >> 8);
    }

    private static int Lcm(int left, int right) => left / Gcd(left, right) * right;

    private static int Gcd(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }

    private static byte Multiply(byte left, byte right)
    {
        var value = 0;
        var a = (int)left;
        var b = (int)right;
        for (var i = 0; i < 8; i++)
        {
            if ((b & 1) != 0)
            {
                value ^= a;
            }

            a = ((a << 1) ^ ((a & 0x80) != 0 ? 0x11B : 0)) & 0xFF;
            b >>= 1;
        }

        return (byte)value;
    }

    private static byte Substitute(int value)
    {
        var inverse = 0;
        if (value != 0)
        {
            inverse = 1;
            for (var i = 0; i < 254; i++)
            {
                inverse = Multiply((byte)inverse, (byte)value);
            }
        }

        var result = inverse ^ 0x63;
        for (var shift = 1; shift <= 4; shift++)
        {
            result ^= ((inverse << shift) | (inverse >> (8 - shift))) & 0xFF;
        }

        return (byte)result;
    }

    private static byte[] BuildSBox()
    {
        var box = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            box[i] = Substitute(i);
        }

        return box;
    }

    private static byte[] BuildInverseSBox()
    {
        var inverse = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            inverse[SBox[i]] = (byte)i;
        }

        return inverse;
    }
}
