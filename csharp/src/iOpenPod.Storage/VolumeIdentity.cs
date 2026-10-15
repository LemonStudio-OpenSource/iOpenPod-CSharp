namespace IOpenPod.Storage;

/// <summary>
/// 一次挂载观测得到的卷身份。卷身份在一次连接会话内保持稳定，
/// 断开重连后必须重新观测，绝不复用旧的会话。
/// </summary>
/// <param name="Serial">卷序列号（十六进制字符串），用于区分同名的不同设备。</param>
/// <param name="Label">卷标。</param>
/// <param name="FileSystem">文件系统名称，例如 FAT32、exFAT、HFS+。</param>
/// <param name="MountPoint">挂载点，Windows 上形如 <c>E:\</c>。</param>
/// <param name="DevicePath">物理设备路径，用于关联 USB 证据。</param>
/// <param name="TotalBytes">卷总容量。</param>
/// <param name="FreeBytes">卷剩余可用空间。</param>
public sealed record VolumeIdentity(
    string Serial,
    string Label,
    string FileSystem,
    string MountPoint,
    string DevicePath,
    long TotalBytes,
    long FreeBytes)
{
    /// <summary>供设置文件持久化使用的稳定键。</summary>
    public string StableKey =>
        $"{Serial}|{Label}|{TotalBytes}";

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Label) ? MountPoint : $"{Label} ({MountPoint})";
}

/// <summary>卷是否可写、是否为可移动介质等宿主观测结果。</summary>
public sealed record VolumeObservation(
    VolumeIdentity Identity,
    bool IsRemovable,
    bool IsWriteSafe,
    bool IsReady,
    string? Diagnostic)
{
    public string MountPoint => Identity.MountPoint;
}
