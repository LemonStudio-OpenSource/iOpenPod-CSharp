using System.ComponentModel;
using System.Runtime.CompilerServices;
using IOpenPod.App.Library;
using IOpenPod.IPodDb;
using IOpenPod.WinUI.Converters;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IOpenPod.WinUI.ViewModels;

/// <summary>实现属性变更通知的视图模型基类。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>曲目列表中的一行。</summary>
public sealed class TrackRow : ObservableObject
{
    private ImageSource? _cover;
    private bool _coverRequested;
    private bool _isPlaying;

    public TrackRow(TrackRecord track, string? albumLabel = null)
    {
        Track = track;
        Title = track.Title.Length > 0 ? track.Title : track.DeviceFileName;
        Artist = track.Artist.Length > 0 ? track.Artist : "未知艺术家";
        Album = albumLabel ?? (track.Album.Length > 0 ? track.Album : "未知专辑");
        Genre = track.Genre;
        DurationText = DurationConverter.FormatDuration(track.Duration);
        SizeText = BytesToSizeConverter.FormatSize(track.SizeBytes);
        RatingText = track.StarRating == 0 ? string.Empty : new string('★', track.StarRating);
        NumberText = track.TrackNumber > 0 ? track.TrackNumber.ToString() : string.Empty;
        YearText = track.Year > 0 ? track.Year.ToString() : string.Empty;
        TypeText = track.MediaTypeName;
        DetailText = $"{Artist} · {Album}" + (track.Year > 0 ? $" · {track.Year}" : string.Empty);
    }

    public TrackRecord Track { get; }

    public string Title { get; }

    public string Artist { get; }

    public string Album { get; }

    public string Genre { get; }

    public string DurationText { get; }

    public string SizeText { get; }

    public string RatingText { get; }

    public string NumberText { get; }

    public string YearText { get; }

    public string TypeText { get; }

    public string DetailText { get; }

    public bool HasArtwork => Track.HasArtwork;

    /// <summary>是否正在播放（列表用于高亮）。</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    /// <summary>专辑封面；首次访问时异步加载。</summary>
    public ImageSource? Cover
    {
        get
        {
            if (!_coverRequested)
            {
                _coverRequested = true;
                _ = LoadCoverAsync();
            }

            return _cover;
        }
    }

    private async Task LoadCoverAsync()
    {
        var artwork = AppState.Current.Artwork;
        if (artwork is null)
        {
            return;
        }

        try
        {
            var png = await artwork.GetCoverPngAsync(Track, 64);
            var bitmap = await Helpers.ImageHelper.ToBitmapAsync(png);
            if (bitmap is not null)
            {
                _cover = bitmap;
                Raise(nameof(Cover));
            }
        }
        catch (Exception)
        {
            // 封面加载失败时保持占位状态。
        }
    }
}

/// <summary>专辑网格中的一张卡片。</summary>
public sealed class AlbumCard : ObservableObject
{
    private ImageSource? _cover;
    private bool _coverRequested;

    public AlbumCard(AlbumGroup album)
    {
        Album = album;
        AlbumName = album.DisplayName;
        ArtistName = album.AlbumArtist.Length > 0 ? album.AlbumArtist : "未知艺术家";
        MetaText = $"{album.Tracks.Count} 首 · {DurationConverter.FormatDuration(album.TotalDuration)}" +
                   (album.Year > 0 ? $" · {album.Year}" : string.Empty);
    }

    public AlbumGroup Album { get; }

    public string AlbumName { get; }

    public string ArtistName { get; }

    public string MetaText { get; }

    public ImageSource? Cover
    {
        get
        {
            if (!_coverRequested)
            {
                _coverRequested = true;
                _ = LoadCoverAsync();
            }

            return _cover;
        }
    }

    private async Task LoadCoverAsync()
    {
        var artwork = AppState.Current.Artwork;
        if (artwork is null)
        {
            return;
        }

        var representive = Album.Tracks.FirstOrDefault(track => track.HasArtwork) ?? Album.Tracks.FirstOrDefault();
        if (representive is null)
        {
            return;
        }

        try
        {
            var png = await artwork.GetCoverPngAsync(representive, 200);
            var bitmap = await Helpers.ImageHelper.ToBitmapAsync(png);
            if (bitmap is not null)
            {
                _cover = bitmap;
                Raise(nameof(Cover));
            }
        }
        catch (Exception)
        {
            // 封面加载失败时保持占位状态。
        }
    }
}

/// <summary>艺术家或流派列表中的一项。</summary>
public sealed class GroupRow : ObservableObject
{
    private ImageSource? _cover;
    private bool _coverRequested;

    public GroupRow(string name, string meta, TrackRecord? representive, IReadOnlyList<TrackRecord> tracks)
    {
        Name = name.Length > 0 ? name : "未知";
        Meta = meta;
        Representive = representive;
        Tracks = tracks;
    }

    public string Name { get; }

    public string Meta { get; }

    public IReadOnlyList<TrackRecord> Tracks { get; }

    public TrackRecord? Representive { get; }

    public ImageSource? Cover
    {
        get
        {
            if (!_coverRequested)
            {
                _coverRequested = true;
                _ = LoadCoverAsync();
            }

            return _cover;
        }
    }

    private async Task LoadCoverAsync()
    {
        var artwork = AppState.Current.Artwork;
        if (artwork is null || Representive is null)
        {
            return;
        }

        try
        {
            var png = await artwork.GetCoverPngAsync(Representive, 96);
            var bitmap = await Helpers.ImageHelper.ToBitmapAsync(png);
            if (bitmap is not null)
            {
                _cover = bitmap;
                Raise(nameof(Cover));
            }
        }
        catch (Exception)
        {
            // 封面加载失败时保持占位状态。
        }
    }
}

/// <summary>同步计划中的一项。</summary>
public sealed class SyncChangeRow : ObservableObject
{
    private bool _isSelected;

    public SyncChangeRow(IOpenPod.App.Sync.SyncChange change, bool selected)
    {
        Change = change;
        _isSelected = selected;
        Title = change.Title;
        KindText = change.KindName;
        Detail = change.Detail;
    }

    public IOpenPod.App.Sync.SyncChange Change { get; }

    public string Title { get; }

    public string KindText { get; }

    public string Detail { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

/// <summary>备份快照列表中的一项。</summary>
public sealed class BackupRow
{
    public BackupRow(IOpenPod.App.Backup.BackupSnapshot snapshot)
    {
        Snapshot = snapshot;
        Name = snapshot.Name;
        Display = snapshot.Display;
        Path = snapshot.Directory;
    }

    public IOpenPod.App.Backup.BackupSnapshot Snapshot { get; }

    public string Name { get; }

    public string Display { get; }

    public string Path { get; }
}

/// <summary>日志列表中的一项。</summary>
public sealed class LogRow
{
    public LogRow(IOpenPod.App.Logging.LogEntry entry)
    {
        Entry = entry;
        Time = entry.Timestamp.ToString("HH:mm:ss");
        Level = entry.LevelName;
        Category = entry.Category;
        Message = entry.Message;
        BrushKey = entry.Level switch
        {
            IOpenPod.App.Logging.LogLevel.Error => "SystemFillColorCriticalBrush",
            IOpenPod.App.Logging.LogLevel.Warning => "SystemFillColorCautionBrush",
            IOpenPod.App.Logging.LogLevel.Debug => "TextFillColorTertiaryBrush",
            _ => "TextFillColorPrimaryBrush",
        };
    }

    public IOpenPod.App.Logging.LogEntry Entry { get; }

    public string Time { get; }

    public string Level { get; }

    public string Category { get; }

    public string Message { get; }

    public string BrushKey { get; }
}
