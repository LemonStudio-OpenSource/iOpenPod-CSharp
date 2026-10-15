namespace IOpenPod.Storage;

/// <summary>
/// 一个已挂载卷上发现的 iPod 候选项。
/// 只有包含 <c>iPod_Control</c> 标记目录的卷才会成为候选项。
/// </summary>
public sealed record IPodCandidate(
    VolumeObservation Volume,
    IReadOnlyList<SessionPath> MetadataPaths,
    string? SysInfoText,
    string? SysInfoExtendedText,
    byte[]? SysInfoBytes,
    byte[]? SysInfoExtendedBytes)
{
    public string MountPoint => Volume.MountPoint;
}

/// <summary>iPod 卷探测：只读、永不写入。</summary>
public sealed class IPodVolumeDetector
{
    public const string ControlDirectory = "iPod_Control";
    private const int MetadataLimit = 1 << 20;

    private readonly IVolumeDiscovery _discovery;

    public IPodVolumeDetector(IVolumeDiscovery? discovery = null)
    {
        _discovery = discovery ?? new WindowsVolumeDiscovery();
    }

    /// <summary>发现所有 iPod 候选卷，按盘符排序。</summary>
    public IReadOnlyList<IPodCandidate> Discover()
    {
        var candidates = new List<IPodCandidate>();

        foreach (var observation in _discovery.Discover())
        {
            if (!observation.IsReady)
            {
                continue;
            }

            var candidate = TryOpen(observation);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates
            .OrderBy(candidate => candidate.MountPoint, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>探测单个卷；不是 iPod 时返回 <c>null</c>。</summary>
    public IPodCandidate? TryOpen(VolumeObservation observation)
    {
        try
        {
            var controlHost = Path.Combine(observation.MountPoint, ControlDirectory);
            if (!Directory.Exists(controlHost))
            {
                return null;
            }

            var discovered = new List<SessionPath>();
            byte[]? sysInfoBytes = null;
            byte[]? sysInfoExtendedBytes = null;

            var deviceInfoHost = Path.Combine(controlHost, "DeviceInfo");
            foreach (var name in new[] { "SysInfo", "SysInfoExtended" })
            {
                var host = Path.Combine(deviceInfoHost, name);
                if (!File.Exists(host))
                {
                    continue;
                }

                var info = new FileInfo(host);
                if (info.Length > MetadataLimit)
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(host);
                discovered.Add(SessionPath.Create($"{ControlDirectory}/DeviceInfo/{name}"));
                if (name == "SysInfo")
                {
                    sysInfoBytes = bytes;
                }
                else
                {
                    sysInfoExtendedBytes = bytes;
                }
            }

            if (sysInfoBytes is null && sysInfoExtendedBytes is null)
            {
                // 没有身份元数据时仍然视为候选项，交由上层报告“未知设备”。
                var hasDatabase = File.Exists(Path.Combine(controlHost, "iTunes", "iTunesDB"));
                if (!hasDatabase)
                {
                    return null;
                }
            }

            return new IPodCandidate(
                observation,
                discovered,
                sysInfoBytes is null ? null : FilesystemSession.DecodeText(sysInfoBytes),
                sysInfoExtendedBytes is null ? null : FilesystemSession.DecodeText(sysInfoExtendedBytes),
                sysInfoBytes,
                sysInfoExtendedBytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>为候选项建立一次绑定身份的会话。</summary>
    public FilesystemSession OpenSession(IPodCandidate candidate, long connectionGeneration) =>
        new(candidate.Volume.Identity, connectionGeneration, candidate.Volume.IsWriteSafe);
}
