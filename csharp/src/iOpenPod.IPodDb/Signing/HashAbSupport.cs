using IOpenPod.IPodDb.Signing.HashAb;

namespace IOpenPod.IPodDb.Signing;

/// <summary>HASHAB 支持状态。移植实现缺失时，相关机型只允许读取。</summary>
public static class HashAbSupport
{
    private static readonly Lazy<bool> Available = new(Probe, isThreadSafe: true);

    /// <summary>当前构建是否具备可用的 HASHAB 计算能力。</summary>
    public static bool IsAvailable => Available.Value;

    private static bool Probe()
    {
        try
        {
            var guid = new byte[8];
            var digest = new byte[20];
            var result = HashAbCalculator.Compute(digest, guid);
            return result.Length == 57;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
