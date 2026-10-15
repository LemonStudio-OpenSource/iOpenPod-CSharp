using System.Text;

namespace IOpenPod.Storage;

/// <summary>
/// 已经过校验的设备相对路径。
///
/// 设备上的所有读写都必须经过本类型：调用方永远无法把任意宿主路径交给
/// 存储层，因此“只能操作 iPod 内部相对路径”这条安全边界由类型系统保证。
/// </summary>
public readonly struct SessionPath : IEquatable<SessionPath>
{
    private const int MaxSegmentLength = 255;

    private SessionPath(string relative)
    {
        Relative = relative;
    }

    /// <summary>使用正斜杠分隔的相对路径，例如 <c>iPod_Control/iTunes/iTunesDB</c>。</summary>
    public string Relative { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Relative);

    public string FileName
    {
        get
        {
            var index = Relative.LastIndexOf('/');
            return index < 0 ? Relative : Relative[(index + 1)..];
        }
    }

    public string? ParentDirectory
    {
        get
        {
            var index = Relative.LastIndexOf('/');
            return index <= 0 ? null : Relative[..index];
        }
    }

    public override string ToString() => Relative;

    /// <summary>
    /// 校验并构造一个设备相对路径。
    /// 拒绝绝对路径、盘符、UNC 前缀、<c>..</c> 上跳、控制字符与 Windows 保留名。
    /// </summary>
    public static SessionPath Create(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDevicePathException("设备相对路径不能为空。");
        }

        var normalized = relativePath.Replace('\\', '/').Trim();

        if (normalized.StartsWith('/') || normalized.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidDevicePathException($"设备相对路径不能是绝对路径：{relativePath}");
        }

        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            throw new InvalidDevicePathException($"设备相对路径不能包含盘符：{relativePath}");
        }

        foreach (var character in normalized)
        {
            if (char.IsControl(character))
            {
                throw new InvalidDevicePathException("设备相对路径不能包含控制字符。");
            }
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new InvalidDevicePathException($"设备相对路径没有有效片段：{relativePath}");
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (var segment in segments)
        {
            var trimmed = segment.TrimEnd(' ');
            if (trimmed.Length == 0)
            {
                throw new InvalidDevicePathException($"设备相对路径包含空白片段：{relativePath}");
            }

            if (trimmed is "." or "..")
            {
                throw new InvalidDevicePathException($"设备相对路径不能包含上跳片段：{relativePath}");
            }

            var dot = trimmed.IndexOf('.');
            var stem = dot < 0 ? trimmed : trimmed[..dot];
            if (IsReservedName(stem))
            {
                throw new InvalidDevicePathException($"设备相对路径使用了系统保留名：{trimmed}");
            }

            if (trimmed.Length > MaxSegmentLength)
            {
                throw new InvalidDevicePathException($"设备相对路径片段过长：{trimmed}");
            }

            if (builder.Length > 0)
            {
                builder.Append('/');
            }

            builder.Append(trimmed);
        }

        return new SessionPath(builder.ToString());
    }

    /// <summary>尝试构造设备相对路径；非法时返回 <c>false</c>。</summary>
    public static bool TryCreate(string relative, out SessionPath path)
    {
        try
        {
            path = Create(relative);
            return true;
        }
        catch (InvalidDevicePathException)
        {
            path = default;
            return false;
        }
    }

    private static bool IsReservedName(string stem) =>
        stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
        stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
        stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
        stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
        (stem.Length == 4 &&
         (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
          stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
         char.IsAsciiDigit(stem[3]));

    public bool Equals(SessionPath other) =>
        string.Equals(Relative, other.Relative, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SessionPath other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Relative);

    public static bool operator ==(SessionPath left, SessionPath right) => left.Equals(right);

    public static bool operator !=(SessionPath left, SessionPath right) => !left.Equals(right);

    /// <summary>把设备相对路径拼接为宿主绝对路径。</summary>
    internal string ToHostPath(string mountPoint)
    {
        var separator = mountPoint.EndsWith(Path.DirectorySeparatorChar) ? string.Empty : "/";
        return mountPoint + separator + Relative;
    }
}
