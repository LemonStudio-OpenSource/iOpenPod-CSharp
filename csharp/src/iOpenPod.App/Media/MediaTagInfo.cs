namespace IOpenPod.App.Media;

/// <summary>从宿主媒体文件中读出的标签与基础技术参数。</summary>
public sealed record MediaTagInfo
{
    public string Title { get; init; } = string.Empty;

    public string Artist { get; init; } = string.Empty;

    public string Album { get; init; } = string.Empty;

    public string AlbumArtist { get; init; } = string.Empty;

    public string Genre { get; init; } = string.Empty;

    public string Composer { get; init; } = string.Empty;

    public string Comment { get; init; } = string.Empty;

    public string Grouping { get; init; } = string.Empty;

    public string Lyrics { get; init; } = string.Empty;

    public int TrackNumber { get; init; }

    public int TotalTracks { get; init; }

    public int DiscNumber { get; init; }

    public int TotalDiscs { get; init; }

    public int Year { get; init; }

    public int Bpm { get; init; }

    public bool IsCompilation { get; init; }

    /// <summary>媒体时长。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>文件大小（字节）。</summary>
    public long SizeBytes { get; init; }

    /// <summary>平均比特率（kbps）。</summary>
    public int BitrateKbps { get; init; }

    /// <summary>采样率（Hz）。</summary>
    public int SampleRate { get; init; }

    /// <summary>声道数。</summary>
    public int Channels { get; init; }

    /// <summary>是否为视频文件。</summary>
    public bool IsVideo { get; init; }

    /// <summary>内嵌封面图片的原始字节；没有时为 <c>null</c>。</summary>
    public byte[]? CoverImage { get; init; }

    /// <summary>内嵌封面的 MIME 类型。</summary>
    public string CoverMimeType { get; init; } = string.Empty;

    /// <summary>是否读到了任何标签信息。</summary>
    public bool HasAnyTag =>
        Title.Length > 0 || Artist.Length > 0 || Album.Length > 0 || Genre.Length > 0;

    /// <summary>用文件名推断的标题（标签缺失时的回退）。</summary>
    public string EffectiveTitle(string hostPath) =>
        Title.Length > 0 ? Title : Path.GetFileNameWithoutExtension(hostPath);
}
