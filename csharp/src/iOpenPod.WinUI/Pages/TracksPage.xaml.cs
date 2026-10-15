using IOpenPod.App.Logging;
using IOpenPod.App.Settings;
using IOpenPod.IPodDb;
using IOpenPod.WinUI.Converters;
using IOpenPod.WinUI.Services;
using IOpenPod.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace IOpenPod.WinUI.Pages;

/// <summary>全部曲目页面。</summary>
public sealed partial class TracksPage : Page, IRefreshable
{
    private static readonly (TrackSortField Field, string Label)[] SortOptions =
    [
        (TrackSortField.Title, "标题"),
        (TrackSortField.Artist, "艺术家"),
        (TrackSortField.Album, "专辑"),
        (TrackSortField.Genre, "流派"),
        (TrackSortField.TrackNumber, "音轨号"),
        (TrackSortField.Year, "年份"),
        (TrackSortField.Duration, "时长"),
        (TrackSortField.Rating, "评分"),
        (TrackSortField.DateAdded, "添加时间"),
        (TrackSortField.PlayCount, "播放次数"),
    ];

    private bool _initializing;

    public TracksPage()
    {
        InitializeComponent();
        _initializing = true;
        foreach (var (field, label) in SortOptions)
        {
            SortBox.Items.Add(new ComboBoxItem { Content = label, Tag = field });
        }

        _initializing = false;

        Loaded += (_, _) => Refresh();
    }

    /// <summary>重新构建列表。</summary>
    public void Refresh()
    {
        var view = AppState.Current.View;
        if (view is null)
        {
            TrackList.ItemsSource = null;
            SummaryText.Text = "尚未加载设备。请先在“我的 iPod”页面加载音乐库。";
            EmptyPanel.Visibility = Visibility.Visible;
            EmptyText.Text = "还没有加载任何设备。";
            return;
        }

        EmptyPanel.Visibility = Visibility.Collapsed;

        var settings = AppState.Current.Settings;
        _initializing = true;
        try
        {
            foreach (var item in SortBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is TrackSortField field && field == settings.SortField)
                {
                    SortBox.SelectedItem = item;
                }
            }

            SortDirectionToggle.IsChecked = settings.SortDescending;
            SortDirectionIcon.Glyph = settings.SortDescending ? "\uE74B" : "\uE74A";
        }
        finally
        {
            _initializing = false;
        }

        var keyword = SearchBox.Text;
        var filtered = view.Search(keyword);
        var sorted = ApplySort(filtered, settings.SortField, settings.SortDescending);
        TrackList.ItemsSource = sorted.Select(track => new TrackRow(track)).ToArray();

        SummaryText.Text =
            $"{view.Tracks.Count} 首曲目 · {view.Albums.Count} 张专辑 · " +
            $"{DurationConverter.FormatDuration(view.TotalDuration)} · " +
            $"{BytesToSizeConverter.FormatSize(view.TotalSizeBytes)}" +
            (filtered.Count != view.Tracks.Count ? $"（已筛选出 {filtered.Count} 首）" : string.Empty);
    }

    private static IReadOnlyList<TrackRecord> ApplySort(
        IReadOnlyList<TrackRecord> tracks,
        TrackSortField field,
        bool descending)
    {
        IEnumerable<TrackRecord> sorted = field switch
        {
            TrackSortField.Artist => tracks
                .OrderBy(track => track.Artist, StringComparer.CurrentCulture)
                .ThenBy(track => track.Album, StringComparer.CurrentCulture)
                .ThenBy(track => track.TrackNumber),
            TrackSortField.Album => tracks
                .OrderBy(track => track.Album, StringComparer.CurrentCulture)
                .ThenBy(track => track.DiscNumber)
                .ThenBy(track => track.TrackNumber),
            TrackSortField.Genre => tracks
                .OrderBy(track => track.Genre, StringComparer.CurrentCulture)
                .ThenBy(track => track.Artist, StringComparer.CurrentCulture),
            TrackSortField.TrackNumber => tracks
                .OrderBy(track => track.TrackNumber)
                .ThenBy(track => track.Title, StringComparer.CurrentCulture),
            TrackSortField.Year => tracks
                .OrderBy(track => track.Year)
                .ThenBy(track => track.Album, StringComparer.CurrentCulture),
            TrackSortField.Duration => tracks.OrderBy(track => track.LengthMs),
            TrackSortField.Rating => tracks.OrderBy(track => track.Rating),
            TrackSortField.DateAdded => tracks.OrderBy(track => track.DateAdded),
            TrackSortField.PlayCount => tracks.OrderBy(track => track.PlayCount),
            _ => tracks
                .OrderBy(track => track.Title, StringComparer.CurrentCulture)
                .ThenBy(track => track.Artist, StringComparer.CurrentCulture),
        };

        return (descending ? sorted.Reverse() : sorted).ToArray();
    }

    private IReadOnlyList<TrackRecord> SelectedTracks() =>
        TrackList.SelectedItems.OfType<TrackRow>().Select(row => row.Track).ToArray();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || SortBox.SelectedItem is not ComboBoxItem item || item.Tag is not TrackSortField field)
        {
            return;
        }

        AppState.Current.Settings.SortField = field;
        AppState.Current.Settings.Save();
        Refresh();
    }

    private void OnSortDirectionClick(object sender, RoutedEventArgs e)
    {
        AppState.Current.Settings.SortDescending = SortDirectionToggle.IsChecked == true;
        AppState.Current.Settings.Save();
        SortDirectionIcon.Glyph = SortDirectionToggle.IsChecked == true ? "\uE74B" : "\uE74A";
        Refresh();
    }

    private void OnTrackSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = TrackList.SelectedItems.Count;
        MainWindow.SetStatus(count == 0 ? "未选择曲目。" : $"已选择 {count} 首曲目。");
    }

    private void OnTrackDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var tracks = SelectedTracks();
        if (tracks.Count == 0)
        {
            return;
        }

        switch (AppState.Current.Settings.DoubleClickAction)
        {
            case DoubleClickAction.Edit:
                _ = TrackActions.EditAsync(tracks, XamlRoot);
                break;
            case DoubleClickAction.PlayNext:
                TrackActions.PlayNext(tracks);
                break;
            case DoubleClickAction.PlayNow:
                TrackActions.PlayNow(tracks);
                break;
            default:
                TrackActions.Enqueue(tracks);
                break;
        }
    }

    private void OnPlaySelectedClick(object sender, RoutedEventArgs e) => TrackActions.PlayNow(SelectedTracks());

    private void OnPlayNextClick(object sender, RoutedEventArgs e) => TrackActions.PlayNext(SelectedTracks());

    private void OnEnqueueClick(object sender, RoutedEventArgs e) => TrackActions.Enqueue(SelectedTracks());

    private void OnEditClick(object sender, RoutedEventArgs e) =>
        _ = TrackActions.EditAsync(SelectedTracks(), XamlRoot);

    private void OnAddToPlaylistClick(object sender, RoutedEventArgs e) =>
        _ = TrackActions.AddToPlaylistAsync(SelectedTracks(), XamlRoot);

    private void OnExportClick(object sender, RoutedEventArgs e) =>
        _ = TrackActions.ExportAsync(SelectedTracks());

    private void OnRemoveClick(object sender, RoutedEventArgs e) =>
        _ = TrackActions.RemoveAsync(SelectedTracks());
}
