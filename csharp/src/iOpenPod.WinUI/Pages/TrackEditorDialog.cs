using IOpenPod.App.Library;
using IOpenPod.App.Logging;
using IOpenPod.IPodDb;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IOpenPod.WinUI.Pages;

/// <summary>曲目元数据编辑器。支持单首编辑与多首批量应用。</summary>
public sealed class TrackEditorDialog : ContentDialog
{
    private readonly IReadOnlyList<TrackRecord> _tracks;
    private readonly TextBox _title = new();
    private readonly TextBox _artist = new();
    private readonly TextBox _albumArtist = new();
    private readonly TextBox _album = new();
    private readonly TextBox _genre = new();
    private readonly TextBox _composer = new();
    private readonly TextBox _comment = new();
    private readonly TextBox _grouping = new();
    private readonly TextBox _trackNumber = new();
    private readonly TextBox _totalTracks = new();
    private readonly TextBox _discNumber = new();
    private readonly TextBox _year = new();
    private readonly TextBox _bpm = new();
    private readonly TextBox _sortTitle = new();
    private readonly TextBox _sortArtist = new();
    private readonly TextBox _sortAlbum = new();
    private readonly TextBox _lyrics = new();
    private readonly ComboBox _rating = new();
    private readonly ComboBox _mediaType = new();
    private readonly CheckBox _compilation = new() { Content = "合辑" };
    private readonly CheckBox _skipShuffle = new() { Content = "随机播放时跳过" };
    private readonly CheckBox _rememberPosition = new() { Content = "记住播放位置" };

    private static readonly (uint Value, string Label)[] MediaTypes =
    [
        (0x00000001, "音频"),
        (0x00000002, "视频"),
        (0x00000004, "播客"),
        (0x00000006, "视频播客"),
        (0x00000008, "有声书"),
        (0x00000020, "音乐视频"),
        (0x00000040, "电视节目"),
        (0x00000000, "音频/视频"),
        (0x00100000, "语音备忘录"),
    ];

    public TrackEditorDialog(IReadOnlyList<TrackRecord> tracks, XamlRoot? xamlRoot)
    {
        _tracks = tracks;
        var first = tracks[0];

        Title = tracks.Count == 1
            ? $"编辑信息 — {first.DisplayFileName}"
            : $"批量编辑 {tracks.Count} 首曲目的信息";
        PrimaryButtonText = "保存";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;
        XamlRoot = xamlRoot;
        Width = 640;

        foreach (var star in new[] { "未评分", "★", "★★", "★★★", "★★★★", "★★★★★" })
        {
            _rating.Items.Add(star);
        }

        foreach (var (_, label) in MediaTypes)
        {
            _mediaType.Items.Add(label);
        }

        var mixed = tracks.Select(track => track.Title).Distinct().Count() > 1;
        Seed(_title, first.Title, mixed);
        Seed(_artist, first.Artist, tracks.Select(t => t.Artist).Distinct().Count() > 1);
        Seed(_albumArtist, first.AlbumArtist, tracks.Select(t => t.AlbumArtist).Distinct().Count() > 1);
        Seed(_album, first.Album, tracks.Select(t => t.Album).Distinct().Count() > 1);
        Seed(_genre, first.Genre, tracks.Select(t => t.Genre).Distinct().Count() > 1);
        Seed(_composer, first.Composer, tracks.Select(t => t.Composer).Distinct().Count() > 1);
        Seed(_comment, first.Comment, tracks.Select(t => t.Comment).Distinct().Count() > 1);
        Seed(_grouping, first.Grouping, tracks.Select(t => t.Grouping).Distinct().Count() > 1);
        Seed(_sortTitle, first.SortTitle, false);
        Seed(_sortArtist, first.SortArtist, false);
        Seed(_sortAlbum, first.SortAlbum, false);
        Seed(_lyrics, first.Lyrics, tracks.Select(t => t.Lyrics).Distinct().Count() > 1);

        SeedNumber(_trackNumber, first.TrackNumber, tracks.Select(t => t.TrackNumber).Distinct().Count() > 1);
        SeedNumber(_totalTracks, first.TotalTracks, tracks.Select(t => t.TotalTracks).Distinct().Count() > 1);
        SeedNumber(_discNumber, first.DiscNumber, tracks.Select(t => t.DiscNumber).Distinct().Count() > 1);
        SeedNumber(_year, first.Year, tracks.Select(t => t.Year).Distinct().Count() > 1);
        SeedNumber(_bpm, first.Bpm, tracks.Select(t => (int)t.Bpm).Distinct().Count() > 1);

        _rating.SelectedIndex = tracks.Select(track => track.StarRating).Distinct().Count() > 1
            ? -1
            : Math.Clamp(first.StarRating, 0, 5);

        var mediaIndex = Array.FindIndex(MediaTypes, item => item.Value == first.MediaType);
        _mediaType.SelectedIndex = tracks.Select(track => track.MediaType).Distinct().Count() > 1
            ? -1
            : Math.Max(0, mediaIndex);

        _compilation.IsThreeState = true;
        _compilation.IsChecked = tracks.Select(track => track.IsCompilation).Distinct().Count() > 1
            ? null
            : first.IsCompilation;

        _skipShuffle.IsThreeState = true;
        _skipShuffle.IsChecked = tracks.Select(track => track.SkipWhenShuffling).Distinct().Count() > 1
            ? null
            : first.SkipWhenShuffling;

        _rememberPosition.IsThreeState = true;
        _rememberPosition.IsChecked = tracks.Select(track => track.RememberPosition).Distinct().Count() > 1
            ? null
            : first.RememberPosition;

        Content = BuildContent();
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private static void Seed(TextBox box, string value, bool mixed)
    {
        box.Text = mixed ? string.Empty : value;
        box.PlaceholderText = mixed ? "（多个值，留空表示不修改）" : string.Empty;
        box.Header = null;
    }

    private static void SeedNumber(TextBox box, int value, bool mixed)
    {
        box.Text = mixed || value == 0 ? string.Empty : value.ToString();
        box.PlaceholderText = mixed ? "（多个值）" : string.Empty;
    }

    private UIElement BuildContent()
    {
        var tabs = new TabView
        {
            IsAddTabButtonVisible = false,
            Height = 460,
            Width = 600,
        };

        var basics = new StackPanel { Spacing = 10, Padding = new Thickness(4) };
        basics.Children.Add(Field("标题", _title));
        basics.Children.Add(Field("艺术家", _artist));
        basics.Children.Add(Field("专辑艺术家", _albumArtist));
        basics.Children.Add(Field("专辑", _album));
        basics.Children.Add(Field("流派", _genre));
        basics.Children.Add(Field("作曲者", _composer));

        var numbering = new StackPanel { Spacing = 10, Padding = new Thickness(4) };
        numbering.Children.Add(Field("音轨号", _trackNumber));
        numbering.Children.Add(Field("音轨总数", _totalTracks));
        numbering.Children.Add(Field("碟号", _discNumber));
        numbering.Children.Add(Field("年份", _year));
        numbering.Children.Add(Field("每分钟节拍（BPM）", _bpm));
        numbering.Children.Add(Field("评分", _rating));
        numbering.Children.Add(Field("媒体类型", _mediaType));
        numbering.Children.Add(_compilation);
        numbering.Children.Add(_skipShuffle);
        numbering.Children.Add(_rememberPosition);

        var advanced = new StackPanel { Spacing = 10, Padding = new Thickness(4) };
        advanced.Children.Add(Field("排序用标题", _sortTitle));
        advanced.Children.Add(Field("排序用艺术家", _sortArtist));
        advanced.Children.Add(Field("排序用专辑", _sortAlbum));
        advanced.Children.Add(Field("备注", _comment));
        advanced.Children.Add(Field("分组", _grouping));

        var lyrics = new StackPanel { Spacing = 10, Padding = new Thickness(4) };
        _lyrics.AcceptsReturn = true;
        _lyrics.TextWrapping = TextWrapping.Wrap;
        _lyrics.Height = 380;
        lyrics.Children.Add(_lyrics);

        tabs.TabItems.Add(new TabViewItem { Header = "基本信息", Content = Scroll(tabs, basics), IsClosable = false });
        tabs.TabItems.Add(new TabViewItem { Header = "编号与分类", Content = Scroll(tabs, numbering), IsClosable = false });
        tabs.TabItems.Add(new TabViewItem { Header = "排序与备注", Content = Scroll(tabs, advanced), IsClosable = false });
        tabs.TabItems.Add(new TabViewItem { Header = "歌词", Content = lyrics, IsClosable = false });

        return tabs;
    }

    private static ScrollViewer Scroll(TabView owner, UIElement content) =>
        new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    private static UIElement Field(string label, FrameworkElement control)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12 });
        panel.Children.Add(control);
        return panel;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var library = AppState.Current.Library;
        if (library is null)
        {
            args.Cancel = true;
            return;
        }

        var edit = new TrackEdit
        {
            Title = NullIfEmpty(_title.Text),
            Artist = NullIfEmpty(_artist.Text),
            AlbumArtist = NullIfEmpty(_albumArtist.Text),
            Album = NullIfEmpty(_album.Text),
            Genre = NullIfEmpty(_genre.Text),
            Composer = NullIfEmpty(_composer.Text),
            Comment = NullIfEmpty(_comment.Text),
            Grouping = NullIfEmpty(_grouping.Text),
            Lyrics = _lyrics.Text is { Length: > 0 } lyrics ? lyrics : null,
            SortTitle = NullIfEmpty(_sortTitle.Text),
            SortArtist = NullIfEmpty(_sortArtist.Text),
            SortAlbum = NullIfEmpty(_sortAlbum.Text),
            TrackNumber = ParsePositive(_trackNumber.Text),
            TotalTracks = ParsePositive(_totalTracks.Text),
            DiscNumber = ParsePositive(_discNumber.Text),
            Year = ParsePositive(_year.Text),
            Bpm = ParsePositive(_bpm.Text),
            StarRating = _rating.SelectedIndex >= 0 ? _rating.SelectedIndex : null,
            MediaType = _mediaType.SelectedIndex >= 0 ? MediaTypes[_mediaType.SelectedIndex].Value : null,
            IsCompilation = _compilation.IsChecked,
            SkipWhenShuffling = _skipShuffle.IsChecked,
            RememberPosition = _rememberPosition.IsChecked,
        };

        try
        {
            foreach (var track in _tracks)
            {
                library.UpdateTrack(track, edit);
            }
        }
        catch (Exception error)
        {
            AppLog.Error("编辑", "应用元数据修改失败", error);
            args.Cancel = true;
            return;
        }

        AppState.Current.RaiseLibraryChanged();
        MainWindow.SetStatus($"已修改 {_tracks.Count} 首曲目的信息，请在“我的 iPod”页面保存到设备。");

        _ = SaveAsync(library);
    }

    private static async Task SaveAsync(LibrarySession library)
    {
        var outcome = await library.SaveAsync();
        MainWindow.SetStatus(outcome.Message);
        if (outcome.Success)
        {
            AppState.Current.RaiseLibraryChanged();
        }
        else
        {
            await MainWindow.ShowErrorAsync("保存失败", outcome.Message);
        }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static int? ParsePositive(string value) =>
        int.TryParse(value.Trim(), out var number) ? Math.Max(0, number) : null;
}
