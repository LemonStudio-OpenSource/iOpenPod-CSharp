using IOpenPod.IPodDb.Chunks;

namespace IOpenPod.IPodDb;

/// <summary>
/// iTunesDB 与 ArtworkDB 的结构定义表。
///
/// 这是工程中唯一保存“哪个字段在哪个偏移、多宽、什么含义”的地方，
/// 解析器与写入器共享它，因此格式知识不会重复。
/// </summary>
public static class DatabaseDefinitions
{
    // ---- iTunesDB ----

    public static ChunkSpec Mhbd { get; } = new(
        "mhbd", "iTunesDB 根块", [244], 24, ChunkExtent.Length, ChunkBody.Children, [["mhsd"]]);

    public static ChunkSpec Mhsd { get; } = new(
        "mhsd", "iTunesDB 数据集包装块", [96], 16, ChunkExtent.Length, ChunkBody.Dataset, []);

    public static ChunkSpec Mhlt { get; } = new(
        "mhlt", "曲目列表", [92], 12, ChunkExtent.ChildCount, ChunkBody.Children, [["mhit"]]);

    public static ChunkSpec Mhit { get; } = new(
        "mhit", "曲目条目", [0x9C, 0x148, 0x1F8, 0x270], 16, ChunkExtent.Length, ChunkBody.Children, [["mhod"]]);

    public static ChunkSpec Mhlp { get; } = new(
        "mhlp", "播放列表列表", [92], 12, ChunkExtent.ChildCount, ChunkBody.Children, [["mhyp"]]);

    public static ChunkSpec Mhyp { get; } = new(
        "mhyp", "播放列表", [184], 20, ChunkExtent.Length, ChunkBody.Children, [["mhod"], ["mhip"]]);

    public static ChunkSpec Mhip { get; } = new(
        "mhip", "播放列表条目", [76], 16, ChunkExtent.Length, ChunkBody.Children, [["mhod"]]);

    public static ChunkSpec Mhla { get; } = new(
        "mhla", "专辑列表", [92], 12, ChunkExtent.ChildCount, ChunkBody.Children, [["mhia"]]);

    public static ChunkSpec Mhia { get; } = new(
        "mhia", "专辑条目", [88], 16, ChunkExtent.Length, ChunkBody.Children, [["mhod"]]);

    public static ChunkSpec Mhli { get; } = new(
        "mhli", "艺术家列表", [92], 12, ChunkExtent.ChildCount, ChunkBody.Children, [["mhii"]]);

    public static ChunkSpec Mhii { get; } = new(
        "mhii", "艺术家条目", [80], 16, ChunkExtent.Length, ChunkBody.Children, [["mhod"]]);

    public static ChunkSpec Mhod { get; } = new(
        "mhod", "类型化数据对象", [24], 16, ChunkExtent.Length, ChunkBody.Mhod, []);

    public static DatabaseDefinition ITunesDb { get; } = new(
        Mhbd,
        [Mhbd, Mhsd, Mhlt, Mhit, Mhlp, Mhyp, Mhip, Mhla, Mhia, Mhli, Mhii, Mhod],
        [
            new DatasetSpec(1, "曲目列表", "mhlt"),
            new DatasetSpec(2, "播放列表列表", "mhlp"),
            new DatasetSpec(3, "播客播放列表列表", "mhlp"),
            new DatasetSpec(4, "专辑列表", "mhla"),
            new DatasetSpec(5, "智能播放列表列表", "mhlp"),
            new DatasetSpec(6, "未知曲目列表", "mhlt"),
            new DatasetSpec(7, "保留数据集", null),
            new DatasetSpec(8, "艺术家列表", "mhli"),
            new DatasetSpec(9, "Genius 数据", null),
            new DatasetSpec(10, "未知曲目列表", "mhlt"),
        ],
        new DatasetTypeField(0x0C, 4));

    // ---- ArtworkDB ----

    public static ChunkSpec Mhfd { get; } = new(
        "mhfd", "ArtworkDB 根块", [132], 32, ChunkExtent.Length, ChunkBody.Children, [["mhsd"]]);

    public static ChunkSpec ArtworkMhsd { get; } = new(
        "mhsd", "ArtworkDB 数据集包装块", [96], 14, ChunkExtent.Length, ChunkBody.Dataset, []);

    public static ChunkSpec ArtworkMhli { get; } = new(
        "mhli", "插图列表", [92], 12, ChunkExtent.ChildCount, ChunkBody.Children, [["mhii"]]);

    public static ChunkSpec ArtworkMhii { get; } = new(
        "mhii", "插图条目", [152], 52, ChunkExtent.Length, ChunkBody.Children, [["mhod", "mhaf"]]);

    public static ChunkSpec Mhni { get; } = new(
        "mhni", "插图位置与尺寸", [76], 44, ChunkExtent.Length, ChunkBody.Children, [["mhod"]]);

    public static ChunkSpec Mhif { get; } = new(
        "mhif", "插图格式条目", [124], 24, ChunkExtent.Length, ChunkBody.Children, [["mhod"]]);

    public static ChunkSpec Mhaf { get; } = new(
        "mhaf", "附加插图数据", [12], 12, ChunkExtent.Length, ChunkBody.Opaque, []);

    public static ChunkSpec Mhlf { get; } = new(
        "mhlf", "插图格式列表", [92], 12, ChunkExtent.ChildCount, ChunkBody.Children, [["mhif"]]);

    public static ChunkSpec ArtworkMhla { get; } = new(
        "mhla", "照片相簿列表", [92], 12, ChunkExtent.ChildCount, ChunkBody.Children, [["mhba"]]);

    public static ChunkSpec Mhba { get; } = new(
        "mhba", "照片相簿", [148], 64, ChunkExtent.Length, ChunkBody.Children, [["mhod"], ["mhia"]]);

    public static ChunkSpec ArtworkMhia { get; } = new(
        "mhia", "照片相簿成员", [40], 20, ChunkExtent.Length, ChunkBody.Children, []);

    public static ChunkSpec ArtworkMhod { get; } = new(
        "mhod", "ArtworkDB 类型化数据对象", [24], 14, ChunkExtent.Length, ChunkBody.Mhod, []);

    public static DatabaseDefinition ArtworkDb { get; } = new(
        Mhfd,
        [
            Mhfd, ArtworkMhsd, ArtworkMhli, ArtworkMhii, Mhni, Mhif, Mhaf, Mhlf,
            ArtworkMhla, Mhba, ArtworkMhia, ArtworkMhod,
        ],
        [
            new DatasetSpec(1, "插图列表", "mhli"),
            new DatasetSpec(2, "照片相簿列表", "mhla"),
            new DatasetSpec(3, "插图格式列表", "mhlf"),
        ],
        new DatasetTypeField(0x0C, 2));
}
