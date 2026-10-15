using IOpenPod.IPodDb;
using IOpenPod.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IOpenPod.WinUI.Pages;

/// <summary>选择一个或多个曲目的对话框。</summary>
public sealed class TrackPickerDialog : ContentDialog
{
    private readonly ListView _list;
    private readonly IReadOnlyList<TrackRecord> _all;

    public TrackPickerDialog(IReadOnlyList<TrackRecord> tracks, XamlRoot? xamlRoot)
    {
        _all = tracks;

        Title = "选择要添加的曲目";
        PrimaryButtonText = "添加";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;
        XamlRoot = xamlRoot;

        var search = new TextBox { PlaceholderText = "搜索…", Margin = new Thickness(0, 0, 0, 8) };
        _list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Extended,
            Height = 380,
            Width = 520,
            ItemTemplate = (DataTemplate)Application.Current.Resources["TrackRowTemplate"],
        };

        search.TextChanged += (_, _) =>
        {
            var keyword = search.Text.Trim();
            var filtered = keyword.Length == 0
                ? _all
                : _all.Where(track =>
                        track.Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                        track.Artist.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                        track.Album.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
                    .ToArray();
            _list.ItemsSource = filtered.Select(track => new TrackRow(track)).ToArray();
        };

        _list.ItemsSource = tracks.Select(track => new TrackRow(track)).ToArray();

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(search);
        panel.Children.Add(_list);
        panel.Children.Add(new TextBlock
        {
            Text = "按住 Ctrl 或 Shift 可以多选。",
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            FontSize = 12,
        });

        Content = panel;
    }

    /// <summary>用户选择的曲目。</summary>
    public IReadOnlyList<TrackRecord> SelectedTracks =>
        _list.SelectedItems.OfType<TrackRow>().Select(row => row.Track).ToArray();
}

/// <summary>播放队列、播放历史与歌词。</summary>
public sealed class QueueDialog : ContentDialog
{
    public QueueDialog()
    {
        Title = "播放队列";
        CloseButtonText = "关闭";
        Width = 560;

        var tabs = new TabView
        {
            IsAddTabButtonVisible = false,
            Height = 420,
            Width = 520,
        };

        var playback = AppState.Current.Playback;

        tabs.TabItems.Add(BuildListTab("队列", playback.Queue));
        tabs.TabItems.Add(BuildListTab("历史", playback.History));
        tabs.TabItems.Add(BuildLyricsTab());

        Content = tabs;
    }

    private static TabViewItem BuildListTab(string header, IReadOnlyList<TrackRecord> tracks)
    {
        var list = new ListView
        {
            ItemTemplate = (DataTemplate)Application.Current.Resources["TrackRowTemplate"],
            SelectionMode = ListViewSelectionMode.Single,
        };

        list.ItemsSource = tracks.Select(track => new TrackRow(track)).ToArray();

        return new TabViewItem
        {
            Header = $"{header}（{tracks.Count}）",
            Content = list,
            IsClosable = false,
        };
    }

    private static TabViewItem BuildLyricsTab()
    {
        var current = AppState.Current.Playback.CurrentTrack;
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Padding = new Thickness(8),
            Text = current is null
                ? "当前没有正在播放的曲目。"
                : (current.Lyrics.Length > 0 ? current.Lyrics : "这首曲目没有内嵌歌词。"),
        };

        return new TabViewItem
        {
            Header = "歌词",
            Content = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            IsClosable = false,
        };
    }
}
