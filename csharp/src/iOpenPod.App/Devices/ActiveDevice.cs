using IOpenPod.App.Databases;
using IOpenPod.App.Logging;
using IOpenPod.DeviceRegistry;
using IOpenPod.IPodDb;
using IOpenPod.IPodDb.Artwork;
using IOpenPod.IPodDb.Chunks;
using IOpenPod.Storage;

namespace IOpenPod.App.Devices;

/// <summary>设备上的标准相对路径。</summary>
public static class DevicePaths
{
    public const string Control = "iPod_Control";
    public const string SysInfo = "iPod_Control/Device/SysInfo";
    public const string SysInfoExtended = "iPod_Control/Device/SysInfoExtended";
    public const string HashInfo = "iPod_Control/Device/HashInfo";
    public const string ITunesDirectory = "iPod_Control/iTunes";
    public const string ITunesDb = "iPod_Control/iTunes/iTunesDB";
    public const string ITunesCdb = "iPod_Control/iTunes/iTunesCDB";
    public const string SqliteDirectory = "iPod_Control/iTunes/iTunes Library.itlp";
    public const string ArtworkDirectory = "iPod_Control/Artwork";
    public const string ArtworkDb = "iPod_Control/Artwork/ArtworkDB";
    public const string PhotosDb = "Photos/Photo Database";
}

/// <summary>写入能力被限制的原因。</summary>
public enum WriteRestriction
{
    /// <summary>可以写入。</summary>
    None,

    /// <summary>卷本身被标记为只读。</summary>
    VolumeReadOnly,

    /// <summary>设备未提供签名所需的 GUID。</summary>
    MissingGuid,

    /// <summary>该机型的签名算法尚未支持。</summary>
    UnsupportedSignature,

    /// <summary>该机型使用 SQLite 音乐库，写入尚未支持。</summary>
    SqliteLibrary,
}

/// <summary>一个已经打开并加载完成的 iPod。</summary>
public sealed class ActiveDevice : IAsyncDisposable
{
    internal ActiveDevice(
        FilesystemSession session,
        IdentificationResult identification,
        DeviceProfile? profile,
        string name,
        string serialNumber,
        byte[] guid,
        ITunesDatabase database,
        ITunesCdb framing,
        byte[] physicalDatabaseBytes,
        ArtworkDatabase? artwork,
        WriteRestriction restriction,
        string restrictionReason)
    {
        Session = session;
        Identification = identification;
        Profile = profile;
        Name = name;
        SerialNumber = serialNumber;
        Guid = guid;
        Database = database;
        CdbFraming = framing;
        PhysicalDatabaseBytes = physicalDatabaseBytes;
        Artwork = artwork;
        Restriction = restriction;
        RestrictionReason = restrictionReason;
    }

    public FilesystemSession Session { get; }

    public IdentificationResult Identification { get; }

    public DeviceProfile? Profile { get; }

    /// <summary>iPod 显示名称。</summary>
    public string Name { get; internal set; }

    public string SerialNumber { get; }

    public byte[] Guid { get; }

    public ITunesDatabase Database { get; }

    public ITunesCdb CdbFraming { get; }

    /// <summary>设备上当前的物理数据库字节（压缩或未压缩），用于恢复 HASH72 材料与完整性校验。</summary>
    public byte[] PhysicalDatabaseBytes { get; internal set; }

    public ArtworkDatabase? Artwork { get; internal set; }

    public WriteRestriction Restriction { get; }

    public string RestrictionReason { get; }

    public bool CanWrite => Restriction == WriteRestriction.None && Session.IsWriteSafe;

    public string MountPoint => Session.MountPoint;

    /// <summary>设备使用压缩的 iTunesCDB。</summary>
    public bool IsCompressed => CdbFraming.SourceBytes.Length > 0;

    /// <summary>型号显示名（中文）。</summary>
    public string ModelDisplayName => Profile?.ChineseDisplayName ?? "未知 iPod 型号";

    /// <summary>型号详细信息。</summary>
    public string ModelDetail => Profile is null
        ? Identification.ChineseStatus
        : $"{Profile.ChineseDescription} · {ChineseNames.Storage(Profile.StorageTechnology)} · {ChineseNames.Checksum(Profile.Capabilities.Database.Checksum)}";

    public int TrackCount => Database.TrackCount;

    public int PlaylistCount => Database.UserPlaylists.Count;

    public override string ToString() => $"{Name}（{ModelDisplayName}）";

    public ValueTask DisposeAsync()
    {
        Session.Dispose();
        return ValueTask.CompletedTask;
    }
}
