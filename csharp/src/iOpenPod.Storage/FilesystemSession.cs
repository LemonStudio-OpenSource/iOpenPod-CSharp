using System.Security.Cryptography;
using System.Text;

namespace IOpenPod.Storage;

/// <summary>
/// 与某个卷身份、挂载点和连接代次绑定的文件系统会话。
///
/// 会话一旦被作废（设备拔出、卷身份变化），所有操作都会失败，
/// 从而保证断开后的会话绝不会被复用。
/// </summary>
public sealed class FilesystemSession : IDisposable
{
    private const long DefaultReadLimit = 256L * 1024 * 1024;

    private readonly object _gate = new();
    private bool _invalidated;

    internal FilesystemSession(VolumeIdentity identity, long connectionGeneration, bool isWriteSafe)
    {
        Identity = identity;
        ConnectionGeneration = connectionGeneration;
        IsWriteSafe = isWriteSafe;
    }

    public VolumeIdentity Identity { get; }

    public string MountPoint => Identity.MountPoint;

    public long ConnectionGeneration { get; }

    public bool IsWriteSafe { get; }

    public bool IsUsable
    {
        get
        {
            lock (_gate)
            {
                return !_invalidated;
            }
        }
    }

    /// <summary>作废本会话。之后任何操作都会抛出 <see cref="VolumeDisconnectedException"/>。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _invalidated = true;
        }
    }

    internal void ThrowIfUnusable()
    {
        lock (_gate)
        {
            if (_invalidated)
            {
                throw new VolumeDisconnectedException(
                    $"设备 {Identity.DisplayName} 的连接会话已失效，请重新连接后再操作。");
            }
        }
    }

    public string ToHostPath(SessionPath path)
    {
        ThrowIfUnusable();
        return path.ToHostPath(MountPoint);
    }

    public bool FileExists(SessionPath path) => File.Exists(ToHostPath(path));

    public bool DirectoryExists(SessionPath path) => Directory.Exists(ToHostPath(path));

    public long GetFileLength(SessionPath path)
    {
        var info = new FileInfo(ToHostPath(path));
        return info.Exists ? info.Length : -1;
    }

    public byte[] ReadAllBytes(SessionPath path, long maxBytes = DefaultReadLimit)
    {
        var host = ToHostPath(path);
        var info = new FileInfo(host);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"设备上不存在文件：{path}", host);
        }

        if (info.Length > maxBytes)
        {
            throw new StorageLimitException(
                $"文件 {path} 大小为 {info.Length} 字节，超过允许读取的上限 {maxBytes} 字节。");
        }

        return File.ReadAllBytes(host);
    }

    public async Task<byte[]> ReadAllBytesAsync(
        SessionPath path,
        long maxBytes = DefaultReadLimit,
        CancellationToken cancellationToken = default)
    {
        var host = ToHostPath(path);
        var info = new FileInfo(host);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"设备上不存在文件：{path}", host);
        }

        if (info.Length > maxBytes)
        {
            throw new StorageLimitException(
                $"文件 {path} 大小为 {info.Length} 字节，超过允许读取的上限 {maxBytes} 字节。");
        }

        var buffer = new byte[info.Length];
        await using var stream = new FileStream(
            host,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 1 << 16,
            useAsync: true);
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    public string ReadAllText(SessionPath path, long maxBytes = DefaultReadLimit)
    {
        var bytes = ReadAllBytes(path, maxBytes);
        return DecodeText(bytes);
    }

    /// <summary>读取文本文件；自动识别 UTF-8 BOM、UTF-16 与回退编码。</summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    public Stream OpenRead(SessionPath path)
    {
        return new FileStream(
            ToHostPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 1 << 16,
            useAsync: false);
    }

    public async Task<Stream> OpenReadAsync(SessionPath path, CancellationToken cancellationToken = default)
    {
        var stream = new FileStream(
            ToHostPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 1 << 16,
            useAsync: true);
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return stream;
    }

    /// <summary>枚举目录下的文件（不递归）。目录不存在时返回空集合。</summary>
    public IReadOnlyList<SessionPath> EnumerateFiles(SessionPath directory, bool recursive = false)
    {
        var host = ToHostPath(directory);
        if (!Directory.Exists(host))
        {
            return Array.Empty<SessionPath>();
        }

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var results = new List<SessionPath>();
        foreach (var file in Directory.EnumerateFiles(host, "*", option))
        {
            var relative = Path.GetRelativePath(MountPoint, file).Replace('\\', '/');
            if (SessionPath.TryCreate(relative, out var sessionPath))
            {
                results.Add(sessionPath);
            }
        }

        return results;
    }

    public IReadOnlyList<string> EnumerateDirectories(SessionPath directory)
    {
        var host = ToHostPath(directory);
        if (!Directory.Exists(host))
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (var item in Directory.EnumerateDirectories(host))
        {
            names.Add(Path.GetFileName(item));
        }

        return names;
    }

    /// <summary>计算设备文件的 SHA-256 指纹。</summary>
    public string ComputeSha256(SessionPath path)
    {
        using var stream = OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    /// <summary>检查设备是否仍然可访问（卷根目录仍然存在）。</summary>
    public bool Probe()
    {
        try
        {
            return Directory.Exists(MountPoint);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public StorageTransaction BeginTransaction()
    {
        ThrowIfUnusable();
        if (!IsWriteSafe)
        {
            throw new StorageException($"卷 {Identity.DisplayName} 不可写，无法开始写入事务。");
        }

        return new StorageTransaction(this);
    }

    public void Dispose() => Invalidate();
}
