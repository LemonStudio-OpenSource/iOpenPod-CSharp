namespace IOpenPod.IPodDb;

/// <summary>已知的 iTunesDB 数据对象类型。</summary>
public enum MhodType
{
    Title = 1,
    Location = 2,
    Album = 3,
    Artist = 4,
    Genre = 5,
    FileType = 6,
    EqSetting = 7,
    Comment = 8,
    Category = 9,
    Lyrics = 10,
    Unknown11 = 11,
    Composer = 12,
    Grouping = 13,
    Description = 14,
    PodcastEnclosureUrl = 15,
    PodcastRssUrl = 16,
    ChapterData = 17,
    Subtitle = 18,
    Show = 19,
    Episode = 20,
    TvNetwork = 21,
    AlbumArtist = 22,
    SortArtist = 23,
    TrackKeywords = 24,
    ShowLocale = 25,
    ItunesStoreAssetInfo = 26,
    SortTitle = 27,
    SortAlbum = 28,
    SortAlbumArtist = 29,
    SortComposer = 30,
    SortShow = 31,
    VideoTrackData = 32,
    ContentProvider = 37,
    Copyright = 39,
    EncodingQualityDescriptor = 42,
    PurchaseAccount = 43,
    PurchaserName = 44,
    SmartPlaylistPreferences = 50,
    SmartPlaylistRules = 51,
    LibraryPlaylistIndex = 52,
    LibraryPlaylistJumpTable = 53,
    PlaylistPropertyPlist = 55,
    ColumnSizeOrPlaylistOrder = 100,
    PlaylistSettingsBinary = 102,
    AlbumItemAlbum = 200,
    AlbumItemArtist = 201,
    AlbumItemSortArtist = 202,
    AlbumItemPodcastUrl = 203,
    AlbumItemShow = 204,
    ArtistItemArtist = 300,
}

/// <summary>ArtworkDB 数据对象类型。</summary>
public enum ArtworkMhodType
{
    AlbumName = 1,
    ThumbnailImage = 2,
    FileName = 3,
    Unknown4 = 4,
    FullResolutionImage = 5,
    UnknownContainer6 = 6,
}

/// <summary>媒体类型标志（MHIT 偏移 0xD0）。</summary>
[Flags]
public enum MediaTypeFlags : uint
{
    AudioVideo = 0x00000000,
    Audio = 0x00000001,
    Video = 0x00000002,
    Podcast = 0x00000004,
    VideoPodcast = 0x00000006,
    Audiobook = 0x00000008,
    MusicVideo = 0x00000020,
    TvShow = 0x00000040,
    TvShowAlt = 0x00000060,
    Ringtone = 0x00004000,
    Rental = 0x00008000,
    ItunesExtra = 0x00010000,
    Memo = 0x00100000,
    ItunesU = 0x00200000,
    EpubBook = 0x00400000,
    PdfBook = 0x00800000,
}

/// <summary>iTunesDB 中使用的常量表。</summary>
public static class ITunesDbConstants
{
    /// <summary>文件扩展名对应的大端 ASCII 编码整数。</summary>
    public static readonly IReadOnlyDictionary<string, uint> FileExtensionCodes =
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["mp3"] = 0x4D503320,
            ["m4a"] = 0x4D344120,
            ["m4p"] = 0x4D345020,
            ["m4b"] = 0x4D344220,
            ["m4v"] = 0x4D345620,
            ["mp4"] = 0x4D503420,
            ["wav"] = 0x57415620,
            ["aif"] = 0x41494646,
            ["aiff"] = 0x41494646,
            ["aac"] = 0x41414320,
            ["alac"] = 0x4D344120,
            ["m4r"] = 0x4D345220,
        };

    /// <summary>音频格式标志（MHIT 偏移 0x7E）。</summary>
    public static readonly IReadOnlyDictionary<string, ushort> AudioFormatFlags =
        new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["wav"] = 0x0000,
            ["aif"] = 0x0000,
            ["aiff"] = 0x0000,
            ["m4b"] = 0x0001,
        };

    public const ushort DefaultAudioFormatFlag = 0xFFFF;

    /// <summary>播放列表排序方式的中文名称。</summary>
    public static readonly IReadOnlyDictionary<int, string> PlaylistSortOrders =
        new Dictionary<int, string>
        {
            [0] = "默认（未设置）",
            [1] = "播放列表顺序（手动）",
            [2] = "未知排序（2）",
            [3] = "标题",
            [4] = "专辑",
            [5] = "艺术家",
            [6] = "比特率",
            [7] = "流派",
            [8] = "媒体类型",
            [9] = "修改日期",
            [10] = "音轨号",
            [11] = "大小",
            [12] = "时长",
            [13] = "年份",
            [14] = "采样率",
            [15] = "备注",
            [16] = "添加日期",
            [17] = "均衡器",
            [18] = "作曲者",
            [19] = "未知排序（19）",
            [20] = "播放次数",
            [21] = "上次播放",
            [22] = "碟号",
            [23] = "我的评分",
            [24] = "发行日期",
            [25] = "每分钟节拍",
            [26] = "分组",
            [27] = "类别",
            [28] = "描述",
            [29] = "节目",
            [30] = "季",
            [31] = "集",
            [35] = "专辑艺术家",
            [36] = "艺术家（无排序字段）",
        };

    /// <summary>媒体类型的显示名称。</summary>
    public static string MediaTypeName(uint mediaType) => mediaType switch
    {
        0x00000000 => "音频/视频",
        0x00000001 => "音频",
        0x00000002 => "视频",
        0x00000004 => "播客",
        0x00000006 => "视频播客",
        0x00000008 => "有声书",
        0x00000020 => "音乐视频",
        0x00000040 => "电视节目",
        0x00000060 => "电视节目（备用）",
        0x00004000 => "铃声",
        0x00008000 => "租借影片",
        0x00010000 => "iTunes 附加内容",
        0x00100000 => "语音备忘录",
        0x00200000 => "iTunes U",
        0x00400000 => "EPUB 图书",
        0x00800000 => "PDF 图书",
        _ => $"未知媒体类型（0x{mediaType:X8}）",
    };

    /// <summary>把 MHIT 中的文件类型整数还原为扩展名。</summary>
    public static string FileTypeExtension(uint code)
    {
        foreach (var pair in FileExtensionCodes)
        {
            if (pair.Value == code)
            {
                return pair.Key;
            }
        }

        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, code);
        var text = System.Text.Encoding.ASCII.GetString(bytes).Trim();
        return text.Length == 0 ? "bin" : text.ToLowerInvariant();
    }
}
