namespace IOpenPod.DeviceRegistry;

/// <summary>证据的相对权威性。数值越大越可信。</summary>
public enum EvidenceAuthority
{
    Derived = 10,
    DeviceMetadata = 20,
    CurrentHardware = 30,
}

/// <summary>一个带来源与权威性的类型化标识符。</summary>
public sealed record DeviceIdentifier<T>(T Value, string Source, EvidenceAuthority Authority);

/// <summary>成对的 USB 厂商与产品标识。</summary>
public sealed record UsbIdentifier(ushort VendorId, ushort ProductId);

/// <summary>一个当前被考虑的物理设备的全部身份观测。</summary>
public sealed record DeviceEvidence
{
    public IReadOnlyList<DeviceIdentifier<string>> ModelNumbers { get; init; } = [];

    public IReadOnlyList<DeviceIdentifier<string>> ProductSerials { get; init; } = [];

    public IReadOnlyList<DeviceIdentifier<string>> TransportSerials { get; init; } = [];

    public IReadOnlyList<DeviceIdentifier<UsbIdentifier>> UsbIdentifiers { get; init; } = [];

    public IReadOnlyList<DeviceIdentifier<string>> BoardHardwareNames { get; init; } = [];

    public IReadOnlyList<DeviceIdentifier<string>> FirmwareVersions { get; init; } = [];

    public IReadOnlyList<DeviceIdentifier<int>> FamilyIds { get; init; } = [];

    public IReadOnlyList<DeviceIdentifier<int>> UpdaterFamilyIds { get; init; } = [];

    public static DeviceEvidence Empty { get; } = new();

    /// <summary>合并另一份观测，保留双方的来源信息。</summary>
    public DeviceEvidence MergeWith(DeviceEvidence other) => new()
    {
        ModelNumbers = [.. ModelNumbers, .. other.ModelNumbers],
        ProductSerials = [.. ProductSerials, .. other.ProductSerials],
        TransportSerials = [.. TransportSerials, .. other.TransportSerials],
        UsbIdentifiers = [.. UsbIdentifiers, .. other.UsbIdentifiers],
        BoardHardwareNames = [.. BoardHardwareNames, .. other.BoardHardwareNames],
        FirmwareVersions = [.. FirmwareVersions, .. other.FirmwareVersions],
        FamilyIds = [.. FamilyIds, .. other.FamilyIds],
        UpdaterFamilyIds = [.. UpdaterFamilyIds, .. other.UpdaterFamilyIds],
    };
}

public enum StorageTechnology
{
    HardDisk,
    Flash,
}

public enum DatabaseChecksum
{
    None,
    Hash58,
    Hash72,
    HashAb,
}

public enum ArtworkPixelFormat
{
    Rgb565Le,
    Rgb565Be,
    Rgb565Be90,
    Rgb555Le,
    Rgb555Be,
    RecRgb555Le,
    Uyvy,
    UyvyFields,
    I420Le,
    Jpeg,
}

public enum ArtworkUsage
{
    Cover,
    Chapter,
    Photo,
    PhotoThumbnail,
    TvOutput,
}

/// <summary>一个设备专属的 iTHMB 图像格式定义。</summary>
public sealed record ArtworkFormat(
    int FormatId,
    int Width,
    int Height,
    int RowBytes,
    ArtworkPixelFormat PixelFormat,
    ArtworkUsage Usage);

public sealed record DisplayCapabilities(int Width, int Height, bool Color);

public sealed record AudioCapabilities(
    bool SupportsPodcasts,
    bool SupportsGaplessPlayback,
    bool SupportsAlac,
    int MaxSampleRateHz = 48000,
    int MaxChannels = 2,
    int MaxLosslessBits = 16,
    int MaxAacBitrateKbps = 320);

public sealed record ArtworkCapabilities(
    bool SupportsCoverArt,
    bool SupportsPhotos,
    bool SupportsChapterImages,
    bool SupportsSparseArtwork,
    IReadOnlyList<ArtworkFormat> CoverFormats,
    IReadOnlyList<ArtworkFormat> PhotoFormats,
    uint? ArtworkRootValue,
    byte? PhotoAlbumCreationType,
    uint? PhotosRootValue,
    IReadOnlyList<ArtworkFormat> ChapterFormats)
{
    /// <summary>返回本设备档案对某个封面格式 ID 的定义。</summary>
    public ArtworkFormat? CoverFormat(int formatId) =>
        CoverFormats.FirstOrDefault(format => format.FormatId == formatId);
}

public sealed record VideoCapabilities(
    bool Supported,
    int MaxWidth = 0,
    int MaxHeight = 0,
    int MaxBitrateKbps = 0,
    string H264Level = "",
    bool SupportsTx3gSubtitles = false,
    bool SupportsCea608Captions = false,
    int MaxFps = 30,
    IReadOnlyList<string>? H264Profiles = null,
    int MaxAudioBitrateKbps = 160);

public sealed record DatabaseCapabilities(
    DatabaseChecksum Checksum,
    int BinaryVersion,
    int MusicDirectoryCount,
    long MaxDatabaseBytes,
    bool SupportsCompressedDatabase = false,
    bool UsesSqliteDatabase = false,
    DatabaseChecksum SqliteChecksum = DatabaseChecksum.None);

public sealed record DeviceCapabilities(
    DisplayCapabilities Display,
    AudioCapabilities Audio,
    ArtworkCapabilities Artwork,
    VideoCapabilities Video,
    DatabaseCapabilities Database);

/// <summary>目录中一个具体在售 iPod 型号的描述。</summary>
public sealed record DeviceProfile(
    string ModelNumber,
    string Family,
    string Generation,
    string AdvertisedCapacity,
    string Finish,
    string ProductImage,
    StorageTechnology StorageTechnology,
    DeviceCapabilities Capabilities)
{
    /// <summary>英文显示名，保留品牌原始拼写。</summary>
    public string DisplayName => $"{Family} {Generation}";

    /// <summary>完全本地化的中文显示名。</summary>
    public string ChineseDisplayName =>
        $"{ChineseNames.Family(Family)} {ChineseNames.Generation(Generation)}";

    /// <summary>中文完整描述，例如 <c>iPod nano 第 4 代 8GB 银色</c>。</summary>
    public string ChineseDescription =>
        $"{ChineseDisplayName} {AdvertisedCapacity} {ChineseNames.Finish(Finish)}".Trim();
}

/// <summary>目录中的一种机型身份（家族 + 世代）。</summary>
public sealed record ModelIdentity(string Family, string Generation);

public enum ConnectionMode
{
    Normal,
    Recovery,
    Unknown,
}

public sealed record SerialSuffixDefinition(string Suffix, string ModelNumber);

public sealed record UsbProductDefinition(
    UsbIdentifier Identifier,
    ConnectionMode Mode,
    IReadOnlyList<ModelIdentity> CandidateIdentities);

/// <summary>不可变的 iPod 目录。</summary>
public sealed record RegistryCatalog(
    IReadOnlyList<DeviceProfile> Profiles,
    IReadOnlyList<SerialSuffixDefinition> SerialSuffixes,
    IReadOnlyList<UsbProductDefinition> UsbProducts);

public enum IdentifierKind
{
    ModelNumber,
    ProductSerial,
    TransportSerial,
    Usb,
}

public sealed record IdentifierSummary(
    IdentifierKind Kind,
    string Value,
    string Source,
    EvidenceAuthority Authority);

public enum IdentificationIssueCode
{
    ExactIdentifiersDisagree,
    LowerAuthorityIdentifierDisagrees,
    UsbIdentityDisagrees,
    UsbIdentifiersDisagree,
}

public sealed record IdentificationIssue(
    IdentificationIssueCode Code,
    IReadOnlyList<IdentifierSummary> Identifiers)
{
    /// <summary>面向用户的中文说明。</summary>
    public string ChineseMessage => Code switch
    {
        IdentificationIssueCode.ExactIdentifiersDisagree =>
            "多个同等权威的身份证据指向了不同型号。",
        IdentificationIssueCode.LowerAuthorityIdentifierDisagrees =>
            "较低权威的身份证据与主要证据不一致，已采用主要证据。",
        IdentificationIssueCode.UsbIdentityDisagrees =>
            "USB 硬件标识与设备元数据指向的型号不一致。",
        IdentificationIssueCode.UsbIdentifiersDisagree =>
            "多个 USB 标识互相矛盾。",
        _ => "识别过程中出现未知问题。",
    };
}

public enum IdentificationStatus
{
    Exact,
    Ambiguous,
    Conflicting,
    RecoveryMode,
    Unknown,
}

/// <summary>设备注册表返回的完整识别结果（不授予任何写入权限）。</summary>
public sealed record IdentificationResult(
    IdentificationStatus Status,
    ConnectionMode ConnectionMode,
    DeviceProfile? Profile = null,
    IReadOnlyList<DeviceProfile>? Candidates = null,
    IReadOnlyList<IdentificationIssue>? Issues = null)
{
    public IReadOnlyList<DeviceProfile> CandidateList => Candidates ?? [];

    public IReadOnlyList<IdentificationIssue> IssueList => Issues ?? [];

    /// <summary>面向用户的中文状态说明。</summary>
    public string ChineseStatus => Status switch
    {
        IdentificationStatus.Exact => "已精确识别",
        IdentificationStatus.Ambiguous => "型号不唯一",
        IdentificationStatus.Conflicting => "证据冲突",
        IdentificationStatus.RecoveryMode => "恢复模式",
        _ => "未知设备",
    };
}
