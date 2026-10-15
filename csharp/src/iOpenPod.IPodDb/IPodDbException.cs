namespace IOpenPod.IPodDb;

/// <summary>iPod 数据库层抛出的所有可预期错误的基类。消息面向最终用户，使用中文。</summary>
public class IPodDbException : Exception
{
    public IPodDbException(string message) : base(message) { }

    public IPodDbException(string message, Exception inner) : base(message, inner) { }
}
