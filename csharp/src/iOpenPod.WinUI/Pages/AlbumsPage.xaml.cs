using IOpenPod.App.Library;
using IOpenPod.IPodDb;
using IOpenPod.WinUI.Converters;
using IOpenPod.WinUI.Services;
using IOpenPod.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace IOpenPod.WinUI.Pages;

/// <summary>专辑浏览器：网格 + 专辑详情。</summary>
public sealed partial class AlbumsPage : Page, IRefreshable
{
    private AlbumGroup? _current;

    public AlbumsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    /// <summary>重新构建专辑网格。</summary>
    public void Refresh()
    {
        var view = AppState.Current.View;
        if (view is null)
        {
            AlbumGrid.ItemsSource = null;
            SummaryText.Text = "尚未加载设备。请先在“我的 iPod”页面加载音乐库。";
            EmptyPanel.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Collapsed;
            AlbumGrid.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        AlbumGrid.ItemsSource = view.Albums.Select(album => new AlbumCard(album)).ToArray();
        SummaryText.Text = $"共 {view.Albums.Count} 张专辑 · {view.Tracks.Count} 首曲目。" +
                           (_current is not null ? $" 当前查看：{_current.DisplayName}" : string.Empty);

        if (_current is not null)
        {
            var refreshed = view.AlbumsByKey.GetValueOrDefault(_current.Key);
            if (refreshed is not null)
            {
                ShowAlbum(refreshed);
                return;
            }
        }

        ShowGrid();
    }

    private void ShowGrid()
    {
        _current = null;
        AlbumGrid.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = AlbumGrid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowAlbum(AlbumGroup album)
    {
        _current = album;
        AlbumGrid.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;

        DetailAlbum.Text = album.DisplayName;
        DetailArtist.Text = album.Subtitle;
        DetailMeta.Text =
            $"{album.Tracks.Count} 首 · {DurationConverter.FormatDuration(album.TotalDuration)} · " +
            $"{BytesToSizeConverter.FormatSize(album.Tracks.Sum(track => (long)track.SizeBytes))}";

        DetailTracks.ItemsSource = album.Tracks.Select(track => new TrackRow(track, album.DisplayName)).ToArray();
        _ = LoadDetailCoverAsync(album);
    }

    private async Task LoadDetailCoverAsync(AlbumGroup album)
    {
        DetailCover.Source = null;
        var artwork = AppState.Current.Artwork;
        if (artwork is null)
        {
            return;
        }

        var representive = album.Tracks.FirstOrDefault(track => track.HasArtwork) ?? album.Tracks.FirstOrDefault();
        if (representive is null)
        {
            return;
        }

        var png = await artwork.GetCoverPngAsync(representive, 300);
        DetailCover.Source = await Helpers.ImageHelper.ToBitmapAsync(png);
    }

    private void OnAlbumClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumCard card)
        {
            ShowAlbum(card.Album);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowGrid();

    private IReadOnlyList<TrackRecord> SelectedTrackList() =>
        DetailTracks.SelectedItems.OfType<TrackRow>().Select(row => row.Track).ToArray();

    private IReadOnlyList<TrackRecord> CurrentAlbumTracks() => _current?.Tracks ?? [];

    private void OnPlayAlbumClick(object sender, RoutedEventArgs e) => TrackActions.PlayNow(CurrentAlbumTracks());

    private void OnEnqueueAlbumClick(object sender, RoutedEventArgs e) => TrackActions.Enqueue(CurrentAlbumTracks());

    private void OnTrackDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var tracks = SelectedTrackList();
        if (tracks.Count > 0)
        {
            TrackActions.PlayNow(tracks);
        }
    }

    private void OnTrackPlayClick(object sender, RoutedEventArgs e) => TrackActions.PlayNow(SelectedTrackList());

    private void OnTrackEnqueueClick(object sender, RoutedEventArgs e) => TrackActions.Enqueue(SelectedTrackList());

    private void OnTrackEditClick(object sender, RoutedEventArgs e) =>
        _ = TrackActions.EditAsync(SelectedTrackList(), XamlRoot);

    private void OnTrackRemoveClick(object sender, RoutedEventArgs e) =>
        _ = TrackActions.RemoveAsync(SelectedTrackList());
}
