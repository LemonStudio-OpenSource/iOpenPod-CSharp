using IOpenPod.App.Logging;
using IOpenPod.App.Media;
using IOpenPod.IPodDb;

namespace IOpenPod.App.Sync;

/// <summary>宿主上的一个媒体文件。</summary>
public sealed record HostMediaItem(string HostPath, MediaTagInfo Tags)
{
    /// <summary>设备兼容性判定；未探测时为 <c>null</c>（按可直接播放处理）。</summary>
    public ConversionDecision? Conversion { get; init; }

    /// <summary>媒体探测结果；未探测时为 <c>null</c>。</summary>
    public MediaProbeResult? Probe { get; init; }

    /// <summary>该文件是否需要转码后才能进入设备。</summary>
    public bool NeedsConversion => Conversion?.RequiresWork ?? false;

    /// <summary>用于与设备曲目比对的稳定键。</summary>
    public string MatchKey => BuildKey(
        Tags.Title.Length > 0 ? Tags.Title : Path.GetFileNameWithoutExtension(HostPath),
        Tags.Artist,
        Tags.Album);

    public static string BuildKey(string title, string artist, string album) =>
        $"{Normalize(title)}\u0001{Normalize(artist)}\u0001{Normalize(album)}";

    /// <summary>取得设备曲目的比对键。</summary>
    public static string KeyForTrack(TrackRecord track) =>
        BuildKey(track.Title, track.Artist, track.Album);

    private static string Normalize(string value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();
}

/// <summary>同步计划中的一项变更。</summary>
public enum SyncChangeKind
{
    /// <summary>新增到设备。</summary>
    Add,

    /// <summary>替换媒体文件。</summary>
    Replace,

    /// <summary>只更新元数据。</summary>
    UpdateMetadata,

    /// <summary>从设备移除。</summary>
    Remove,
}

/// <summary>同步计划中的一项。</summary>
public sealed record SyncChange(
    SyncChangeKind Kind,
    HostMediaItem? Host,
    TrackRecord? Track,
    string Title,
    string Detail)
{
    /// <summary>用户在界面上看到的描述。</summary>
    public string KindName => Kind switch
    {
        SyncChangeKind.Add => "新增",
        SyncChangeKind.Replace => "更新文件",
        SyncChangeKind.UpdateMetadata => "更新信息",
        SyncChangeKind.Remove => "移除",
        _ => "未知",
    };

    /// <summary>该变更是否需要在写入前转码。</summary>
    public bool NeedsConversion => Host?.NeedsConversion ?? false;

    /// <summary>转码说明（无需转码时为空）。</summary>
    public string ConversionNote => Host?.Conversion is { RequiresWork: true } decision
        ? $"需要转码：{decision.Reason}"
        : string.Empty;
}

/// <summary>同步计划。</summary>
public sealed record SyncPlan(
    IReadOnlyList<SyncChange> Changes,
    int UnchangedCount,
    int HostFileCount,
    int DeviceTrackCount)
{
    public IReadOnlyList<SyncChange> Adds => Changes.Where(change => change.Kind == SyncChangeKind.Add).ToArray();

    public IReadOnlyList<SyncChange> Updates =>
        Changes.Where(change => change.Kind is SyncChangeKind.Replace or SyncChangeKind.UpdateMetadata).ToArray();

    public IReadOnlyList<SyncChange> Removes =>
        Changes.Where(change => change.Kind == SyncChangeKind.Remove).ToArray();

    public bool IsEmpty => Changes.Count == 0;

    public string Summary =>
        $"新增 {Adds.Count} 项，更新 {Updates.Count} 项，移除 {Removes.Count} 项，未变化 {UnchangedCount} 项。";
}

/// <summary>同步执行结果。</summary>
public sealed record SyncOutcome(
    bool Success,
    string Message,
    int Added,
    int Replaced,
    int MetadataUpdated,
    int Removed,
    long BytesCopied,
    IReadOnlyList<string> Failures);

/// <summary>同步进度。</summary>
public sealed record SyncProgress(string Phase, int Completed, int Total, string Current)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);

    /// <summary>面向用户的中文进度文本。</summary>
    public string Display => Current.Length == 0
        ? $"{Phase}（{Completed}/{Total}）"
        : $"{Phase}（{Completed}/{Total}）{Current}";
}
