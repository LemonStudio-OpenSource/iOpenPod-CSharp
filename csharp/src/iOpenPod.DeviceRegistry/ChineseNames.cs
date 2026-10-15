namespace IOpenPod.DeviceRegistry;

/// <summary>把目录中的英文枚举值翻译为完整中文。</summary>
public static class ChineseNames
{
    private static readonly Dictionary<string, string> FamilyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iPod"] = "iPod",
        ["iPod Mini"] = "iPod mini",
        ["iPod Nano"] = "iPod nano",
        ["iPod Classic"] = "iPod classic",
        ["iPod Shuffle"] = "iPod shuffle",
        ["iPod Touch"] = "iPod touch",
    };

    private static readonly Dictionary<string, string> GenerationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1st Gen"] = "第 1 代",
        ["2nd Gen"] = "第 2 代",
        ["3rd Gen"] = "第 3 代",
        ["4th Gen"] = "第 4 代",
        ["4th Gen (color)"] = "第 4 代（彩色）",
        ["4th Gen (mono)"] = "第 4 代（单色）",
        ["4th Gen (photo)"] = "第 4 代（照片）",
        ["5th Gen"] = "第 5 代",
        ["5.5th Gen"] = "第 5.5 代",
        ["6th Gen"] = "第 6 代",
        ["6.5th Gen"] = "第 6.5 代",
        ["7th Gen"] = "第 7 代",
    };

    private static readonly Dictionary<string, string> FinishNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Black"] = "黑色",
        ["White"] = "白色",
        ["Silver"] = "银色",
        ["Gold"] = "金色",
        ["Blue"] = "蓝色",
        ["Green"] = "绿色",
        ["Pink"] = "粉色",
        ["Purple"] = "紫色",
        ["Red"] = "红色",
        ["Yellow"] = "黄色",
        ["Orange"] = "橙色",
        ["Graphite"] = "石墨色",
        ["Slate"] = "石板灰",
        ["Space Gray"] = "深空灰",
        ["U2"] = "U2 特别版",
    };

    private static readonly Dictionary<StorageTechnology, string> StorageNames = new()
    {
        [StorageTechnology.HardDisk] = "机械硬盘",
        [StorageTechnology.Flash] = "闪存",
    };

    private static readonly Dictionary<DatabaseChecksum, string> ChecksumNames = new()
    {
        [DatabaseChecksum.None] = "无校验",
        [DatabaseChecksum.Hash58] = "HASH58 校验",
        [DatabaseChecksum.Hash72] = "HASH72 校验",
        [DatabaseChecksum.HashAb] = "HASHAB 校验",
    };

    private static readonly Dictionary<ConnectionMode, string> ConnectionNames = new()
    {
        [ConnectionMode.Normal] = "正常模式",
        [ConnectionMode.Recovery] = "恢复模式",
        [ConnectionMode.Unknown] = "未知模式",
    };

    public static string Family(string family) =>
        FamilyNames.TryGetValue(family, out var name) ? name : family;

    public static string Generation(string generation) =>
        GenerationNames.TryGetValue(generation, out var name) ? name : generation;

    public static string Finish(string finish) =>
        FinishNames.TryGetValue(finish, out var name) ? name : finish;

    public static string Storage(StorageTechnology technology) =>
        StorageNames.TryGetValue(technology, out var name) ? name : technology.ToString();

    public static string Checksum(DatabaseChecksum checksum) =>
        ChecksumNames.TryGetValue(checksum, out var name) ? name : checksum.ToString();

    public static string Connection(ConnectionMode mode) =>
        ConnectionNames.TryGetValue(mode, out var name) ? name : mode.ToString();

    public static string ArtworkRole(ArtworkUsage usage) => usage switch
    {
        ArtworkUsage.Cover => "专辑封面",
        ArtworkUsage.Chapter => "章节插图",
        ArtworkUsage.Photo => "照片",
        ArtworkUsage.PhotoThumbnail => "照片缩略图",
        ArtworkUsage.TvOutput => "电视输出",
        _ => usage.ToString(),
    };
}
