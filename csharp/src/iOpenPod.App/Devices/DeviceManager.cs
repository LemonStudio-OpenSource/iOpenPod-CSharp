using IOpenPod.App.Databases;
using IOpenPod.App.Logging;
using IOpenPod.DeviceRegistry;
using IOpenPod.IPodDb;
using IOpenPod.IPodDb.Artwork;
using IOpenPod.IPodDb.Signing;
using IOpenPod.Storage;

namespace IOpenPod.App.Devices;

/// <summary>一个已发现但尚未加载的 iPod 候选项。</summary>
public sealed record DeviceCandidateInfo(
    IPodCandidate Candidate,
    IdentificationResult Identification,
    string DisplayName,
    string Detail,
    bool IsIdentified)
{
    public string MountPoint => Candidate.MountPoint;

    public string StableKey => Candidate.Volume.Identity.StableKey;
}

/// <summary>
/// 设备协调器：负责发现、识别、加载与卸载当前的活动 iPod。
///
/// 只有被用户明确选择的设备才会被加载；发现过程永远只读。
/// </summary>
public sealed class DeviceManager : IDisposable
{
    private const long MaxDatabaseBytes = 256L * 1024 * 1024;
    private const long MaxArtworkBytes = 256L * 1024 * 1024;

    private readonly IPodVolumeDetector _detector;
    private readonly DeviceRegistry.DeviceRegistry _registry;
    private long _connectionGeneration;
    private bool _disposed;

    public DeviceManager(IPodVolumeDetector? detector = null, DeviceRegistry.DeviceRegistry? registry = null)
    {
        _detector = detector ?? new IPodVolumeDetector();
        _registry = registry ?? DeviceRegistry.DeviceRegistry.CreateDefault();
    }

    /// <summary>当前打开的设备；没有时为 <c>null</c>。</summary>
    public ActiveDevice? Current { get; private set; }

    /// <summary>活动设备发生变化时触发。</summary>
    public event EventHandler<ActiveDevice?>? CurrentChanged;

    /// <summary>发现全部 iPod 候选项。</summary>
    public IReadOnlyList<DeviceCandidateInfo> Discover()
    {
        var results = new List<DeviceCandidateInfo>();

        foreach (var candidate in _detector.Discover())
        {
            var evidence = CollectEvidence(candidate);
            var identification = _registry.Identify(evidence);
            var (displayName, detail) = Describe(candidate, identification);
            results.Add(new DeviceCandidateInfo(
                candidate,
                identification,
                displayName,
                detail,
                identification.Status == IdentificationStatus.Exact));
        }

        AppLog.Info("设备", $"发现 {results.Count} 个 iPod 候选设备。");
        return results;
    }

    private static DeviceEvidence CollectEvidence(IPodCandidate candidate)
    {
        var evidence = DeviceEvidence.Empty;

        if (candidate.SysInfoBytes is { Length: > 0 })
        {
            evidence = evidence.MergeWith(DeviceMetadata.ParseSysInfo(
                candidate.SysInfoBytes, "SysInfo", EvidenceAuthority.DeviceMetadata));
        }

        if (candidate.SysInfoExtendedBytes is { Length: > 0 })
        {
            evidence = evidence.MergeWith(DeviceMetadata.ParseSysInfoExtended(
                candidate.SysInfoExtendedBytes, "SysInfoExtended", EvidenceAuthority.DeviceMetadata));
        }

        if (!string.IsNullOrEmpty(candidate.Volume.Identity.DevicePath))
        {
            var usb = ParseUsbFromInstanceId(candidate.Volume.Identity.DevicePath);
            if (usb is not null)
            {
                evidence = evidence.MergeWith(new DeviceEvidence
                {
                    UsbIdentifiers =
                    [
                        new DeviceIdentifier<UsbIdentifier>(usb, "USB", EvidenceAuthority.CurrentHardware),
                    ],
                });
            }
        }

        var serial = candidate.Volume.Identity.Serial;
        if (!string.IsNullOrEmpty(serial))
        {
            evidence = evidence.MergeWith(new DeviceEvidence
            {
                TransportSerials =
                [
                    new DeviceIdentifier<string>(
                        Identifiers.NormalizeSerial(serial), "卷序列号", EvidenceAuthority.Derived),
                ],
            });
        }

        return evidence;
    }

    private static UsbIdentifier? ParseUsbFromInstanceId(string instanceId)
    {
        var marker = instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        ushort? vendor = null;
        ushort? product = null;
        var parts = instanceId[marker..].Split('&', '\\');
        foreach (var part in parts)
        {
            if (part.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) &&
                ushort.TryParse(part[4..], System.Globalization.NumberStyles.HexNumber, null, out var v))
            {
                vendor = v;
            }
            else if (part.StartsWith("PID_", StringComparison.OrdinalIgnoreCase) &&
                     ushort.TryParse(part[4..], System.Globalization.NumberStyles.HexNumber, null, out var p))
            {
                product = p;
            }
        }

        return vendor is not null && product is not null ? new UsbIdentifier(vendor.Value, product.Value) : null;
    }

    private static (string DisplayName, string Detail) Describe(
        IPodCandidate candidate,
        IdentificationResult identification)
    {
        var model = identification.Profile?.ChineseDisplayName
                    ?? (identification.CandidateList.Count > 0
                        ? string.Join(" / ", identification.CandidateList.Take(3).Select(p => p.ChineseDisplayName))
                        : "未知 iPod");

        var label = candidate.Volume.Identity.Label;
        var name = string.IsNullOrWhiteSpace(label) ? model : label;
        var detail = $"{model} · {identification.ChineseStatus} · {candidate.MountPoint}";
        return (name, detail);
    }

    /// <summary>打开并完整加载一个 iPod 候选项。</summary>
    public async Task<ActiveDevice> OpenAsync(
        DeviceCandidateInfo candidate,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Close();

        var generation = Interlocked.Increment(ref _connectionGeneration);
        var session = _detector.OpenSession(candidate.Candidate, generation);
        AppLog.Info("设备", $"正在打开设备：{candidate.DisplayName}（{candidate.MountPoint}）");

        try
        {
            var guid = ReadGuid(candidate, session);
            var name = await ReadDeviceNameAsync(session, cancellationToken).ConfigureAwait(false);

            // ---- iTunesDB / iTunesCDB ----
            var framing = ITunesCdb.Empty;
            byte[]? databaseBytes = null;
            byte[]? physicalBytes = null;
            var cdbPath = SessionPath.Create(DevicePaths.ITunesCdb);
            var dbPath = SessionPath.Create(DevicePaths.ITunesDb);

            if (session.FileExists(cdbPath))
            {
                var physical = await session.ReadAllBytesAsync(cdbPath, MaxDatabaseBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (ITunesCdbCodec.IsITunesCdb(physical))
                {
                    framing = ITunesCdbCodec.Decompress(physical);
                    databaseBytes = framing.LogicalBytes;
                    physicalBytes = physical;
                    AppLog.Info("数据库", $"已载入压缩音乐库 iTunesCDB（{physical.Length} 字节）。");
                }
            }

            if (databaseBytes is null)
            {
                if (!session.FileExists(dbPath))
                {
                    throw new IPodDbException("设备上既没有 iTunesDB 也没有 iTunesCDB，无法读取音乐库。");
                }

                physicalBytes = await session.ReadAllBytesAsync(dbPath, MaxDatabaseBytes, cancellationToken)
                    .ConfigureAwait(false);
                databaseBytes = physicalBytes;
                if (ITunesCdbCodec.IsITunesCdb(databaseBytes))
                {
                    framing = ITunesCdbCodec.Decompress(databaseBytes);
                    databaseBytes = framing.LogicalBytes;
                }

                AppLog.Info("数据库", $"已载入音乐库 iTunesDB（{databaseBytes.Length} 字节）。");
            }

            var database = ITunesDatabase.Parse(databaseBytes);
            AppLog.Info(
                "数据库",
                $"音乐库解析完成：{database.TrackCount} 首曲目，{database.UserPlaylists.Count} 个播放列表。");

            // ---- ArtworkDB ----
            ArtworkDatabase? artwork = null;
            var artworkPath = SessionPath.Create(DevicePaths.ArtworkDb);
            if (session.FileExists(artworkPath))
            {
                try
                {
                    var artworkBytes = await session.ReadAllBytesAsync(
                        artworkPath, MaxArtworkBytes, cancellationToken).ConfigureAwait(false);
                    artwork = ArtworkDatabase.Parse(artworkBytes);
                    AppLog.Info("插图", $"插图库解析完成：{artwork.Items.Count} 张插图。");
                }
                catch (Exception error)
                {
                    AppLog.Error("插图", "插图库无法读取，专辑封面将不可用", error);
                }
            }

            var (restriction, reason) = DetermineRestriction(candidate, session, guid, framing);

            var device = new ActiveDevice(
                session,
                candidate.Identification,
                candidate.Identification.Profile,
                name,
                ReadSerial(candidate),
                guid,
                database,
                framing,
                physicalBytes ?? databaseBytes,
                artwork,
                restriction,
                reason);

            Current = device;
            AppLog.Info("设备", $"设备已就绪：{device}（可写入：{(device.CanWrite ? "是" : "否")}）");
            if (!device.CanWrite)
            {
                AppLog.Warning("设备", $"写入被限制：{reason}");
            }

            CurrentChanged?.Invoke(this, device);
            return device;
        }
        catch (Exception)
        {
            session.Dispose();
            throw;
        }
    }

    private static (WriteRestriction Restriction, string Reason) DetermineRestriction(
        DeviceCandidateInfo candidate,
        FilesystemSession session,
        byte[] guid,
        ITunesCdb framing)
    {
        if (!session.IsWriteSafe)
        {
            return (WriteRestriction.VolumeReadOnly, "该卷被标记为只读，只能浏览与备份。");
        }

        var profile = candidate.Identification.Profile;
        if (profile is not null && profile.Capabilities.Database.UsesSqliteDatabase)
        {
            return (WriteRestriction.SqliteLibrary,
                "该机型使用 SQLite 音乐库（iTunes Library.itlp），本版本暂不支持写入，仍可浏览、播放与备份。");
        }

        var checksum = profile?.Capabilities.Database.Checksum ?? DatabaseChecksum.None;
        if (checksum != DatabaseChecksum.None && guid.Length != 8)
        {
            return (WriteRestriction.MissingGuid,
                "设备未提供 FireWire GUID，无法计算数据库签名，因此不能安全写入。");
        }

        if (checksum == DatabaseChecksum.HashAb && !HashAbSupport.IsAvailable)
        {
            return (WriteRestriction.UnsupportedSignature,
                "该机型需要 HASHAB 签名，当前构建尚未启用，只能浏览、播放与备份。");
        }

        return (WriteRestriction.None, string.Empty);
    }

    private static byte[] ReadGuid(DeviceCandidateInfo candidate, FilesystemSession session)
    {
        // 1) SysInfo 中的 FirewireGuid
        if (candidate.Candidate.SysInfoBytes is { Length: > 0 })
        {
            var evidence = DeviceMetadata.ParseSysInfo(candidate.Candidate.SysInfoBytes);
            var serial = evidence.TransportSerials.FirstOrDefault();
            if (serial is not null)
            {
                var bytes = TryHexToBytes(serial.Value);
                if (bytes.Length == 8)
                {
                    return bytes;
                }
            }
        }

        // 2) SysInfoExtended 中的 FireWireGUID
        if (candidate.Candidate.SysInfoExtendedBytes is { Length: > 0 })
        {
            var evidence = DeviceMetadata.ParseSysInfoExtended(candidate.Candidate.SysInfoExtendedBytes);
            var serial = evidence.TransportSerials.FirstOrDefault();
            if (serial is not null)
            {
                var bytes = TryHexToBytes(serial.Value);
                if (bytes.Length == 8)
                {
                    return bytes;
                }
            }
        }

        // 3) 设备上的 HashInfo 记录（UUID 字段的前 8 字节即 FireWire GUID）
        try
        {
            var hashInfoPath = SessionPath.Create(DevicePaths.HashInfo);
            if (session.FileExists(hashInfoPath))
            {
                var data = session.ReadAllBytes(hashInfoPath, 4096);
                var material = DatabaseSignature.ParseHashInfo(data);
                if (material.Uuid.Length >= 8)
                {
                    return material.Uuid[..8];
                }
            }
        }
        catch (Exception error)
        {
            AppLog.Warning("设备", $"读取 HashInfo 失败：{error.Message}");
        }

        return [];
    }

    private static byte[] TryHexToBytes(string hex)
    {
        var normalized = Identifiers.NormalizeSerial(hex);
        if (normalized.Length != 16)
        {
            // 有些设备给出的是 12 位十六进制串，左侧补零到 8 字节。
            if (normalized.Length is > 0 and < 16)
            {
                normalized = normalized.PadLeft(16, '0');
            }
            else
            {
                return [];
            }
        }

        try
        {
            return Convert.FromHexString(normalized);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static string ReadSerial(DeviceCandidateInfo candidate)
    {
        if (candidate.Candidate.SysInfoBytes is { Length: > 0 })
        {
            var evidence = DeviceMetadata.ParseSysInfo(candidate.Candidate.SysInfoBytes);
            var serial = evidence.ProductSerials.FirstOrDefault();
            if (serial is not null)
            {
                return serial.Value;
            }
        }

        return string.Empty;
    }

    private static async Task<string> ReadDeviceNameAsync(
        FilesystemSession session,
        CancellationToken cancellationToken)
    {
        // 设备名以主播放列表名为准；读取失败时回退到卷标。
        try
        {
            var dbPath = SessionPath.Create(DevicePaths.ITunesDb);
            var cdbPath = SessionPath.Create(DevicePaths.ITunesCdb);
            byte[]? bytes = null;

            if (session.FileExists(cdbPath))
            {
                var physical = await session.ReadAllBytesAsync(cdbPath, MaxDatabaseBytes, cancellationToken)
                    .ConfigureAwait(false);
                bytes = ITunesCdbCodec.IsITunesCdb(physical)
                    ? ITunesCdbCodec.Decompress(physical).LogicalBytes
                    : physical;
            }

            if (bytes is null && session.FileExists(dbPath))
            {
                bytes = await session.ReadAllBytesAsync(dbPath, MaxDatabaseBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (ITunesCdbCodec.IsITunesCdb(bytes))
                {
                    bytes = ITunesCdbCodec.Decompress(bytes).LogicalBytes;
                }
            }

            if (bytes is not null)
            {
                var database = ITunesDatabase.Parse(bytes);
                var master = database.MasterPlaylist;
                if (master is not null && master.Name.Length > 0)
                {
                    return master.Name;
                }
            }
        }
        catch (Exception error)
        {
            AppLog.Warning("设备", $"读取设备名称失败：{error.Message}");
        }

        return session.Identity.Label;
    }

    /// <summary>关闭当前设备。</summary>
    public void Close()
    {
        var previous = Current;
        if (previous is null)
        {
            return;
        }

        Current = null;
        previous.Session.Invalidate();
        AppLog.Info("设备", $"已关闭设备：{previous.Name}");
        CurrentChanged?.Invoke(this, null);
    }

    /// <summary>安全弹出设备。</summary>
    public bool Eject(ActiveDevice device)
    {
        AppLog.Info("设备", $"正在安全弹出 {device.Name}（{device.MountPoint}）…");
        var result = SafeRemoval.TryEject(device.MountPoint, out var message);
        if (result)
        {
            AppLog.Info("设备", message);
        }
        else
        {
            AppLog.Warning("设备", message);
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
    }
}

