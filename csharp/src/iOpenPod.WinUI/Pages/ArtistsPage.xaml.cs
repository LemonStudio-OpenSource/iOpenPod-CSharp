using IOpenPod.IPodDb;
using IOpenPod.WinUI.Converters;
using IOpenPod.WinUI.Services;
using IOpenPod.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace IOpenPod.WinUI.Pages;

/// <summary>艺术家浏览器。</summary>
public sealed partial class ArtistsPage : Page, IRefreshable
{
    private string _currentKey = string.Empty;

    public ArtistsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    /// <summary>重新构建艺术家列表。</summary>
    public void Refresh()
    {
        var view = AppState.Current.View;
        if (view is null)
        {
            GroupList.ItemsSource = null;
            SummaryText.Text = "尚未加载设备。请先在“我的 iPod”页面加载音乐库。";
            ReturnToList();
            return;
        }

        GroupList.ItemsSource = view.Artists
            .Select(artist => new GroupRow(
                artist.DisplayName,
                $"{artist.Tracks.Count} 首 · {artist.AlbumCount} 张专辑",
                artist.Tracks.FirstOrDefault(track => track.HasArtwork) ?? artist.Tracks.FirstOrDefault(),
                artist.Tracks))
            .ToArray();

        SummaryText.Text = $"共 {view.Artists.Count} 位艺术家 · {view.Albums.Count} 张专辑。";

        if (_currentKey.Length > 0)
        {
            var match = view.Artists.FirstOrDefault(artist => artist.Key == _currentKey);
            if (match is not null)
            {
                ShowArtist(match.DisplayName, match.Tracks);
                return;
            }
        }

        ReturnToList();
    }

    private void ReturnToList()
    {
        _currentKey = string.Empty;
        GroupList.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
    }

    private void ShowArtist(string name, IReadOnlyList<TrackRecord> tracks)
    {
        _currentKey = name;
        GroupList.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;

        DetailName.Text = name.Length > 0 ? name : "未知艺术家";
        DetailMeta.Text =
            $"{tracks.Count} 首 · {DurationConverter.FormatDuration(TimeSpan.FromMilliseconds(tracks.Sum(t => (double)t.LengthMs)))}";
        DetailTracks.ItemsSource = tracks.Select(track => new TrackRow(track)).ToArray();
    }

    private void OnGroupClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GroupRow row)
        {
            ShowArtist(row.Name, row.Tracks);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => ReturnToList();

    private IReadOnlyList<TrackRecord> SelectedTracks() =>
        DetailTracks.SelectedItems.OfType<TrackRow>().Select(row => row.Track).ToArray();

    private void OnTrackDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var tracks = SelectedTracks();
        if (tracks.Count > 0)
        {
            TrackActions.PlayNow(tracks);
        }
    }

    private void OnTrackPlayClick(object sender, RoutedEventArgs e) => TrackActions.PlayNow(SelectedTracks());

    private void OnTrackEnqueueClick(object sender, RoutedEventArgs e) => TrackActions.Enqueue(SelectedTracks());

    private void OnTrackEditClick(object sender, RoutedEventArgs e) =>
        _ = TrackActions.EditAsync(SelectedTracks(), XamlRoot);

    private void OnTrackRemoveClick(object sender, RoutedEventArgs e) =>
        _ = TrackActions.RemoveAsync(SelectedTracks());
}
