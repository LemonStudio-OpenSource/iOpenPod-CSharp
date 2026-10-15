using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IOpenPod.DeviceRegistry;

/// <summary>
/// 内置 iPod 目录。
///
/// 目录数据源自由原 iOpenPod 研究整理出的完整机型表：覆盖第 1–5.5 代全尺寸 iPod、
/// iPod classic、iPod mini 以及 iPod nano 第 1–7 代。iPod shuffle 与 iPod touch
/// 不在支持范围内。
/// </summary>
public static class DeviceCatalog
{
    private static readonly Lazy<RegistryCatalog> LazyCatalog = new(Load, isThreadSafe: true);

    /// <summary>默认目录（首次访问时从内嵌资源解析并校验）。</summary>
    public static RegistryCatalog Default => LazyCatalog.Value;

    private static RegistryCatalog Load()
    {
        var assembly = typeof(DeviceCatalog).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(resource => resource.EndsWith("catalog.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("未找到内嵌的设备目录资源 catalog.json。");

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"无法打开内嵌资源 {name}。");

        var dto = JsonSerializer.Deserialize<CatalogDto>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        }) ?? throw new InvalidOperationException("设备目录内容为空或格式不正确。");

        var profiles = dto.Profiles.Select(ToProfile).ToArray();
        var suffixes = dto.SerialSuffixes
            .Select(item => new SerialSuffixDefinition(item.Suffix, item.ModelNumber))
            .ToArray();
        var usbProducts = dto.UsbProducts.Select(item => new UsbProductDefinition(
            new UsbIdentifier((ushort)item.Identifier.VendorId, (ushort)item.Identifier.ProductId),
            ParseConnectionMode(item.Mode),
            item.CandidateIdentities
                .Select(identity => new ModelIdentity(identity.Family, identity.Generation))
                .ToArray()))
            .ToArray();

        return new RegistryCatalog(profiles, suffixes, usbProducts);
    }

    private static ConnectionMode ParseConnectionMode(string mode) => mode?.ToLowerInvariant() switch
    {
        "normal" => ConnectionMode.Normal,
        "recovery" => ConnectionMode.Recovery,
        _ => ConnectionMode.Unknown,
    };

    private static DeviceProfile ToProfile(ProfileDto dto) => new(
        dto.ModelNumber,
        dto.Family,
        dto.Generation,
        dto.AdvertisedCapacity,
        dto.Finish,
        dto.ProductImage,
        string.Equals(dto.StorageTechnology, "flash", StringComparison.OrdinalIgnoreCase)
            ? StorageTechnology.Flash
            : StorageTechnology.HardDisk,
        new DeviceCapabilities(
            new DisplayCapabilities(dto.Capabilities.Display.Width, dto.Capabilities.Display.Height, dto.Capabilities.Display.Color),
            new AudioCapabilities(
                dto.Capabilities.Audio.SupportsPodcasts,
                dto.Capabilities.Audio.SupportsGaplessPlayback,
                dto.Capabilities.Audio.SupportsAlac,
                dto.Capabilities.Audio.MaxSampleRateHz,
                dto.Capabilities.Audio.MaxChannels,
                dto.Capabilities.Audio.MaxLosslessBits,
                dto.Capabilities.Audio.MaxAacBitrateKbps),
            new ArtworkCapabilities(
                dto.Capabilities.Artwork.SupportsCoverArt,
                dto.Capabilities.Artwork.SupportsPhotos,
                dto.Capabilities.Artwork.SupportsChapterImages,
                dto.Capabilities.Artwork.SupportsSparseArtwork,
                dto.Capabilities.Artwork.CoverFormats.Select(ToArtworkFormat).ToArray(),
                dto.Capabilities.Artwork.PhotoFormats.Select(ToArtworkFormat).ToArray(),
                dto.Capabilities.Artwork.ArtworkRootValue,
                dto.Capabilities.Artwork.PhotoAlbumCreationType is null
                    ? null
                    : (byte)dto.Capabilities.Artwork.PhotoAlbumCreationType.Value,
                dto.Capabilities.Artwork.PhotosRootValue,
                dto.Capabilities.Artwork.ChapterFormats.Select(ToArtworkFormat).ToArray()),
            new VideoCapabilities(
                dto.Capabilities.Video.Supported,
                dto.Capabilities.Video.MaxWidth,
                dto.Capabilities.Video.MaxHeight,
                dto.Capabilities.Video.MaxBitrateKbps,
                dto.Capabilities.Video.H264Level,
                dto.Capabilities.Video.SupportsTx3gSubtitles,
                dto.Capabilities.Video.SupportsCea608Captions,
                dto.Capabilities.Video.MaxFps,
                dto.Capabilities.Video.H264Profiles,
                dto.Capabilities.Video.MaxAudioBitrateKbps),
            new DatabaseCapabilities(
                ParseChecksum(dto.Capabilities.Database.Checksum),
                dto.Capabilities.Database.BinaryVersion,
                dto.Capabilities.Database.MusicDirectoryCount,
                dto.Capabilities.Database.MaxDatabaseBytes,
                dto.Capabilities.Database.SupportsCompressedDatabase,
                dto.Capabilities.Database.UsesSqliteDatabase,
                ParseChecksum(dto.Capabilities.Database.SqliteChecksum))));

    private static DatabaseChecksum ParseChecksum(string? value) => value?.ToLowerInvariant() switch
    {
        "hash58" => DatabaseChecksum.Hash58,
        "hash72" => DatabaseChecksum.Hash72,
        "hashab" => DatabaseChecksum.HashAb,
        _ => DatabaseChecksum.None,
    };

    private static ArtworkFormat ToArtworkFormat(ArtworkFormatDto dto) => new(
        dto.FormatId,
        dto.Width,
        dto.Height,
        dto.RowBytes,
        ParsePixelFormat(dto.PixelFormat),
        ParseUsage(dto.Usage));

    private static ArtworkPixelFormat ParsePixelFormat(string value) => value switch
    {
        "RGB565_LE" => ArtworkPixelFormat.Rgb565Le,
        "RGB565_BE" => ArtworkPixelFormat.Rgb565Be,
        "RGB565_BE_90" => ArtworkPixelFormat.Rgb565Be90,
        "RGB555_LE" => ArtworkPixelFormat.Rgb555Le,
        "RGB555_BE" => ArtworkPixelFormat.Rgb555Be,
        "REC_RGB555_LE" => ArtworkPixelFormat.RecRgb555Le,
        "UYVY" => ArtworkPixelFormat.Uyvy,
        "UYVY_FIELDS" => ArtworkPixelFormat.UyvyFields,
        "I420_LE" => ArtworkPixelFormat.I420Le,
        "JPEG" => ArtworkPixelFormat.Jpeg,
        _ => ArtworkPixelFormat.Rgb565Le,
    };

    private static ArtworkUsage ParseUsage(string value) => value switch
    {
        "cover" => ArtworkUsage.Cover,
        "chapter" => ArtworkUsage.Chapter,
        "photo" => ArtworkUsage.Photo,
        "photo_thumbnail" => ArtworkUsage.PhotoThumbnail,
        "tv_output" => ArtworkUsage.TvOutput,
        _ => ArtworkUsage.Cover,
    };

    // ---- JSON 传输对象 ----

    private sealed class CatalogDto
    {
        [JsonPropertyName("profiles")] public List<ProfileDto> Profiles { get; set; } = [];
        [JsonPropertyName("serialSuffixes")] public List<SerialSuffixDto> SerialSuffixes { get; set; } = [];
        [JsonPropertyName("usbProducts")] public List<UsbProductDto> UsbProducts { get; set; } = [];
    }

    private sealed class ProfileDto
    {
        [JsonPropertyName("model_number")] public string ModelNumber { get; set; } = "";
        [JsonPropertyName("family")] public string Family { get; set; } = "";
        [JsonPropertyName("generation")] public string Generation { get; set; } = "";
        [JsonPropertyName("advertised_capacity")] public string AdvertisedCapacity { get; set; } = "";
        [JsonPropertyName("finish")] public string Finish { get; set; } = "";
        [JsonPropertyName("product_image")] public string ProductImage { get; set; } = "";
        [JsonPropertyName("storage_technology")] public string StorageTechnology { get; set; } = "";
        [JsonPropertyName("capabilities")] public CapabilitiesDto Capabilities { get; set; } = new();
    }

    private sealed class CapabilitiesDto
    {
        [JsonPropertyName("display")] public DisplayDto Display { get; set; } = new();
        [JsonPropertyName("audio")] public AudioDto Audio { get; set; } = new();
        [JsonPropertyName("artwork")] public ArtworkDto Artwork { get; set; } = new();
        [JsonPropertyName("video")] public VideoDto Video { get; set; } = new();
        [JsonPropertyName("database")] public DatabaseDto Database { get; set; } = new();
    }

    private sealed class DisplayDto
    {
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("color")] public bool Color { get; set; }
    }

    private sealed class AudioDto
    {
        [JsonPropertyName("supports_podcasts")] public bool SupportsPodcasts { get; set; }
        [JsonPropertyName("supports_gapless_playback")] public bool SupportsGaplessPlayback { get; set; }
        [JsonPropertyName("supports_alac")] public bool SupportsAlac { get; set; }
        [JsonPropertyName("max_sample_rate_hz")] public int MaxSampleRateHz { get; set; } = 48000;
        [JsonPropertyName("max_channels")] public int MaxChannels { get; set; } = 2;
        [JsonPropertyName("max_lossless_bits")] public int MaxLosslessBits { get; set; } = 16;
        [JsonPropertyName("max_aac_bitrate_kbps")] public int MaxAacBitrateKbps { get; set; } = 320;
    }

    private sealed class ArtworkDto
    {
        [JsonPropertyName("supports_cover_art")] public bool SupportsCoverArt { get; set; }
        [JsonPropertyName("supports_photos")] public bool SupportsPhotos { get; set; }
        [JsonPropertyName("supports_chapter_images")] public bool SupportsChapterImages { get; set; }
        [JsonPropertyName("supports_sparse_artwork")] public bool SupportsSparseArtwork { get; set; }
        [JsonPropertyName("cover_formats")] public List<ArtworkFormatDto> CoverFormats { get; set; } = [];
        [JsonPropertyName("photo_formats")] public List<ArtworkFormatDto> PhotoFormats { get; set; } = [];
        [JsonPropertyName("artwork_root_value")] public uint? ArtworkRootValue { get; set; }
        [JsonPropertyName("photo_album_creation_type")] public int? PhotoAlbumCreationType { get; set; }
        [JsonPropertyName("photos_root_value")] public uint? PhotosRootValue { get; set; }
        [JsonPropertyName("chapter_formats")] public List<ArtworkFormatDto> ChapterFormats { get; set; } = [];
    }

    private sealed class ArtworkFormatDto
    {
        [JsonPropertyName("format_id")] public int FormatId { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("row_bytes")] public int RowBytes { get; set; }
        [JsonPropertyName("pixel_format")] public string PixelFormat { get; set; } = "";
        [JsonPropertyName("usage")] public string Usage { get; set; } = "";
    }

    private sealed class VideoDto
    {
        [JsonPropertyName("supported")] public bool Supported { get; set; }
        [JsonPropertyName("max_width")] public int MaxWidth { get; set; }
        [JsonPropertyName("max_height")] public int MaxHeight { get; set; }
        [JsonPropertyName("max_bitrate_kbps")] public int MaxBitrateKbps { get; set; }
        [JsonPropertyName("h264_level")] public string H264Level { get; set; } = "";
        [JsonPropertyName("supports_tx3g_subtitles")] public bool SupportsTx3gSubtitles { get; set; }
        [JsonPropertyName("supports_cea608_captions")] public bool SupportsCea608Captions { get; set; }
        [JsonPropertyName("max_fps")] public int MaxFps { get; set; } = 30;
        [JsonPropertyName("h264_profiles")] public List<string>? H264Profiles { get; set; }
        [JsonPropertyName("max_audio_bitrate_kbps")] public int MaxAudioBitrateKbps { get; set; } = 160;
    }

    private sealed class DatabaseDto
    {
        [JsonPropertyName("checksum")] public string Checksum { get; set; } = "none";
        [JsonPropertyName("binary_version")] public int BinaryVersion { get; set; }
        [JsonPropertyName("music_directory_count")] public int MusicDirectoryCount { get; set; }
        [JsonPropertyName("max_database_bytes")] public long MaxDatabaseBytes { get; set; }
        [JsonPropertyName("supports_compressed_database")] public bool SupportsCompressedDatabase { get; set; }
        [JsonPropertyName("uses_sqlite_database")] public bool UsesSqliteDatabase { get; set; }
        [JsonPropertyName("sqlite_checksum")] public string SqliteChecksum { get; set; } = "none";
    }

    private sealed class SerialSuffixDto
    {
        [JsonPropertyName("suffix")] public string Suffix { get; set; } = "";
        [JsonPropertyName("model_number")] public string ModelNumber { get; set; } = "";
    }

    private sealed class UsbProductDto
    {
        [JsonPropertyName("identifier")] public UsbIdentifierDto Identifier { get; set; } = new();
        [JsonPropertyName("mode")] public string Mode { get; set; } = "normal";
        [JsonPropertyName("candidate_identities")] public List<ModelIdentityDto> CandidateIdentities { get; set; } = [];
    }

    private sealed class UsbIdentifierDto
    {
        [JsonPropertyName("vendor_id")] public int VendorId { get; set; }
        [JsonPropertyName("product_id")] public int ProductId { get; set; }
    }

    private sealed class ModelIdentityDto
    {
        [JsonPropertyName("family")] public string Family { get; set; } = "";
        [JsonPropertyName("generation")] public string Generation { get; set; } = "";
    }
}
