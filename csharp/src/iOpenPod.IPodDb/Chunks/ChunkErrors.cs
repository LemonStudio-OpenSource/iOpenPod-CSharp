namespace IOpenPod.IPodDb.Chunks;

/// <summary>数据库结构被截断。</summary>
public sealed class TruncatedChunkException : IPodDbException
{
    public TruncatedChunkException(string message) : base(message) { }
}

/// <summary>数据块声明长度非法。</summary>
public sealed class InvalidChunkLengthException : IPodDbException
{
    public InvalidChunkLengthException(string message) : base(message) { }
}

/// <summary>数据块结构不符合定义。</summary>
public sealed class InvalidChunkDataException : IPodDbException
{
    public InvalidChunkDataException(string message) : base(message) { }

    public InvalidChunkDataException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>写入失败。</summary>
public sealed class IPodDbWriteException : IPodDbException
{
    public IPodDbWriteException(string message) : base(message) { }
}
