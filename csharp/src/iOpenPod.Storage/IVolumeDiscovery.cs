namespace IOpenPod.Storage;

/// <summary>宿主的卷发现能力。发现过程绝不写入任何设备。</summary>
public interface IVolumeDiscovery
{
    /// <summary>枚举当前宿主上所有已就绪的卷。</summary>
    IReadOnlyList<VolumeObservation> Discover();
}

/// <summary>通用 USB 标识，用于把卷关联到物理设备。</summary>
public sealed record UsbDeviceIdentity(ushort VendorId, ushort ProductId, string? SerialNumber, string? Description)
{
    public string Display => $"VID_{VendorId:X4}&PID_{ProductId:X4}";
}
