using IOpenPod.DeviceRegistry;
using IOpenPod.IPodDb;

namespace IOpenPod.App.Library;

/// <summary>一个专辑分组。</summary>
public sealed record AlbumGroup(
    string Key,
    string Album,
    string AlbumArtist,
    int Year,
    bool IsCompilation,
    IReadOnlyList<TrackRecord> Tracks)
{
    /// <summary>专辑总时长。</summary>
    public TimeSpan TotalDuration => TimeSpan.FromMilliseconds(Tracks.Sum(track => (double)track.LengthMs));

    public bool HasArtwork => Tracks.Any(track => track.HasArtwork);

    public string DisplayName => Album.Length > 0 ? Album : "未知专辑";

    public string Subtitle
    {
        get
        {
            var artist = AlbumArtist.Length > 0 ? AlbumArtist : "未知艺术家";
            return Year > 0 ? $"{artist} · {Year}" : artist;
        }
    }
}

/// <summary>一个艺术家分组。</summary>
public sealed record ArtistGroup(
    string Key,
    string Artist,
    IReadOnlyList<TrackRecord> Tracks,
    IReadOnlyList<AlbumGroup> Albums)
{
    public string DisplayName => Artist.Length > 0 ? Artist : "未知艺术家";

    public int AlbumCount => Albums.Count;
}

/// <summary>一个流派分组。</summary>
public sealed record GenreGroup(
    string Key,
    string Genre,
    IReadOnlyList<TrackRecord> Tracks,
    IReadOnlyList<AlbumGroup> Albums)
{
    public string DisplayName => Genre.Length > 0 ? Genre : "未知流派";
}

/// <summary>
/// 音乐库的浏览视图。
///
/// 全部索引在构造时一次性建立，之后只读，因此界面滚动与筛选都是 O(1) 查找。
/// </summary>
public sealed class LibraryView
{
    public LibraryView(ITunesDatabase database)
    {
        Database = database;

        Tracks = database.Tracks
            .OrderBy(track => SortKey(track.Title), StringComparer.CurrentCulture)
            .ThenBy(track => track.TrackNumber)
            .ToArray();

        Albums = Tracks
            .GroupBy(AlbumKey, StringComparer.CurrentCulture)
            .Select(group => new AlbumGroup(
                group.Key,
                group.First().Album,
                group.First().AlbumArtist.Length > 0 ? group.First().AlbumArtist : group.First().Artist,
                group.First().Year,
                group.Any(track => track.IsCompilation),
                group.OrderBy(track => track.DiscNumber)
                     .ThenBy(track => track.TrackNumber)
                     .ThenBy(track => SortKey(track.Title), StringComparer.CurrentCulture)
                     .ToArray()))
            .OrderBy(album => SortKey(album.Album), StringComparer.CurrentCulture)
            .ToArray();

        Artists = Tracks
            .GroupBy(track => track.Artist.Length > 0 ? track.Artist : string.Empty, StringComparer.CurrentCulture)
            .Select(group =>
            {
                var albums = Albums
                    .Where(album => group.Any(track => AlbumKey(track) == album.Key))
                    .ToArray();
                return new ArtistGroup(group.Key, group.Key, group.ToArray(), albums);
            })
            .OrderBy(artist => SortKey(artist.Artist), StringComparer.CurrentCulture)
            .ToArray();

        Genres = Tracks
            .GroupBy(track => track.Genre.Length > 0 ? track.Genre : string.Empty, StringComparer.CurrentCulture)
            .Select(group =>
            {
                var albums = Albums
                    .Where(album => group.Any(track => AlbumKey(track) == album.Key))
                    .ToArray();
                return new GenreGroup(group.Key, group.Key, group.ToArray(), albums);
            })
            .OrderBy(genre => SortKey(genre.Genre), StringComparer.CurrentCulture)
            .ToArray();

        AlbumsByKey = Albums.ToDictionary(album => album.Key, StringComparer.CurrentCulture);
    }

    public ITunesDatabase Database { get; }

    public IReadOnlyList<TrackRecord> Tracks { get; }

    public IReadOnlyList<AlbumGroup> Albums { get; }

    public IReadOnlyList<ArtistGroup> Artists { get; }

    public IReadOnlyList<GenreGroup> Genres { get; }

    public IReadOnlyDictionary<string, AlbumGroup> AlbumsByKey { get; }

    /// <summary>音乐库总时长。</summary>
    public TimeSpan TotalDuration => TimeSpan.FromMilliseconds(Tracks.Sum(track => (double)track.LengthMs));

    /// <summary>音乐库总大小（字节）。</summary>
    public long TotalSizeBytes => Tracks.Sum(track => (long)track.SizeBytes);

    /// <summary>把专辑归属键规范化为“专辑名 + 专辑艺术家”。</summary>
    public static string AlbumKey(TrackRecord track)
    {
        var album = track.Album.Trim();
        var artist = track.AlbumArtist.Length > 0 ? track.AlbumArtist : track.Artist;
        return $"{album}\u0000{artist.Trim()}";
    }

    /// <summary>排序键：忽略开头的冠词，并按不区分大小写比较。</summary>
    public static string SortKey(string value)
    {
        var text = (value ?? string.Empty).Trim();
        foreach (var article in new[] { "the ", "an ", "a " })
        {
            if (text.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                return text[article.Length..];
            }
        }

        return text;
    }

    /// <summary>按关键字搜索曲目（标题、艺术家、专辑、作曲者、流派）。</summary>
    public IReadOnlyList<TrackRecord> Search(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return Tracks;
        }

        var needle = keyword.Trim();
        return Tracks.Where(track =>
                Contains(track.Title, needle) ||
                Contains(track.Artist, needle) ||
                Contains(track.Album, needle) ||
                Contains(track.Composer, needle) ||
                Contains(track.Genre, needle) ||
                Contains(track.AlbumArtist, needle))
            .ToArray();
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
}
