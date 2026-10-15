using IOpenPod.IPodDb.Chunks;

namespace IOpenPod.IPodDb;

/// <summary>
/// 无冲突的本地标识分配器。
/// 从现有标识的最大值之后开始递增，回绕时跳过已使用值。
/// </summary>
public sealed class IdentityAllocator
{
    private readonly HashSet<ulong> _used;
    private readonly ulong _limit;
    private readonly int _bits;
    private ulong _cursor;

    public IdentityAllocator(IEnumerable<ulong> existing, int bits)
    {
        _bits = bits;
        // 64 位与更宽的标识空间上限就是 ulong 的最大值（移位 64 位在 C# 中会回绕）。
        _limit = bits >= 64 ? ulong.MaxValue : 1UL << bits;
        _used = [.. existing];
        var max = _used.Count == 0 ? 0UL : _used.Max();
        _cursor = max + 1;
    }

    public ulong Take()
    {
        if (_cursor >= _limit)
        {
            _cursor = 1;
        }

        var start = _cursor;
        while (_used.Contains(_cursor))
        {
            _cursor++;
            if (_cursor >= _limit)
            {
                _cursor = 1;
            }

            if (_cursor == start)
            {
                throw new IPodDbWriteException($"没有可用的 {_bits} 位标识符了。");
            }
        }

        var result = _cursor;
        _used.Add(result);
        _cursor++;
        return result;
    }
}
