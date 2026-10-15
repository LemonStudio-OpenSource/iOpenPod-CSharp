namespace IOpenPod.Storage;

/// <summary>存储层抛出的所有可预期错误的基类。消息面向最终用户，使用中文。</summary>
public class StorageException : Exception
{
    public StorageException(string message) : base(message) { }

    public StorageException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>卷已断开或会话已被作废。</summary>
public sealed class VolumeDisconnectedException : StorageException
{
    public VolumeDisconnectedException(string message) : base(message) { }
}

/// <summary>请求的设备相对路径非法（绝对路径、上跳、非法字符等）。</summary>
public sealed class InvalidDevicePathException : StorageException
{
    public InvalidDevicePathException(string message) : base(message) { }
}

/// <summary>读取或写入超出允许的范围。</summary>
public sealed class StorageLimitException : StorageException
{
    public StorageLimitException(string message) : base(message) { }
}

/// <summary>磁盘空间不足。</summary>
public sealed class InsufficientSpaceException : StorageException
{
    public InsufficientSpaceException(string message) : base(message) { }
}
