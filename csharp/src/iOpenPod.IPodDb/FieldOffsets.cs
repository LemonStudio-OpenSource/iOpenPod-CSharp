using IOpenPod.IPodDb.Chunks;

namespace IOpenPod.IPodDb;

/// <summary>MHIT（曲目条目）头部字段偏移表。</summary>
public static class MhitFields
{
    public const int ChildCount = 0x0C;
    public const int TrackId = 0x10;
    public const int UnknownClassFlag = 0x14;
    public const int FileType = 0x18;
    public const int VbrFlag = 0x1C;
    public const int Mp3Flag = 0x1D;
    public const int CompilationFlag = 0x1E;
    public const int Rating = 0x1F;
    public const int LastModified = 0x20;
    public const int Size = 0x24;
    public const int Length = 0x28;
    public const int TrackNumber = 0x2C;
    public const int TotalTracks = 0x30;
    public const int Year = 0x34;
    public const int Bitrate = 0x38;
    public const int SampleRate1 = 0x3C;
    public const int Volume = 0x40;
    public const int StartTime = 0x44;
    public const int StopTime = 0x48;
    public const int SoundCheck = 0x4C;
    public const int PlayCount1 = 0x50;
    public const int PlayCount2 = 0x54;
    public const int LastPlayed = 0x58;
    public const int DiscNumber = 0x5C;
    public const int TotalDiscs = 0x60;
    public const int UserId = 0x64;
    public const int DateAdded = 0x68;
    public const int BookmarkTime = 0x6C;
    public const int DbTrackId = 0x70;
    public const int CheckedFlag = 0x78;
    public const int AppRating = 0x79;
    public const int Bpm = 0x7A;
    public const int ArtworkCount = 0x7C;
    public const int AudioFormatFlag = 0x7E;
    public const int ArtworkSize = 0x80;
    public const int SampleRate2 = 0x88;
    public const int DateReleased = 0x8C;
    public const int MpegAudioType = 0x90;
    public const int ExplicitFlag = 0x92;
    public const int PurchasedAacFlag = 0x93;
    public const int GeniusCategoryId = 0x98;
    public const int SkipCount = 0x9C;
    public const int LastSkipped = 0xA0;
    public const int HasArtwork = 0xA4;
    public const int SkipWhenShuffling = 0xA5;
    public const int RememberPosition = 0xA6;
    public const int PodcastNowPlayingFlag = 0xA7;
    public const int DbTrackId2 = 0xA8;
    public const int LyricsFlag = 0xB0;
    public const int VideoFlag = 0xB1;
    public const int NotPlayedFlag = 0xB2;
    public const int BookmarkTime2 = 0xB4;
    public const int Pregap = 0xB8;
    public const int SampleCount = 0xBC;
    public const int Postgap = 0xC8;
    public const int Encoder = 0xCC;
    public const int MediaType = 0xD0;
    public const int SeasonNumber = 0xD4;
    public const int EpisodeNumber = 0xD8;
    public const int DateAddedToItunes = 0xDC;
    public const int StoreTrackId = 0xE0;
    public const int StoreEncoderVersion = 0xE4;
    public const int StoreArtistId = 0xE8;
    public const int StoreAlbumId = 0xF0;
    public const int GaplessAudioPayloadSize = 0xF8;
    public const int GaplessTrackFlag = 0x100;
    public const int GaplessAlbumFlag = 0x102;
    public const int AlbumId = 0x120;
    public const int DbId2Ref = 0x124;
    public const int Size2 = 0x12C;
    public const int SortMhodIndicators = 0x134;
    public const int ArtworkIdRef = 0x160;
    public const int VideoFlag2 = 0x194;
    public const int PurchasedAacFlag2 = 0x195;
    public const int StoreTrackId2 = 0x1B0;
    public const int ArtistIdRef = 0x1E0;
    public const int ComposerId = 0x1F4;
    public const int TvShowMediaTypeFlag = 0x1F8;

    /// <summary>为给定的数据库版本选择曲目头部长度。</summary>
    public static int HeaderSizeForVersion(uint databaseVersion) => databaseVersion switch
    {
        <= 0x12 => 0x9C,
        <= 0x19 => 0x148,
        <= 0x2D => 0x1F8,
        _ => 0x270,
    };
}

/// <summary>MHYP（播放列表）头部字段偏移表。</summary>
public static class MhypFields
{
    public const int MhodChildCount = 0x0C;
    public const int MhipChildCount = 0x10;
    public const int MasterFlag = 0x14;
    public const int Flag1 = 0x15;
    public const int Flag2 = 0x16;
    public const int Flag3 = 0x17;
    public const int Timestamp = 0x18;
    public const int PlaylistId = 0x1C;
    public const int StringMhodChildCount = 0x28;
    public const int PlaylistKindFlags = 0x2A;
    public const int SortOrder = 0x2C;
    public const int ParentFolderPlaylistId = 0x30;
    public const int DbId2 = 0x3C;
    public const int PlaylistId2 = 0x44;
    public const int Mhsd5Type = 0x50;
    public const int PhaseGameFlag = 0x52;
    public const int Timestamp2 = 0x58;
}

/// <summary>MHIP（播放列表条目）头部字段偏移表。</summary>
public static class MhipFields
{
    public const int ChildCount = 0x0C;
    public const int PodcastGroupFlag = 0x10;
    public const int GroupId = 0x14;
    public const int TrackId = 0x18;
    public const int Timestamp = 0x1C;
    public const int GroupIdRef = 0x20;
    public const int GroupPersistentId = 0x24;
    public const int TrackPersistentId = 0x2C;
    public const int MhipPersistentId = 0x3C;
}

/// <summary>MHBD（数据库根）头部字段偏移表。</summary>
public static class MhbdFields
{
    public const int Compressed = 0x0C;
    public const int Version = 0x10;
    public const int ChildCount = 0x14;
    public const int DbId = 0x18;
    public const int Platform = 0x20;
    public const int DbId2 = 0x24;
    public const int HashingScheme = 0x30;
    public const int Hash58 = 0x58;
    public const int TimezoneOffset = 0x6C;
    public const int HashTypeIndicator = 0x70;
    public const int Hash72 = 0x72;
    public const int CdbFlag = 0xA8;
    public const int HashAb = 0xAB;
    public const int HeaderSize = 244;
    public const int LegacyHeaderSize = 0x68;
}
